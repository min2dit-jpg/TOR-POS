using System.Security.Cryptography;
using System.Text;
using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed record RestaurantOperatorSessionLogin(
    string SessionToken,
    string Username,
    DateTimeOffset ExpiresAt);

public sealed class RestaurantOperatorSessionService
{
    private static readonly TimeSpan SessionLifetime =
        TimeSpan.FromHours(8);
    private static readonly TimeSpan LastSeenWriteInterval =
        TimeSpan.FromMinutes(1);

    private readonly SqliteDatabase _db;
    private readonly RestaurantEntitlementService _entitlements;
    private readonly IAuthenticationService _authentication;
    private readonly Dictionary<string, DateTimeOffset> _lastSeenWrites =
        new(StringComparer.Ordinal);
    private readonly object _lastSeenGate = new();

    public RestaurantOperatorSessionService(
        SqliteDatabase db,
        RestaurantEntitlementService entitlements,
        IAuthenticationService authentication)
    {
        _db = db;
        _entitlements = entitlements;
        _authentication = authentication;
    }

    public async Task<RestaurantOperatorSessionLogin> LoginAsync(
        string deviceId,
        string username,
        string pin,
        CancellationToken ct = default)
    {
        _entitlements.Require(
            RestaurantFeature.HandheldBestellung);

        deviceId = NormalizeRequired(
            deviceId,
            100,
            nameof(deviceId));

        var login = await _authentication.LoginWithPinAsync(
            (username ?? "").Trim(),
            (pin ?? "").Trim(),
            ct);

        if (!login.Success ||
            login.User is null ||
            !login.User.Can(UserPermissions.Sale))
        {
            throw new UnauthorizedAccessException(
                "Bediener/PIN ist ungültig oder nicht für Verkauf freigegeben.");
        }

        var token = Convert.ToHexString(
            RandomNumberGenerator.GetBytes(32));
        var tokenHash = Hash(token);
        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(SessionLifetime);

        await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = c.BeginTransaction();

            await using (var cleanup = c.CreateCommand())
            {
                cleanup.Transaction = tx;
                cleanup.CommandText = """
                    DELETE FROM restaurant_operator_sessions
                    WHERE expires_at < $cutoff
                       OR (revoked_at IS NOT NULL AND revoked_at < $cutoff);
                    """;
                cleanup.Parameters.AddWithValue(
                    "$cutoff",
                    now.AddDays(-7).ToString("O"));
                await cleanup.ExecuteNonQueryAsync(ct);
            }

            // Keep one current shift session per operator/device. Old sessions
            // are explicitly revoked instead of silently remaining usable.
            await using (var revoke = c.CreateCommand())
            {
                revoke.Transaction = tx;
                revoke.CommandText = """
                    UPDATE restaurant_operator_sessions
                    SET revoked_at=$now
                    WHERE device_id=$device
                      AND user_id=$user
                      AND revoked_at IS NULL;
                    """;
                revoke.Parameters.AddWithValue("$now", now.ToString("O"));
                revoke.Parameters.AddWithValue("$device", deviceId);
                revoke.Parameters.AddWithValue("$user", login.User.Id);
                await revoke.ExecuteNonQueryAsync(ct);
            }

            await using (var insert = c.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO restaurant_operator_sessions(
                        id,device_id,user_id,username,token_hash,is_admin,
                        permissions,created_at,expires_at,last_seen_at,revoked_at)
                    VALUES(
                        $id,$device,$user,$username,$token,$admin,
                        $permissions,$now,$expires,$now,NULL);
                    """;
                insert.Parameters.AddWithValue(
                    "$id",
                    Guid.NewGuid().ToString("N"));
                insert.Parameters.AddWithValue("$device", deviceId);
                insert.Parameters.AddWithValue("$user", login.User.Id);
                insert.Parameters.AddWithValue("$username", login.User.Username);
                insert.Parameters.AddWithValue("$token", tokenHash);
                insert.Parameters.AddWithValue("$admin", login.User.IsAdmin ? 1 : 0);
                insert.Parameters.AddWithValue(
                    "$permissions",
                    (long)login.User.Permissions);
                insert.Parameters.AddWithValue("$now", now.ToString("O"));
                insert.Parameters.AddWithValue("$expires", expires.ToString("O"));
                await insert.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        });

        return new RestaurantOperatorSessionLogin(
            token,
            login.User.Username,
            expires);
    }

    public async Task<AuthenticatedUser> RequireAsync(
        string deviceId,
        string sessionToken,
        CancellationToken ct = default)
    {
        _entitlements.Require(
            RestaurantFeature.HandheldBestellung);

        deviceId = NormalizeRequired(
            deviceId,
            100,
            nameof(deviceId));
        sessionToken = NormalizeRequired(
            sessionToken,
            128,
            nameof(sessionToken));

        var tokenHash = Hash(sessionToken);
        var now = DateTimeOffset.UtcNow;

        long userId;
        string username;
        bool isAdmin;
        UserPermissions permissions;
        DateTimeOffset expiresAt;

        await using (var c = _db.OpenReadConnection())
        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT
                    s.user_id,
                    u.username,
                    u.is_active,
                    u.must_change_password,
                    u.is_admin,
                    COALESCE(p.credentials_configured,0),
                    COALESCE(p.permissions,0),
                    s.expires_at
                FROM restaurant_operator_sessions s
                JOIN users u
                  ON u.id=s.user_id
                LEFT JOIN user_permissions p
                  ON p.user_id=u.id
                WHERE s.device_id=$device
                  AND s.token_hash=$token
                  AND s.revoked_at IS NULL
                LIMIT 1;
                """;
            q.Parameters.AddWithValue("$device", deviceId);
            q.Parameters.AddWithValue("$token", tokenHash);

            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
            {
                throw new UnauthorizedAccessException(
                    "Bediener-Sitzung ist ungültig.");
            }

            userId = r.GetInt64(0);
            username = r.GetString(1);
            var active = r.GetInt32(2) == 1;
            var mustChange = r.GetInt32(3) == 1;
            isAdmin = r.GetInt32(4) == 1;
            var configured = r.GetInt32(5) == 1;
            permissions =
                (UserPermissions)r.GetInt64(6);
            expiresAt = DateTimeOffset.Parse(
                r.GetString(7));

            var canSell =
                active &&
                !mustChange &&
                (isAdmin ||
                 (configured &&
                  (permissions & UserPermissions.Sale) ==
                      UserPermissions.Sale));

            if (!canSell)
            {
                throw new UnauthorizedAccessException(
                    "Bediener ist nicht mehr für Verkauf freigegeben.");
            }
        }

        if (expiresAt <= now)
        {
            throw new UnauthorizedAccessException(
                "Bediener-Sitzung ist abgelaufen.");
        }

        if (ShouldTouch(
                deviceId,
                tokenHash,
                now))
        {
            await TouchAsync(
                deviceId,
                tokenHash,
                now,
                ct);
        }

        return new AuthenticatedUser(
            userId,
            username,
            isAdmin ? "ADMIN" : "MITARBEITER",
            isAdmin,
            MustChangePassword: false,
            permissions,
            IsTraining: false);
    }

    public async Task LogoutAsync(
        string deviceId,
        string sessionToken,
        CancellationToken ct = default)
    {
        deviceId = NormalizeRequired(
            deviceId,
            100,
            nameof(deviceId));
        sessionToken = NormalizeRequired(
            sessionToken,
            128,
            nameof(sessionToken));
        var tokenHash = Hash(sessionToken);

        await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                UPDATE restaurant_operator_sessions
                SET revoked_at=$now
                WHERE device_id=$device
                  AND token_hash=$token
                  AND revoked_at IS NULL;
                """;
            q.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            q.Parameters.AddWithValue("$device", deviceId);
            q.Parameters.AddWithValue("$token", tokenHash);
            await q.ExecuteNonQueryAsync(ct);
        });

        lock (_lastSeenGate)
        {
            _lastSeenWrites.Remove(
                deviceId + "\u001f" + tokenHash);
        }
    }

    private bool ShouldTouch(
        string deviceId,
        string token,
        DateTimeOffset now)
    {
        var key = deviceId + "\u001f" + token;

        lock (_lastSeenGate)
        {
            if (_lastSeenWrites.TryGetValue(
                    key,
                    out var previous) &&
                now - previous <
                    LastSeenWriteInterval)
            {
                return false;
            }

            _lastSeenWrites[key] = now;
            return true;
        }
    }

    private Task TouchAsync(
        string deviceId,
        string tokenHash,
        DateTimeOffset now,
        CancellationToken ct) =>
        IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                UPDATE restaurant_operator_sessions
                SET last_seen_at=$now
                WHERE device_id=$device
                  AND token_hash=$token
                  AND revoked_at IS NULL;
                """;
            q.Parameters.AddWithValue("$now", now.ToString("O"));
            q.Parameters.AddWithValue("$device", deviceId);
            q.Parameters.AddWithValue("$token", tokenHash);
            await q.ExecuteNonQueryAsync(ct);
        });

    private static string Hash(string value) =>
        Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(value)));

    private static string NormalizeRequired(
        string? value,
        int maxLength,
        string parameterName)
    {
        var normalized = (value ?? "").Trim();

        if (normalized.Length == 0 ||
            normalized.Length > maxLength)
        {
            throw new ArgumentException(
                $"{parameterName} ist ungültig.",
                parameterName);
        }

        return normalized;
    }
}
