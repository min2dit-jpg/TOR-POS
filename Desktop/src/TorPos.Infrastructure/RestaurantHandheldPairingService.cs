using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace TorPos.Infrastructure;

public sealed record RestaurantPairingCode(
    string Id,
    string Code,
    DateTimeOffset ExpiresAt);

public sealed record RestaurantPairedDevice(
    string DeviceId,
    string DisplayName,
    string DeviceToken,
    DateTimeOffset PairedAt);

public sealed record RestaurantHandheldDeviceInfo(
    string DeviceId,
    string DisplayName,
    DateTimeOffset PairedAt,
    DateTimeOffset LastSeenAt,
    bool IsActive);

public sealed class RestaurantHandheldPairingService
{
    private readonly SqliteDatabase _db;
    private readonly RestaurantEntitlementService _entitlements;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeenWrites =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan LastSeenWriteInterval =
        TimeSpan.FromSeconds(30);

    public RestaurantHandheldPairingService(
        SqliteDatabase db,
        RestaurantEntitlementService entitlements)
    {
        _db = db;
        _entitlements = entitlements;
    }

    public async Task<RestaurantPairingCode> CreatePairingCodeAsync(
        string actor,
        TimeSpan? lifetime = null,
        CancellationToken ct = default)
    {
        _entitlements.Require(
            TorPos.Core.RestaurantFeature.HandheldBestellung);

        actor = (actor ?? "").Trim();
        if (actor.Length == 0)
            throw new ArgumentException("Bediener fehlt.", nameof(actor));

        var ttl = lifetime ?? TimeSpan.FromMinutes(10);
        if (ttl <= TimeSpan.Zero || ttl > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(lifetime));

        var code = RandomNumberGenerator.GetInt32(100000, 1000000)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(ttl);

        await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = c.BeginTransaction();

            await using (var purge = c.CreateCommand())
            {
                purge.Transaction = tx;
                purge.CommandText = """
                    DELETE FROM restaurant_pairing_codes
                    WHERE expires_at < $now
                       OR consumed_at IS NOT NULL;
                    """;
                purge.Parameters.AddWithValue("$now", now.ToString("O"));
                await purge.ExecuteNonQueryAsync(ct);
            }

            await using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = """
                    INSERT INTO restaurant_pairing_codes(
                        id,code_hash,created_at,expires_at,created_by,
                        consumed_at,consumed_by_device)
                    VALUES($id,$hash,$created,$expires,$actor,NULL,'');
                    """;
                q.Parameters.AddWithValue("$id", id);
                q.Parameters.AddWithValue("$hash", Hash(code));
                q.Parameters.AddWithValue("$created", now.ToString("O"));
                q.Parameters.AddWithValue("$expires", expires.ToString("O"));
                q.Parameters.AddWithValue("$actor", actor);
                await q.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        });

        return new RestaurantPairingCode(id, code, expires);
    }

    public async Task<RestaurantPairedDevice> PairAsync(
        string pairingId,
        string pairingCode,
        string deviceId,
        string displayName,
        CancellationToken ct = default)
    {
        _entitlements.Require(
            TorPos.Core.RestaurantFeature.HandheldBestellung);

        pairingId = (pairingId ?? "").Trim();
        pairingCode = (pairingCode ?? "").Trim();
        deviceId = (deviceId ?? "").Trim();
        displayName = (displayName ?? "").Trim();

        if (pairingId.Length is < 16 or > 64 ||
            !pairingId.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                "Pairing-Sitzung ist ungültig.");
        }

        if (pairingCode.Length != 6 ||
            !pairingCode.All(char.IsDigit))
        {
            throw new InvalidOperationException(
                "Pairing-Code ist ungültig.");
        }

        if (deviceId.Length is < 4 or > 128)
            throw new ArgumentException(
                "Geräte-ID ist ungültig.",
                nameof(deviceId));

        if (displayName.Length == 0 ||
            displayName.Length > 120)
        {
            throw new ArgumentException(
                "Gerätename ist ungültig.",
                nameof(displayName));
        }

        var now = DateTimeOffset.UtcNow;
        var token = Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(32));
        var tokenHash = Hash(token);

        return await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = c.BeginTransaction();

            await using (var existingDevice = c.CreateCommand())
            {
                existingDevice.Transaction = tx;
                existingDevice.CommandText = """
                    SELECT is_active
                    FROM restaurant_handheld_devices
                    WHERE device_id=$id
                    LIMIT 1;
                    """;
                existingDevice.Parameters.AddWithValue(
                    "$id",
                    deviceId);

                var existing =
                    await existingDevice.ExecuteScalarAsync(ct);

                if (existing is not null)
                {
                    throw new InvalidOperationException(
                        "Diese Geräte-ID ist bereits registriert. " +
                        "Ein bestehendes oder deaktiviertes Gerät darf nicht durch Pairing überschrieben werden.");
                }
            }

            string? storedCodeHash = null;
            string? pairedBy = null;
            var failedAttempts = 0;

            await using (var find = c.CreateCommand())
            {
                find.Transaction = tx;
                find.CommandText = """
                    SELECT code_hash,created_by,failed_attempts
                    FROM restaurant_pairing_codes
                    WHERE id=$id
                      AND consumed_at IS NULL
                      AND expires_at >= $now
                    LIMIT 1;
                    """;
                find.Parameters.AddWithValue(
                    "$id",
                    pairingId);
                find.Parameters.AddWithValue(
                    "$now",
                    now.ToString("O"));

                await using var r =
                    await find.ExecuteReaderAsync(ct);

                if (await r.ReadAsync(ct))
                {
                    storedCodeHash = r.GetString(0);
                    pairedBy = r.GetString(1);
                    failedAttempts = r.GetInt32(2);
                }
            }

            if (storedCodeHash is null ||
                failedAttempts >= 5)
            {
                throw new InvalidOperationException(
                    "Pairing-Sitzung ist abgelaufen, gesperrt oder bereits verwendet.");
            }

            if (!FixedEquals(
                    storedCodeHash,
                    Hash(pairingCode)))
            {
                var nextAttempts =
                    Math.Min(5, failedAttempts + 1);

                await using var failed = c.CreateCommand();
                failed.Transaction = tx;
                failed.CommandText = """
                    UPDATE restaurant_pairing_codes
                    SET failed_attempts=$attempts,
                        consumed_at=CASE
                            WHEN $attempts >= 5 THEN $now
                            ELSE consumed_at
                        END,
                        consumed_by_device=CASE
                            WHEN $attempts >= 5 THEN 'FAILED_ATTEMPTS'
                            ELSE consumed_by_device
                        END
                    WHERE id=$id
                      AND consumed_at IS NULL;
                    """;
                failed.Parameters.AddWithValue(
                    "$attempts",
                    nextAttempts);
                failed.Parameters.AddWithValue(
                    "$now",
                    now.ToString("O"));
                failed.Parameters.AddWithValue(
                    "$id",
                    pairingId);
                await failed.ExecuteNonQueryAsync(ct);
                await tx.CommitAsync(ct);

                throw new InvalidOperationException(
                    nextAttempts >= 5
                        ? "Pairing-Code wurde nach fünf Fehlversuchen gesperrt."
                        : $"Pairing-Code ist ungültig. Noch {5 - nextAttempts} Versuch(e).");
            }

            await using (var consume = c.CreateCommand())
            {
                consume.Transaction = tx;
                consume.CommandText = """
                    UPDATE restaurant_pairing_codes
                    SET consumed_at=$now,
                        consumed_by_device=$device
                    WHERE id=$id
                      AND consumed_at IS NULL
                      AND failed_attempts < 5;
                    """;
                consume.Parameters.AddWithValue(
                    "$now",
                    now.ToString("O"));
                consume.Parameters.AddWithValue(
                    "$device",
                    deviceId);
                consume.Parameters.AddWithValue(
                    "$id",
                    pairingId);

                if (await consume.ExecuteNonQueryAsync(ct) != 1)
                {
                    throw new InvalidOperationException(
                        "Pairing-Sitzung wurde bereits verwendet oder gesperrt.");
                }
            }

            await using (var device = c.CreateCommand())
            {
                device.Transaction = tx;
                device.CommandText = """
                    INSERT INTO restaurant_handheld_devices(
                        device_id,display_name,token_hash,paired_at,paired_by,
                        last_seen_at,is_active)
                    VALUES($id,$name,$token,$now,$actor,$now,1);
                    """;
                device.Parameters.AddWithValue(
                    "$id",
                    deviceId);
                device.Parameters.AddWithValue(
                    "$name",
                    displayName);
                device.Parameters.AddWithValue(
                    "$token",
                    tokenHash);
                device.Parameters.AddWithValue(
                    "$now",
                    now.ToString("O"));
                device.Parameters.AddWithValue(
                    "$actor",
                    pairedBy ?? "");
                await device.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);

            return new RestaurantPairedDevice(
                deviceId,
                displayName,
                token,
                now);
        });
    }

    public async Task RequireAuthenticatedAsync(
        string deviceId,
        string deviceToken,
        CancellationToken ct = default)
    {
        _entitlements.Require(
            TorPos.Core.RestaurantFeature.HandheldBestellung);

        deviceId = (deviceId ?? "").Trim();
        deviceToken = (deviceToken ?? "").Trim();

        if (deviceId.Length == 0 || deviceToken.Length == 0)
            throw new UnauthorizedAccessException(
                "Handheld-Gerät ist nicht authentifiziert.");

        string? storedHash;
        await using (var c = _db.OpenReadConnection())
        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT token_hash
                FROM restaurant_handheld_devices
                WHERE device_id=$id AND is_active=1;
                """;
            q.Parameters.AddWithValue("$id", deviceId);
            storedHash = (string?)await q.ExecuteScalarAsync(ct);
        }

        if (storedHash is null ||
            !FixedEquals(
                storedHash,
                Hash(deviceToken)))
        {
            throw new UnauthorizedAccessException(
                "Handheld-Gerät ist nicht authentifiziert.");
        }

        var now = DateTimeOffset.UtcNow;
        if (!TryClaimLastSeenWrite(
                deviceId,
                now))
        {
            return;
        }

        try
        {
            await IoQueue.RunAsync(async () =>
            {
                await using var c = _db.OpenConnection();
                await using var touch = c.CreateCommand();
                touch.CommandText = """
                    UPDATE restaurant_handheld_devices
                    SET last_seen_at=$now
                    WHERE device_id=$id
                      AND is_active=1;
                    """;
                touch.Parameters.AddWithValue(
                    "$now",
                    now.ToString("O"));
                touch.Parameters.AddWithValue(
                    "$id",
                    deviceId);
                await touch.ExecuteNonQueryAsync(ct);
            });
        }
        catch
        {
            _lastSeenWrites.TryRemove(
                deviceId,
                out _);
            throw;
        }
    }

    private bool TryClaimLastSeenWrite(
        string deviceId,
        DateTimeOffset now)
    {
        while (true)
        {
            if (!_lastSeenWrites.TryGetValue(
                    deviceId,
                    out var previous))
            {
                if (_lastSeenWrites.TryAdd(
                        deviceId,
                        now))
                    return true;

                continue;
            }

            if (now - previous <
                LastSeenWriteInterval)
                return false;

            if (_lastSeenWrites.TryUpdate(
                    deviceId,
                    now,
                    previous))
                return true;
        }
    }

    public Task<IReadOnlyList<RestaurantHandheldDeviceInfo>> ListDevicesAsync(
        CancellationToken ct = default)
    {
        _entitlements.Require(
            TorPos.Core.RestaurantFeature.HandheldBestellung);

        return IoQueue.RunAsync<IReadOnlyList<RestaurantHandheldDeviceInfo>>(async () =>
        {
            var result = new List<RestaurantHandheldDeviceInfo>();
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                SELECT device_id,display_name,paired_at,last_seen_at,is_active
                FROM restaurant_handheld_devices
                ORDER BY is_active DESC,last_seen_at DESC,display_name;
                """;

            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                result.Add(new RestaurantHandheldDeviceInfo(
                    r.GetString(0),
                    r.GetString(1),
                    DateTimeOffset.Parse(r.GetString(2)),
                    DateTimeOffset.Parse(r.GetString(3)),
                    r.GetInt32(4) != 0));
            }

            return result;
        });
    }

    public Task DeactivateAsync(
        string deviceId,
        CancellationToken ct = default) =>
        IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                UPDATE restaurant_handheld_devices
                SET is_active=0
                WHERE device_id=$id;
                """;
            q.Parameters.AddWithValue("$id", (deviceId ?? "").Trim());
            await q.ExecuteNonQueryAsync(ct);
        });

    private static string Hash(string value) =>
        Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(value)));

    private static bool FixedEquals(
        string left,
        string right)
    {
        try
        {
            var a = Convert.FromHexString(left);
            var b = Convert.FromHexString(right);
            return a.Length == b.Length &&
                   CryptographicOperations.FixedTimeEquals(a, b);
        }
        catch
        {
            return false;
        }
    }
}
