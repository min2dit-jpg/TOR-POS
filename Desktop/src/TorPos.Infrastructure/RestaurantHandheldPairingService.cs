using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace TorPos.Infrastructure;

public sealed record RestaurantPairingCode(
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

    private static readonly object PairingAttemptGate = new();
    private static readonly Queue<DateTimeOffset> GlobalPairingAttempts = new();
    private static readonly Dictionary<string, Queue<DateTimeOffset>> PairingAttemptsByCode =
        new(StringComparer.Ordinal);
    private static readonly TimeSpan PairingAttemptWindow =
        TimeSpan.FromMinutes(1);
    private const int GlobalPairingAttemptLimit = 30;
    private const int PerCodePairingAttemptLimit = 5;

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

        return new RestaurantPairingCode(code, expires);
    }

    public async Task<RestaurantPairedDevice> PairAsync(
        string pairingCode,
        string deviceId,
        string displayName,
        CancellationToken ct = default)
    {
        _entitlements.Require(
            TorPos.Core.RestaurantFeature.HandheldBestellung);

        pairingCode = (pairingCode ?? "").Trim();
        deviceId = (deviceId ?? "").Trim();
        displayName = (displayName ?? "").Trim();

        if (pairingCode.Length != 6 || !pairingCode.All(char.IsDigit))
            throw new InvalidOperationException("Pairing-Code ist ungültig.");
        if (deviceId.Length is < 4 or > 128)
            throw new ArgumentException("Geräte-ID ist ungültig.", nameof(deviceId));
        if (displayName.Length == 0 || displayName.Length > 120)
            throw new ArgumentException("Gerätename ist ungültig.", nameof(displayName));

        RequirePairingAttemptBudget(pairingCode);

        var now = DateTimeOffset.UtcNow;
        var token = Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(32));
        var tokenHash = Hash(token);

        return await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = c.BeginTransaction();

            string? pairingId = null;
            string? pairedBy = null;

            await using (var find = c.CreateCommand())
            {
                find.Transaction = tx;
                find.CommandText = """
                    SELECT id,created_by
                    FROM restaurant_pairing_codes
                    WHERE code_hash=$hash
                      AND consumed_at IS NULL
                      AND expires_at >= $now
                    LIMIT 1;
                    """;
                find.Parameters.AddWithValue("$hash", Hash(pairingCode));
                find.Parameters.AddWithValue("$now", now.ToString("O"));

                await using var r = await find.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    pairingId = r.GetString(0);
                    pairedBy = r.GetString(1);
                }
            }

            if (pairingId is null)
                throw new InvalidOperationException(
                    "Pairing-Code ist abgelaufen, bereits verwendet oder ungültig.");

            // Only after proving knowledge of a valid one-time code may the
            // service reveal that a device identity already exists. The check
            // still happens before consume, so a duplicate identity never
            // burns the administrator-issued code.
            await using (var existingDevice = c.CreateCommand())
            {
                existingDevice.Transaction = tx;
                existingDevice.CommandText = """
                    SELECT COUNT(*)
                    FROM restaurant_handheld_devices
                    WHERE device_id=$device;
                    """;
                existingDevice.Parameters.AddWithValue("$device", deviceId);

                if (Convert.ToInt32(
                        await existingDevice.ExecuteScalarAsync(ct)) > 0)
                {
                    throw new InvalidOperationException(
                        "Geräte-ID ist bereits gekoppelt. Vor einer erneuten Kopplung muss eine neue Geräte-ID verwendet werden.");
                }
            }

            await using (var consume = c.CreateCommand())
            {
                consume.Transaction = tx;
                consume.CommandText = """
                    UPDATE restaurant_pairing_codes
                    SET consumed_at=$now,
                        consumed_by_device=$device
                    WHERE id=$id
                      AND consumed_at IS NULL;
                    """;
                consume.Parameters.AddWithValue("$now", now.ToString("O"));
                consume.Parameters.AddWithValue("$device", deviceId);
                consume.Parameters.AddWithValue("$id", pairingId);

                if (await consume.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException(
                        "Pairing-Code wurde bereits verwendet.");
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
                device.Parameters.AddWithValue("$id", deviceId);
                device.Parameters.AddWithValue("$name", displayName);
                device.Parameters.AddWithValue("$token", tokenHash);
                device.Parameters.AddWithValue("$now", now.ToString("O"));
                device.Parameters.AddWithValue("$actor", pairedBy ?? "");
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

    private static void RequirePairingAttemptBudget(
        string pairingCode)
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - PairingAttemptWindow;
        var codeKey = Hash(pairingCode);

        lock (PairingAttemptGate)
        {
            while (GlobalPairingAttempts.Count > 0 &&
                   GlobalPairingAttempts.Peek() < cutoff)
            {
                GlobalPairingAttempts.Dequeue();
            }

            foreach (var key in PairingAttemptsByCode.Keys.ToArray())
            {
                var queue = PairingAttemptsByCode[key];
                while (queue.Count > 0 &&
                       queue.Peek() < cutoff)
                {
                    queue.Dequeue();
                }

                if (queue.Count == 0)
                    PairingAttemptsByCode.Remove(key);
            }

            if (GlobalPairingAttempts.Count >=
                    GlobalPairingAttemptLimit)
            {
                throw new InvalidOperationException(
                    "Zu viele Pairing-Versuche. Bitte kurz warten und einen neuen Pairing-Code verwenden.");
            }

            if (!PairingAttemptsByCode.TryGetValue(
                    codeKey,
                    out var codeAttempts))
            {
                codeAttempts = new Queue<DateTimeOffset>();
                PairingAttemptsByCode[codeKey] = codeAttempts;
            }

            if (codeAttempts.Count >=
                    PerCodePairingAttemptLimit)
            {
                throw new InvalidOperationException(
                    "Zu viele Pairing-Versuche. Bitte kurz warten und einen neuen Pairing-Code verwenden.");
            }

            GlobalPairingAttempts.Enqueue(now);
            codeAttempts.Enqueue(now);
        }
    }

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
