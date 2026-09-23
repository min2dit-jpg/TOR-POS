using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed record RestaurantTerminalStatus(
    string TerminalId,
    string DisplayName,
    string TerminalType,
    DateTimeOffset LastSeenAt,
    string AppVersion,
    string MachineName,
    bool IsActive);

public sealed class RestaurantTerminalRegistry
{
    private readonly SqliteDatabase _db;
    private readonly RestaurantEntitlementService _entitlements;

    public RestaurantTerminalRegistry(
        SqliteDatabase db,
        RestaurantEntitlementService entitlements)
    {
        _db = db;
        _entitlements = entitlements;
    }

    public Task RegisterOrHeartbeatAsync(
        string terminalId,
        string displayName,
        string terminalType,
        string appVersion,
        string machineName,
        CancellationToken ct = default)
    {
        _entitlements.Require(RestaurantFeature.MehrereKassen);

        terminalId = NormalizeRequired(terminalId, 100, nameof(terminalId));
        displayName = NormalizeRequired(displayName, 120, nameof(displayName));
        terminalType = (terminalType ?? "").Trim().ToUpperInvariant();
        appVersion = (appVersion ?? "").Trim();
        machineName = (machineName ?? "").Trim();

        if (terminalType is not ("KASSE" or "HANDHELD" or "KDS"))
            throw new ArgumentOutOfRangeException(nameof(terminalType));

        return IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                INSERT INTO restaurant_terminals(
                    terminal_id,display_name,terminal_type,last_seen_at,
                    app_version,machine_name,is_active)
                VALUES($id,$name,$type,$seen,$version,$machine,1)
                ON CONFLICT(terminal_id) DO UPDATE SET
                    display_name=excluded.display_name,
                    terminal_type=excluded.terminal_type,
                    last_seen_at=excluded.last_seen_at,
                    app_version=excluded.app_version,
                    machine_name=excluded.machine_name,
                    is_active=1;
                """;
            q.Parameters.AddWithValue("$id", terminalId);
            q.Parameters.AddWithValue("$name", displayName);
            q.Parameters.AddWithValue("$type", terminalType);
            q.Parameters.AddWithValue("$seen", DateTimeOffset.UtcNow.ToString("O"));
            q.Parameters.AddWithValue("$version", appVersion);
            q.Parameters.AddWithValue("$machine", machineName);
            await q.ExecuteNonQueryAsync(ct);
        });
    }

    public async Task<IReadOnlyList<RestaurantTerminalStatus>> ListAsync(
        CancellationToken ct = default)
    {
        _entitlements.Require(RestaurantFeature.MehrereKassen);

        var result = new List<RestaurantTerminalStatus>();
        await using var c = _db.OpenReadConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT terminal_id,display_name,terminal_type,last_seen_at,
                   app_version,machine_name,is_active
            FROM restaurant_terminals
            ORDER BY terminal_type,display_name,terminal_id;
            """;

        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            result.Add(new RestaurantTerminalStatus(
                r.GetString(0),
                r.GetString(1),
                r.GetString(2),
                DateTimeOffset.Parse(r.GetString(3)),
                r.GetString(4),
                r.GetString(5),
                r.GetInt32(6) == 1));
        }

        return result;
    }

    public Task DeactivateAsync(
        string terminalId,
        CancellationToken ct = default)
    {
        _entitlements.Require(RestaurantFeature.MehrereKassen);
        terminalId = NormalizeRequired(terminalId, 100, nameof(terminalId));

        return IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                UPDATE restaurant_terminals
                SET is_active=0
                WHERE terminal_id=$id;
                """;
            q.Parameters.AddWithValue("$id", terminalId);
            await q.ExecuteNonQueryAsync(ct);
        });
    }

    private static string NormalizeRequired(
        string? value,
        int maxLength,
        string parameterName)
    {
        var normalized = (value ?? "").Trim();
        if (normalized.Length == 0 || normalized.Length > maxLength)
            throw new ArgumentException(
                $"{parameterName} ist ungültig.",
                parameterName);
        return normalized;
    }
}
