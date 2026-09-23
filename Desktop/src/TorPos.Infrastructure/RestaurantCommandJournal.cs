using Microsoft.Data.Sqlite;

namespace TorPos.Infrastructure;

public enum RestaurantCommandClaimState
{
    New,
    Recovered,
    InProgress,
    Completed,
    Failed
}

public sealed record RestaurantCommandClaim(
    RestaurantCommandClaimState State,
    long ResultSessionVersion,
    string ErrorText);

public sealed class RestaurantCommandJournal
{
    private readonly SqliteDatabase _db;
    private readonly string _ownerId =
        Guid.NewGuid().ToString("N");

    public RestaurantCommandJournal(SqliteDatabase db)
    {
        _db = db;
    }

    public Task<RestaurantCommandClaim> BeginAsync(
        string deviceId,
        string commandId,
        string commandType,
        string requestHash,
        string sessionId,
        CancellationToken ct = default)
    {
        deviceId = Normalize(deviceId, 100, nameof(deviceId));
        commandId = Normalize(commandId, 120, nameof(commandId));
        commandType = Normalize(commandType, 60, nameof(commandType));
        requestHash = Normalize(requestHash, 128, nameof(requestHash));
        sessionId = (sessionId ?? "").Trim();

        return IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = c.BeginTransaction();

            string? existingType = null;
            string? existingHash = null;
            string? existingSession = null;
            string? existingState = null;
            string? existingOwner = null;
            long existingVersion = 0;
            string existingError = "";

            await using (var read = c.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = """
                    SELECT command_type,request_hash,session_id,state,owner_id,
                           result_session_version,error_text
                    FROM restaurant_device_commands
                    WHERE device_id=$device
                      AND command_id=$command;
                    """;
                read.Parameters.AddWithValue("$device", deviceId);
                read.Parameters.AddWithValue("$command", commandId);

                await using var r = await read.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    existingType = r.GetString(0);
                    existingHash = r.GetString(1);
                    existingSession = r.GetString(2);
                    existingState = r.GetString(3);
                    existingOwner = r.GetString(4);
                    existingVersion = r.GetInt64(5);
                    existingError = r.GetString(6);
                }
            }

            var now = DateTimeOffset.UtcNow.ToString("O");

            if (existingState is not null)
            {
                if (!string.Equals(existingType, commandType, StringComparison.Ordinal) ||
                    !string.Equals(existingHash, requestHash, StringComparison.Ordinal) ||
                    !string.Equals(existingSession, sessionId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Command-ID wurde bereits für einen anderen Restaurant-Befehl verwendet.");
                }

                if (string.Equals(existingState, "COMPLETED", StringComparison.Ordinal))
                {
                    await tx.CommitAsync(ct);
                    return new RestaurantCommandClaim(
                        RestaurantCommandClaimState.Completed,
                        existingVersion,
                        "");
                }

                if (string.Equals(existingState, "FAILED", StringComparison.Ordinal))
                {
                    await tx.CommitAsync(ct);
                    return new RestaurantCommandClaim(
                        RestaurantCommandClaimState.Failed,
                        0,
                        existingError);
                }

                if (string.Equals(existingOwner, _ownerId, StringComparison.Ordinal))
                {
                    await tx.CommitAsync(ct);
                    return new RestaurantCommandClaim(
                        RestaurantCommandClaimState.InProgress,
                        0,
                        "");
                }

                await using (var recover = c.CreateCommand())
                {
                    recover.Transaction = tx;
                    recover.CommandText = """
                        UPDATE restaurant_device_commands
                        SET owner_id=$owner,
                            updated_at=$now
                        WHERE device_id=$device
                          AND command_id=$command
                          AND state='IN_PROGRESS';
                        """;
                    recover.Parameters.AddWithValue("$owner", _ownerId);
                    recover.Parameters.AddWithValue("$now", now);
                    recover.Parameters.AddWithValue("$device", deviceId);
                    recover.Parameters.AddWithValue("$command", commandId);
                    if (await recover.ExecuteNonQueryAsync(ct) != 1)
                        throw new InvalidOperationException(
                            "Restaurant-Befehl konnte nicht zur Wiederaufnahme übernommen werden.");
                }

                await tx.CommitAsync(ct);
                return new RestaurantCommandClaim(
                    RestaurantCommandClaimState.Recovered,
                    0,
                    "");
            }

            await using (var insert = c.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO restaurant_device_commands(
                        device_id,command_id,command_type,request_hash,session_id,
                        state,owner_id,result_session_version,error_text,
                        created_at,updated_at)
                    VALUES(
                        $device,$command,$type,$hash,$session,
                        'IN_PROGRESS',$owner,0,'',$now,$now);
                    """;
                insert.Parameters.AddWithValue("$device", deviceId);
                insert.Parameters.AddWithValue("$command", commandId);
                insert.Parameters.AddWithValue("$type", commandType);
                insert.Parameters.AddWithValue("$hash", requestHash);
                insert.Parameters.AddWithValue("$session", sessionId);
                insert.Parameters.AddWithValue("$owner", _ownerId);
                insert.Parameters.AddWithValue("$now", now);
                await insert.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return new RestaurantCommandClaim(
                RestaurantCommandClaimState.New,
                0,
                "");
        });
    }

    public Task CompleteAsync(
        string deviceId,
        string commandId,
        long resultSessionVersion,
        CancellationToken ct = default)
    {
        if (resultSessionVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(resultSessionVersion));

        return SetTerminalStateAsync(
            deviceId,
            commandId,
            "COMPLETED",
            resultSessionVersion,
            "",
            ct);
    }

    public Task FailAsync(
        string deviceId,
        string commandId,
        string errorText,
        CancellationToken ct = default) =>
        SetTerminalStateAsync(
            deviceId,
            commandId,
            "FAILED",
            0,
            errorText,
            ct);

    private Task SetTerminalStateAsync(
        string deviceId,
        string commandId,
        string state,
        long version,
        string errorText,
        CancellationToken ct)
    {
        deviceId = Normalize(deviceId, 100, nameof(deviceId));
        commandId = Normalize(commandId, 120, nameof(commandId));
        errorText = (errorText ?? "").Trim();
        if (errorText.Length > 500)
            errorText = errorText[..500];

        return IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                UPDATE restaurant_device_commands
                SET state=$state,
                    result_session_version=$version,
                    error_text=$error,
                    updated_at=$now
                WHERE device_id=$device
                  AND command_id=$command
                  AND owner_id=$owner
                  AND state='IN_PROGRESS';
                """;
            q.Parameters.AddWithValue("$state", state);
            q.Parameters.AddWithValue("$version", version);
            q.Parameters.AddWithValue("$error", errorText);
            q.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            q.Parameters.AddWithValue("$device", deviceId);
            q.Parameters.AddWithValue("$command", commandId);
            q.Parameters.AddWithValue("$owner", _ownerId);

            if (await q.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException(
                    "Restaurant-Befehl besitzt nicht mehr die erwartete Ausführungsberechtigung.");
        });
    }

    private static string Normalize(
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
