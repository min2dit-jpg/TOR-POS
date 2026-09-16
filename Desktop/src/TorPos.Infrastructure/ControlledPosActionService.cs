using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TorPos.Infrastructure;

/// <summary>
/// R70: append-only, structured and hash-chained audit for sensitive cashier
/// actions such as immediate storno, discount and cart cancellation.
///
/// The normal audit_log remains the general journal. Every controlled action
/// is mirrored there in the same SQLite transaction, while pos_action_log
/// carries structured before/after values and a tamper-evident hash chain.
///
/// This is tamper-evident, not a substitute for OS access control or a future
/// signed external checkpoint.
/// </summary>
public sealed class ControlledPosActionService
{
    private readonly SqliteDatabase _db;

    public ControlledPosActionService(SqliteDatabase db)
    {
        _db = db;
    }

    public async Task AppendAsync(
        PosActionLogRequest request,
        CancellationToken ct = default)
    {
        Validate(request);

        await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = await c.BeginTransactionAsync(ct);

            var createdAt = DateTimeOffset.Now.ToString("O");
            var previousHash = "";

            await using (var previous = c.CreateCommand())
            {
                previous.Transaction = (SqliteTransaction)tx;
                previous.CommandText = """
                    SELECT entry_hash
                    FROM pos_action_log
                    ORDER BY id DESC
                    LIMIT 1;
                    """;
                previousHash =
                    Convert.ToString(await previous.ExecuteScalarAsync(ct)) ?? "";
            }

            var entryHash = ComputeHash(
                previousHash,
                createdAt,
                request);

            await using (var q = c.CreateCommand())
            {
                q.Transaction = (SqliteTransaction)tx;
                q.CommandText = """
                    INSERT INTO pos_action_log(
                        created_at,
                        action_id,
                        phase,
                        actor,
                        register_id,
                        operation_id,
                        action_type,
                        reason,
                        entity_type,
                        entity_id,
                        before_total_cents,
                        after_total_cents,
                        amount_cents,
                        details,
                        prev_hash,
                        entry_hash)
                    VALUES(
                        $created,
                        $actionId,
                        $phase,
                        $actor,
                        $register,
                        $operation,
                        $action,
                        $reason,
                        $entityType,
                        $entityId,
                        $before,
                        $after,
                        $amount,
                        $details,
                        $prev,
                        $hash);
                    """;

                Bind(q, request, createdAt, previousHash, entryHash);
                await q.ExecuteNonQueryAsync(ct);
            }

            // Existing reports already consume audit_log. Mirror the controlled
            // entry there inside the SAME database transaction.
            await using (var audit = c.CreateCommand())
            {
                audit.Transaction = (SqliteTransaction)tx;
                audit.CommandText = """
                    INSERT INTO audit_log(
                        created_at,
                        actor,
                        event_type,
                        entity_type,
                        entity_id,
                        details)
                    VALUES(
                        $created,
                        $actor,
                        $event,
                        $entityType,
                        $entityId,
                        $details);
                    """;

                audit.Parameters.AddWithValue("$created", createdAt);
                audit.Parameters.AddWithValue("$actor", request.Actor);
                audit.Parameters.AddWithValue(
                    "$event",
                    $"{request.ActionType}_{request.Phase}");
                audit.Parameters.AddWithValue(
                    "$entityType",
                    request.EntityType);
                audit.Parameters.AddWithValue(
                    "$entityId",
                    request.EntityId);

                audit.Parameters.AddWithValue(
                    "$details",
                    JsonSerializer.Serialize(new
                    {
                        action_id = request.ActionId,
                        operation_id = request.OperationId,
                        register_id = request.RegisterId,
                        reason = request.Reason,
                        before_total_cents = request.BeforeTotalCents,
                        after_total_cents = request.AfterTotalCents,
                        amount_cents = request.AmountCents,
                        details = request.Details,
                        integrity_hash = entryHash
                    }));

                await audit.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        });
    }

    // Deliberately NOT routed through IoQueue: this is a plain read-only scan
    // (no transaction, no write) on its own connection, safe alongside
    // concurrent checkout writes under WAL. pos_action_log only grows, so
    // funneling this through the single shared write-serialization queue
    // meant a routine integrity check could stall every register's checkout
    // for as long as the full-table hash walk takes.
    public async Task<PosActionIntegrityStatus> VerifyIntegrityAsync(
        CancellationToken ct = default)
    {
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                SELECT
                    id,
                    created_at,
                    action_id,
                    phase,
                    actor,
                    register_id,
                    operation_id,
                    action_type,
                    reason,
                    entity_type,
                    entity_id,
                    before_total_cents,
                    after_total_cents,
                    amount_cents,
                    details,
                    prev_hash,
                    entry_hash
                FROM pos_action_log
                ORDER BY id;
                """;

            await using var r = await q.ExecuteReaderAsync(ct);

            var expectedPreviousHash = "";
            long count = 0;
            long lastId = 0;
            string lastHash = "";

            while (await r.ReadAsync(ct))
            {
                count++;
                lastId = r.GetInt64(0);
                var createdAt = r.GetString(1);

                var request = new PosActionLogRequest
                {
                    ActionId = r.GetString(2),
                    Phase = r.GetString(3),
                    Actor = r.GetString(4),
                    RegisterId = r.GetString(5),
                    OperationId = r.GetString(6),
                    ActionType = r.GetString(7),
                    Reason = r.GetString(8),
                    EntityType = r.GetString(9),
                    EntityId = r.GetString(10),
                    BeforeTotalCents = r.GetInt64(11),
                    AfterTotalCents = r.GetInt64(12),
                    AmountCents = r.GetInt64(13),
                    Details = r.GetString(14)
                };

                var storedPreviousHash = r.GetString(15);
                var storedHash = r.GetString(16);

                if (!string.Equals(
                    storedPreviousHash,
                    expectedPreviousHash,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return new PosActionIntegrityStatus(
                        false,
                        count,
                        lastId,
                        storedHash,
                        $"Hash-Kette vor Eintrag {lastId} unterbrochen.");
                }

                var expectedHash = ComputeHash(
                    storedPreviousHash,
                    createdAt,
                    request);

                if (!string.Equals(
                    storedHash,
                    expectedHash,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return new PosActionIntegrityStatus(
                        false,
                        count,
                        lastId,
                        storedHash,
                        $"Integritätsprüfung bei Eintrag {lastId} fehlgeschlagen.");
                }

                expectedPreviousHash = storedHash;
                lastHash = storedHash;
            }

            return new PosActionIntegrityStatus(
                true,
                count,
                lastId,
                lastHash,
                count == 0
                    ? "Noch keine kontrollierten Kassenaktionen vorhanden."
                    : $"{count} kontrollierte Kassenaktionen unverändert.");
        }
    }

    private static void Validate(PosActionLogRequest request)
    {
        static void Required(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException(
                    $"Audit-Feld '{name}' darf nicht leer sein.");
        }

        Required(request.ActionId, nameof(request.ActionId));
        Required(request.Phase, nameof(request.Phase));
        Required(request.Actor, nameof(request.Actor));
        Required(request.RegisterId, nameof(request.RegisterId));
        Required(request.OperationId, nameof(request.OperationId));
        Required(request.ActionType, nameof(request.ActionType));
        Required(request.Reason, nameof(request.Reason));
        Required(request.EntityType, nameof(request.EntityType));

        if (request.BeforeTotalCents < 0 ||
            request.AfterTotalCents < 0 ||
            request.AmountCents < 0)
        {
            throw new InvalidOperationException(
                "Audit-Beträge dürfen nicht negativ sein.");
        }
    }

    private static void Bind(
        SqliteCommand q,
        PosActionLogRequest request,
        string createdAt,
        string previousHash,
        string entryHash)
    {
        q.Parameters.AddWithValue("$created", createdAt);
        q.Parameters.AddWithValue("$actionId", request.ActionId);
        q.Parameters.AddWithValue("$phase", request.Phase);
        q.Parameters.AddWithValue("$actor", request.Actor);
        q.Parameters.AddWithValue("$register", request.RegisterId);
        q.Parameters.AddWithValue("$operation", request.OperationId);
        q.Parameters.AddWithValue("$action", request.ActionType);
        q.Parameters.AddWithValue("$reason", request.Reason);
        q.Parameters.AddWithValue("$entityType", request.EntityType);
        q.Parameters.AddWithValue("$entityId", request.EntityId ?? "");
        q.Parameters.AddWithValue("$before", request.BeforeTotalCents);
        q.Parameters.AddWithValue("$after", request.AfterTotalCents);
        q.Parameters.AddWithValue("$amount", request.AmountCents);
        q.Parameters.AddWithValue("$details", request.Details ?? "");
        q.Parameters.AddWithValue("$prev", previousHash);
        q.Parameters.AddWithValue("$hash", entryHash);
    }

    private static string ComputeHash(
        string previousHash,
        string createdAt,
        PosActionLogRequest request)
    {
        var b = new StringBuilder();

        static void Field(StringBuilder target, string? value)
        {
            value ??= "";
            target.Append(value.Length);
            target.Append(':');
            target.Append(value);
            target.Append('|');
        }

        Field(b, "TOR-POS-POS-ACTION-V1");
        Field(b, previousHash);
        Field(b, createdAt);
        Field(b, request.ActionId);
        Field(b, request.Phase);
        Field(b, request.Actor);
        Field(b, request.RegisterId);
        Field(b, request.OperationId);
        Field(b, request.ActionType);
        Field(b, request.Reason);
        Field(b, request.EntityType);
        Field(b, request.EntityId);
        Field(b, request.BeforeTotalCents.ToString());
        Field(b, request.AfterTotalCents.ToString());
        Field(b, request.AmountCents.ToString());
        Field(b, request.Details);

        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes(b.ToString()));

        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed record PosActionLogRequest
{
    public string ActionId { get; init; } = "";
    public string Phase { get; init; } = "";
    public string Actor { get; init; } = "";
    public string RegisterId { get; init; } = "";
    public string OperationId { get; init; } = "";
    public string ActionType { get; init; } = "";
    public string Reason { get; init; } = "";
    public string EntityType { get; init; } = "";
    public string EntityId { get; init; } = "";
    public long BeforeTotalCents { get; init; }
    public long AfterTotalCents { get; init; }
    public long AmountCents { get; init; }
    public string Details { get; init; } = "";
}

public sealed record PosActionIntegrityStatus(
    bool Valid,
    long EntryCount,
    long LastId,
    string LastHash,
    string Message);
