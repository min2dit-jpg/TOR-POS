using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TorPos.Infrastructure;

public enum FiskaltrustSignState
{
    Prepared,
    Sent,
    Unknown,
    Committed
}

public sealed record FiskaltrustSignJournalEntry(
    string Id,
    string ReceiptReference,
    string OperationKey,
    FiskaltrustSignState State,
    FiskaltrustReceiptRequest Request,
    FiskaltrustReceiptResponse? Response,
    string Evidence,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Durable journal for future fiskaltrust Sign calls.
///
/// Important for Germany: explicit flow reuses the same cbReceiptReference for
/// START, optional UPDATE/DELTA calls and the final POS receipt. Therefore the
/// journal identity is (receiptReference, operationKey), not receiptReference
/// alone.
/// </summary>
public sealed class FiskaltrustSignJournal
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

    private readonly SqliteDatabase _db;

    public FiskaltrustSignJournal(SqliteDatabase db) =>
        _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            CREATE TABLE IF NOT EXISTS fiskaltrust_sign_journal_v2(
              id TEXT PRIMARY KEY,
              receipt_reference TEXT NOT NULL,
              operation_key TEXT NOT NULL,
              request_json TEXT NOT NULL,
              state TEXT NOT NULL CHECK(state IN ('PREPARED','SENT','UNKNOWN','COMMITTED')),
              response_json TEXT NOT NULL DEFAULT '',
              evidence TEXT NOT NULL DEFAULT '',
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              UNIQUE(receipt_reference,operation_key));

            CREATE INDEX IF NOT EXISTS ix_fiskaltrust_sign_journal_v2_state
              ON fiskaltrust_sign_journal_v2(state,updated_at);

            CREATE TRIGGER IF NOT EXISTS trg_fiskaltrust_sign_v2_request_immutable
            BEFORE UPDATE OF receipt_reference,operation_key,request_json
            ON fiskaltrust_sign_journal_v2
            BEGIN
              SELECT RAISE(ABORT,'fiskaltrust sign request is immutable');
            END;

            CREATE TRIGGER IF NOT EXISTS trg_fiskaltrust_sign_v2_no_delete
            BEFORE DELETE ON fiskaltrust_sign_journal_v2
            BEGIN
              SELECT RAISE(ABORT,'fiskaltrust sign journal cannot be deleted');
            END;
            """;
        await q.ExecuteNonQueryAsync(ct);
    }

    public Task<FiskaltrustSignJournalEntry> BeginAsync(
        FiskaltrustReceiptRequest request,
        CancellationToken ct = default) =>
        BeginAsync(request, "FINAL", ct);

    public async Task<FiskaltrustSignJournalEntry> BeginAsync(
        FiskaltrustReceiptRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reference = request.CbReceiptReference?.Trim() ?? "";
        if (reference.Length == 0)
            throw new ArgumentException(
                "cbReceiptReference fehlt für das fiskaltrust Journal.",
                nameof(request));

        operationKey = NormalizeOperationKey(operationKey);
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);

        await using var c = _db.OpenConnection();
        await using var tx = await c.BeginTransactionAsync(ct);

        var existing = await ReadAsync(
            c,
            (SqliteTransaction)tx,
            reference,
            operationKey,
            ct);

        if (existing is not null)
        {
            var existingJson = JsonSerializer.Serialize(existing.Request, JsonOptions);
            if (!string.Equals(existingJson, requestJson, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"fiskaltrust Operation '{reference}/{operationKey}' wurde bereits mit einem anderen Payload verwendet.");
            }

            await tx.CommitAsync(ct);
            return existing;
        }

        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");

        await using (var q = c.CreateCommand())
        {
            q.Transaction = (SqliteTransaction)tx;
            q.CommandText = """
                INSERT INTO fiskaltrust_sign_journal_v2(
                  id,receipt_reference,operation_key,request_json,state,
                  response_json,evidence,created_at,updated_at)
                VALUES(
                  $id,$reference,$operation,$request,'PREPARED','','',$created,$updated);
                """;
            q.Parameters.AddWithValue("$id", id);
            q.Parameters.AddWithValue("$reference", reference);
            q.Parameters.AddWithValue("$operation", operationKey);
            q.Parameters.AddWithValue("$request", requestJson);
            q.Parameters.AddWithValue("$created", now.ToString("O"));
            q.Parameters.AddWithValue("$updated", now.ToString("O"));
            await q.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);

        return new FiskaltrustSignJournalEntry(
            id,
            reference,
            operationKey,
            FiskaltrustSignState.Prepared,
            request,
            null,
            "",
            now,
            now);
    }

    public Task MarkSentAsync(
        string id,
        CancellationToken ct = default) =>
        TransitionAsync(
            id,
            from: ["PREPARED"],
            to: "SENT",
            responseJson: null,
            evidence: null,
            ct);

    public Task MarkUnknownAsync(
        string id,
        string evidence,
        CancellationToken ct = default) =>
        TransitionAsync(
            id,
            from: ["SENT"],
            to: "UNKNOWN",
            responseJson: null,
            evidence: evidence ?? "",
            ct);

    public Task MarkCommittedAsync(
        string id,
        FiskaltrustReceiptResponse response,
        string evidence = "",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        return TransitionAsync(
            id,
            from: ["SENT", "UNKNOWN"],
            to: "COMMITTED",
            responseJson: JsonSerializer.Serialize(response, JsonOptions),
            evidence: evidence ?? "",
            ct);
    }

    public Task<FiskaltrustSignJournalEntry?> GetByReferenceAsync(
        string receiptReference,
        CancellationToken ct = default) =>
        GetAsync(receiptReference, "FINAL", ct);

    public async Task<FiskaltrustSignJournalEntry?> GetAsync(
        string receiptReference,
        string operationKey,
        CancellationToken ct = default)
    {
        await using var c = _db.OpenConnection();
        return await ReadAsync(
            c,
            transaction: null,
            receiptReference?.Trim() ?? "",
            NormalizeOperationKey(operationKey),
            ct);
    }

    public async Task<IReadOnlyList<FiskaltrustSignJournalEntry>>
        GetRecoveryCandidatesAsync(
            CancellationToken ct = default)
    {
        var rows = new List<FiskaltrustSignJournalEntry>();

        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT id,receipt_reference,operation_key,request_json,state,
                   response_json,evidence,created_at,updated_at
            FROM fiskaltrust_sign_journal_v2
            WHERE state IN ('SENT','UNKNOWN')
            ORDER BY created_at,id;
            """;

        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(Map(r));

        return rows;
    }

    private async Task TransitionAsync(
        string id,
        IReadOnlyList<string> from,
        string to,
        string? responseJson,
        string? evidence,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Journal-ID fehlt.", nameof(id));

        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();

        var allowed = string.Join(
            ",",
            from.Select((_, i) => $"$from{i}"));

        q.CommandText =
            $"""
             UPDATE fiskaltrust_sign_journal_v2
             SET state=$to,
                 response_json=COALESCE($response,response_json),
                 evidence=COALESCE($evidence,evidence),
                 updated_at=$updated
             WHERE id=$id AND state IN ({allowed});
             """;

        q.Parameters.AddWithValue("$id", id);
        q.Parameters.AddWithValue("$to", to);
        q.Parameters.AddWithValue(
            "$response",
            responseJson is null ? DBNull.Value : responseJson);
        q.Parameters.AddWithValue(
            "$evidence",
            evidence is null ? DBNull.Value : evidence);
        q.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));

        for (var i = 0; i < from.Count; i++)
            q.Parameters.AddWithValue($"$from{i}", from[i]);

        if (await q.ExecuteNonQueryAsync(ct) != 1)
        {
            throw new InvalidOperationException(
                $"fiskaltrust Journal-Statuswechsel nach '{to}' ist aus dem aktuellen Zustand nicht erlaubt.");
        }
    }

    private static async Task<FiskaltrustSignJournalEntry?> ReadAsync(
        SqliteConnection c,
        SqliteTransaction? transaction,
        string reference,
        string operationKey,
        CancellationToken ct)
    {
        if (reference.Length == 0)
            return null;

        await using var q = c.CreateCommand();
        q.Transaction = transaction;
        q.CommandText = """
            SELECT id,receipt_reference,operation_key,request_json,state,
                   response_json,evidence,created_at,updated_at
            FROM fiskaltrust_sign_journal_v2
            WHERE receipt_reference=$reference
              AND operation_key=$operation;
            """;
        q.Parameters.AddWithValue("$reference", reference);
        q.Parameters.AddWithValue("$operation", operationKey);

        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    private static FiskaltrustSignJournalEntry Map(SqliteDataReader r)
    {
        var request =
            JsonSerializer.Deserialize<FiskaltrustReceiptRequest>(
                r.GetString(3),
                JsonOptions)
            ?? throw new InvalidOperationException(
                "Gespeicherter fiskaltrust Request ist nicht lesbar.");

        FiskaltrustReceiptResponse? response = null;
        if (!r.IsDBNull(5) && !string.IsNullOrWhiteSpace(r.GetString(5)))
        {
            response =
                JsonSerializer.Deserialize<FiskaltrustReceiptResponse>(
                    r.GetString(5),
                    JsonOptions)
                ?? throw new InvalidOperationException(
                    "Gespeicherte fiskaltrust Response ist nicht lesbar.");
        }

        return new FiskaltrustSignJournalEntry(
            r.GetString(0),
            r.GetString(1),
            r.GetString(2),
            ParseState(r.GetString(4)),
            request,
            response,
            r.GetString(6),
            DateTimeOffset.Parse(r.GetString(7)),
            DateTimeOffset.Parse(r.GetString(8)));
    }

    private static string NormalizeOperationKey(string? operationKey)
    {
        var value = (operationKey ?? "").Trim().ToUpperInvariant();
        if (value.Length == 0)
            throw new ArgumentException("fiskaltrust operationKey fehlt.", nameof(operationKey));
        if (value.Length > 80)
            throw new ArgumentException("fiskaltrust operationKey ist zu lang.", nameof(operationKey));
        return value;
    }

    private static FiskaltrustSignState ParseState(string state) => state switch
    {
        "PREPARED" => FiskaltrustSignState.Prepared,
        "SENT" => FiskaltrustSignState.Sent,
        "UNKNOWN" => FiskaltrustSignState.Unknown,
        "COMMITTED" => FiskaltrustSignState.Committed,
        _ => throw new InvalidOperationException(
            $"Unbekannter fiskaltrust Journal-Status: {state}")
    };
}
