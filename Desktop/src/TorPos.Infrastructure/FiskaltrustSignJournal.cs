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
    FiskaltrustSignState State,
    FiskaltrustReceiptRequest Request,
    FiskaltrustReceiptResponse? Response,
    string Evidence,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Durable journal for future fiskaltrust Sign calls.
///
/// Safety rule:
/// PREPARED is written before any network call. SENT is written immediately
/// before POST /Sign. If the process dies after SENT, the original request is
/// recovered from this journal and must be queried with ReceiptRequest before
/// any resend is considered.
///
/// This class is intentionally not wired into production checkout yet.
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
            CREATE TABLE IF NOT EXISTS fiskaltrust_sign_journal(
              id TEXT PRIMARY KEY,
              receipt_reference TEXT NOT NULL UNIQUE,
              request_json TEXT NOT NULL,
              state TEXT NOT NULL CHECK(state IN ('PREPARED','SENT','UNKNOWN','COMMITTED')),
              response_json TEXT NOT NULL DEFAULT '',
              evidence TEXT NOT NULL DEFAULT '',
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL);

            CREATE INDEX IF NOT EXISTS ix_fiskaltrust_sign_journal_state
              ON fiskaltrust_sign_journal(state,updated_at);

            CREATE TRIGGER IF NOT EXISTS trg_fiskaltrust_sign_request_immutable
            BEFORE UPDATE OF receipt_reference,request_json ON fiskaltrust_sign_journal
            BEGIN
              SELECT RAISE(ABORT,'fiskaltrust sign request is immutable');
            END;

            CREATE TRIGGER IF NOT EXISTS trg_fiskaltrust_sign_no_delete
            BEFORE DELETE ON fiskaltrust_sign_journal
            BEGIN
              SELECT RAISE(ABORT,'fiskaltrust sign journal cannot be deleted');
            END;
            """;
        await q.ExecuteNonQueryAsync(ct);
    }

    public async Task<FiskaltrustSignJournalEntry> BeginAsync(
        FiskaltrustReceiptRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reference = request.CbReceiptReference?.Trim() ?? "";
        if (reference.Length == 0)
            throw new ArgumentException(
                "cbReceiptReference fehlt für das fiskaltrust Journal.",
                nameof(request));

        var requestJson = JsonSerializer.Serialize(request, JsonOptions);

        await using var c = _db.OpenConnection();
        await using var tx = await c.BeginTransactionAsync(ct);

        var existing = await ReadByReferenceAsync(
            c,
            (SqliteTransaction)tx,
            reference,
            ct);

        if (existing is not null)
        {
            var existingJson = JsonSerializer.Serialize(existing.Request, JsonOptions);
            if (!string.Equals(existingJson, requestJson, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"cbReceiptReference '{reference}' wurde bereits mit einem anderen fiskaltrust Payload verwendet.");
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
                INSERT INTO fiskaltrust_sign_journal(
                  id,receipt_reference,request_json,state,response_json,evidence,
                  created_at,updated_at)
                VALUES(
                  $id,$reference,$request,'PREPARED','','',$created,$updated);
                """;
            q.Parameters.AddWithValue("$id", id);
            q.Parameters.AddWithValue("$reference", reference);
            q.Parameters.AddWithValue("$request", requestJson);
            q.Parameters.AddWithValue("$created", now.ToString("O"));
            q.Parameters.AddWithValue("$updated", now.ToString("O"));
            await q.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);

        return new FiskaltrustSignJournalEntry(
            id,
            reference,
            FiskaltrustSignState.Prepared,
            request,
            null,
            "",
            now,
            now);
    }

    /// <summary>
    /// Must be called immediately before the HTTP Sign request.
    /// Once SENT exists, restart logic may only recover/query first.
    /// </summary>
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

    public async Task<FiskaltrustSignJournalEntry?> GetByReferenceAsync(
        string receiptReference,
        CancellationToken ct = default)
    {
        await using var c = _db.OpenConnection();
        return await ReadByReferenceAsync(
            c,
            transaction: null,
            receiptReference?.Trim() ?? "",
            ct);
    }

    /// <summary>
    /// SENT/UNKNOWN entries may have reached fiskaltrust. They must be resolved
    /// with ReceiptRequest using the persisted original Request before resend.
    /// PREPARED is excluded because it was never marked as submitted.
    /// </summary>
    public async Task<IReadOnlyList<FiskaltrustSignJournalEntry>>
        GetRecoveryCandidatesAsync(
            CancellationToken ct = default)
    {
        var rows = new List<FiskaltrustSignJournalEntry>();

        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT id,receipt_reference,request_json,state,response_json,evidence,
                   created_at,updated_at
            FROM fiskaltrust_sign_journal
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
             UPDATE fiskaltrust_sign_journal
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

    private static async Task<FiskaltrustSignJournalEntry?> ReadByReferenceAsync(
        SqliteConnection c,
        SqliteTransaction? transaction,
        string reference,
        CancellationToken ct)
    {
        if (reference.Length == 0)
            return null;

        await using var q = c.CreateCommand();
        q.Transaction = transaction;
        q.CommandText = """
            SELECT id,receipt_reference,request_json,state,response_json,evidence,
                   created_at,updated_at
            FROM fiskaltrust_sign_journal
            WHERE receipt_reference=$reference;
            """;
        q.Parameters.AddWithValue("$reference", reference);

        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    private static FiskaltrustSignJournalEntry Map(SqliteDataReader r)
    {
        var request =
            JsonSerializer.Deserialize<FiskaltrustReceiptRequest>(
                r.GetString(2),
                JsonOptions)
            ?? throw new InvalidOperationException(
                "Gespeicherter fiskaltrust Request ist nicht lesbar.");

        FiskaltrustReceiptResponse? response = null;
        if (!r.IsDBNull(4) && !string.IsNullOrWhiteSpace(r.GetString(4)))
        {
            response =
                JsonSerializer.Deserialize<FiskaltrustReceiptResponse>(
                    r.GetString(4),
                    JsonOptions)
                ?? throw new InvalidOperationException(
                    "Gespeicherte fiskaltrust Response ist nicht lesbar.");
        }

        return new FiskaltrustSignJournalEntry(
            r.GetString(0),
            r.GetString(1),
            ParseState(r.GetString(3)),
            request,
            response,
            r.GetString(5),
            DateTimeOffset.Parse(r.GetString(6)),
            DateTimeOffset.Parse(r.GetString(7)));
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
