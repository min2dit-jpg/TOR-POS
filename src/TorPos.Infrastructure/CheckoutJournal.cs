using System.Text.Json;
using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed class CheckoutJournal : ICheckoutJournal
{
    private readonly SqliteDatabase _db;

    public CheckoutJournal(
        SqliteDatabase db) =>
        _db = db;

    public Task BeginAsync(
        CheckoutSnapshot snapshot) =>
        IoQueue.RunAsync(async () =>
        {
            using var c =
                _db.OpenConnection();

            using var q =
                c.CreateCommand();

            q.CommandText = """
                INSERT INTO checkout_operations(
                    id,state,snapshot,evidence,updated_at,
                    terminal_outcome,terminal_code,terminal_message,
                    terminal_submitted,resolution,resolution_actor,resolution_at)
                VALUES(
                    $id,$state,$snapshot,'',$now,
                    'NONE','','',
                    0,'NONE','','');
                """;

            q.Parameters.AddWithValue(
                "$id",
                snapshot.OperationId);

            // R101: Mixed goes through the same terminal-prepared path as
            // Card whenever it has a nonzero card portion to charge.
            q.Parameters.AddWithValue(
                "$state",
                snapshot.EffectiveCardPortionCents > 0
                    ? "PREPARED"
                    : "CASH_READY");

            q.Parameters.AddWithValue(
                "$snapshot",
                JsonSerializer.Serialize(snapshot));

            q.Parameters.AddWithValue(
                "$now",
                DateTimeOffset.Now.ToString("O"));

            await q.ExecuteNonQueryAsync();
        });

    /// <summary>
    /// Generic state transition retained for cash/fiscal/recovery flows.
    /// Terminal financial results should use TransitionTerminalAsync.
    /// </summary>
    public Task TransitionAsync(
        string id,
        string expected,
        string state,
        string evidence) =>
        IoQueue.RunAsync(async () =>
        {
            using var c =
                _db.OpenConnection();

            using var tx =
                c.BeginTransaction();

            using var q =
                c.CreateCommand();

            q.Transaction = tx;
            q.CommandText = """
                UPDATE checkout_operations
                SET state=$state,
                    evidence=$evidence,
                    updated_at=$now
                WHERE id=$id
                  AND state=$expected;
                """;

            var now =
                DateTimeOffset.Now.ToString("O");

            q.Parameters.AddWithValue("$id", id);
            q.Parameters.AddWithValue("$expected", expected);
            q.Parameters.AddWithValue("$state", state);
            q.Parameters.AddWithValue("$evidence", evidence);
            q.Parameters.AddWithValue("$now", now);

            if (await q.ExecuteNonQueryAsync() != 1)
            {
                throw new InvalidOperationException(
                    "Zahlungsstatus wurde verändert. Nicht erneut kassieren.");
            }

            using var audit =
                c.CreateCommand();

            audit.Transaction = tx;
            audit.CommandText = """
                INSERT INTO audit_log(
                    created_at,actor,event_type,
                    entity_type,entity_id,details)
                VALUES(
                    $now,'SYSTEM','CHECKOUT_TRANSITION',
                    'CHECKOUT',$id,$details);
                """;

            audit.Parameters.AddWithValue("$now", now);
            audit.Parameters.AddWithValue("$id", id);
            audit.Parameters.AddWithValue(
                "$details",
                expected + " -> " + state + "; " + evidence);

            await audit.ExecuteNonQueryAsync();

            tx.Commit();
        });

    /// <summary>
    /// Durable admission marker written immediately before the payment command.
    /// Once SENT is stored, TOR must assume that an external charge may occur.
    /// </summary>
    public Task MarkTerminalSubmittedAsync(
        string id,
        string evidence) =>
        IoQueue.RunAsync(async () =>
        {
            using var c =
                _db.OpenConnection();

            using var tx =
                c.BeginTransaction();

            var now =
                DateTimeOffset.Now.ToString("O");

            using var q =
                c.CreateCommand();

            q.Transaction = tx;
            q.CommandText = """
                UPDATE checkout_operations
                SET state='SENT',
                    evidence=$evidence,
                    updated_at=$now,
                    terminal_submitted=1
                WHERE id=$id
                  AND state='PREPARED'
                  AND terminal_submitted=0;
                """;

            q.Parameters.AddWithValue("$id", id);
            q.Parameters.AddWithValue("$evidence", evidence);
            q.Parameters.AddWithValue("$now", now);

            if (await q.ExecuteNonQueryAsync() != 1)
            {
                throw new InvalidOperationException(
                    "Zahlungsauftrag wurde bereits gesendet oder verändert. Nicht erneut kassieren.");
            }

            using var audit =
                c.CreateCommand();

            audit.Transaction = tx;
            audit.CommandText = """
                INSERT INTO audit_log(
                    created_at,actor,event_type,
                    entity_type,entity_id,details)
                VALUES(
                    $now,'SYSTEM','CHECKOUT_TERMINAL_SUBMITTED',
                    'CHECKOUT',$id,$details);
                """;

            audit.Parameters.AddWithValue("$now", now);
            audit.Parameters.AddWithValue("$id", id);
            audit.Parameters.AddWithValue(
                "$details",
                "PREPARED -> SENT; submitted=true; " + evidence);

            await audit.ExecuteNonQueryAsync();

            tx.Commit();
        });

    /// <summary>
    /// Stores terminal outcome and checkout safety transition atomically.
    /// The mapping is intentionally strict:
    /// APPROVED        -> state APPROVED, request submitted
    /// DECLINED        -> state NOT_CHARGED, request submitted
    /// CANCELLED       -> state NOT_CHARGED, request submitted
    /// NOT_SENT        -> state NOT_CHARGED, request not submitted
    /// UNKNOWN         -> state UNKNOWN, request submitted
    /// </summary>
    public Task TransitionTerminalAsync(
        string id,
        string expected,
        string state,
        string evidence,
        PaymentTerminalOutcome outcome,
        bool requestSubmitted,
        string terminalCode,
        string terminalMessage) =>
        IoQueue.RunAsync(async () =>
        {
            ValidateTerminalTransition(
                expected,
                state,
                outcome,
                requestSubmitted);

            var resolution =
                outcome switch
                {
                    PaymentTerminalOutcome.Approved =>
                        CheckoutResolution.AutoApproved,

                    PaymentTerminalOutcome.Declined or
                    PaymentTerminalOutcome.Cancelled or
                    PaymentTerminalOutcome.NotSent =>
                        CheckoutResolution.AutoNotCharged,

                    _ =>
                        CheckoutResolution.None
                };

            var safeCode =
                SafeText(
                    terminalCode,
                    120);

            var safeMessage =
                SafeText(
                    terminalMessage,
                    500);

            using var c =
                _db.OpenConnection();

            using var tx =
                c.BeginTransaction();

            var now =
                DateTimeOffset.Now.ToString("O");

            using var q =
                c.CreateCommand();

            q.Transaction = tx;
            q.CommandText = """
                UPDATE checkout_operations
                SET state=$state,
                    evidence=$evidence,
                    updated_at=$now,
                    terminal_outcome=$outcome,
                    terminal_code=$code,
                    terminal_message=$message,
                    terminal_submitted=$submitted,
                    resolution=$resolution,
                    resolution_actor=CASE
                        WHEN $resolution='NONE' THEN ''
                        ELSE 'SYSTEM'
                    END,
                    resolution_at=CASE
                        WHEN $resolution='NONE' THEN ''
                        ELSE $now
                    END
                WHERE id=$id
                  AND state=$expected;
                """;

            q.Parameters.AddWithValue("$id", id);
            q.Parameters.AddWithValue("$expected", expected);
            q.Parameters.AddWithValue("$state", state);
            q.Parameters.AddWithValue("$evidence", evidence);
            q.Parameters.AddWithValue("$now", now);
            q.Parameters.AddWithValue(
                "$outcome",
                PaymentOutcomeCodec.ToStorage(outcome));
            q.Parameters.AddWithValue(
                "$code",
                safeCode);
            q.Parameters.AddWithValue(
                "$message",
                safeMessage);
            q.Parameters.AddWithValue(
                "$submitted",
                requestSubmitted ? 1 : 0);
            q.Parameters.AddWithValue(
                "$resolution",
                PaymentOutcomeCodec.ToStorage(resolution));

            if (await q.ExecuteNonQueryAsync() != 1)
            {
                throw new InvalidOperationException(
                    "Zahlungsstatus wurde verändert. Nicht erneut kassieren.");
            }

            using var audit =
                c.CreateCommand();

            audit.Transaction = tx;
            audit.CommandText = """
                INSERT INTO audit_log(
                    created_at,actor,event_type,
                    entity_type,entity_id,details)
                VALUES(
                    $now,'SYSTEM','CHECKOUT_TERMINAL_OUTCOME',
                    'CHECKOUT',$id,$details);
                """;

            audit.Parameters.AddWithValue("$now", now);
            audit.Parameters.AddWithValue("$id", id);
            audit.Parameters.AddWithValue(
                "$details",
                $"{expected} -> {state}; " +
                $"outcome={PaymentOutcomeCodec.ToStorage(outcome)}; " +
                $"submitted={requestSubmitted}; " +
                $"code={safeCode}; {evidence}");

            await audit.ExecuteNonQueryAsync();

            tx.Commit();
        });

    /// <summary>
    /// Evidence-based operator reconciliation. This does not send any command
    /// to the terminal and never rewrites the original terminal outcome.
    /// </summary>
    public Task ResolveAsync(
        string id,
        string expected,
        bool paid,
        string actor,
        string evidence) =>
        IoQueue.RunAsync(async () =>
        {
            actor =
                SafeText(
                    actor,
                    120);

            evidence =
                SafeText(
                    evidence,
                    1000);

            if (actor.Length == 0)
            {
                throw new InvalidOperationException(
                    "Administrator fehlt.");
            }

            if (evidence.Length < 8)
            {
                throw new InvalidOperationException(
                    "Prüfnachweis fehlt.");
            }

            if (paid &&
                string.Equals(
                    expected,
                    "PREPARED",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "PREPARED bedeutet: kein Zahlungsauftrag wurde als gesendet markiert. " +
                    "Dieser Zustand darf nicht als Kartenzahlung bestätigt werden.");
            }

            if (!paid &&
                string.Equals(
                    expected,
                    "APPROVED",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Bestätigte Belastung darf nicht auf NOT_CHARGED zurückgesetzt werden.");
            }

            if (string.Equals(
                    expected,
                    "COMMITTED",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    expected,
                    "NOT_CHARGED",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Abgeschlossener Zahlungsstatus kann nicht erneut aufgelöst werden.");
            }

            var targetState =
                paid
                    ? "APPROVED"
                    : "NOT_CHARGED";

            var resolution =
                paid
                    ? CheckoutResolution.ManualPaid
                    : CheckoutResolution.ManualNotCharged;

            using var c =
                _db.OpenConnection();

            using var tx =
                c.BeginTransaction();

            var now =
                DateTimeOffset.Now.ToString("O");

            using var q =
                c.CreateCommand();

            q.Transaction = tx;
            q.CommandText = """
                UPDATE checkout_operations
                SET state=$state,
                    evidence=$evidence,
                    updated_at=$now,
                    resolution=$resolution,
                    resolution_actor=$actor,
                    resolution_at=$now
                WHERE id=$id
                  AND state=$expected;
                """;

            q.Parameters.AddWithValue("$id", id);
            q.Parameters.AddWithValue("$expected", expected);
            q.Parameters.AddWithValue("$state", targetState);
            q.Parameters.AddWithValue("$evidence", evidence);
            q.Parameters.AddWithValue("$now", now);
            q.Parameters.AddWithValue("$actor", actor);
            q.Parameters.AddWithValue(
                "$resolution",
                PaymentOutcomeCodec.ToStorage(resolution));

            if (await q.ExecuteNonQueryAsync() != 1)
            {
                throw new InvalidOperationException(
                    "Zahlungsstatus wurde verändert. Prüfung erneut laden.");
            }

            using var audit =
                c.CreateCommand();

            audit.Transaction = tx;
            audit.CommandText = """
                INSERT INTO audit_log(
                    created_at,actor,event_type,
                    entity_type,entity_id,details)
                VALUES(
                    $now,$actor,'CHECKOUT_RECONCILED',
                    'CHECKOUT',$id,$details);
                """;

            audit.Parameters.AddWithValue("$now", now);
            audit.Parameters.AddWithValue("$actor", actor);
            audit.Parameters.AddWithValue("$id", id);
            audit.Parameters.AddWithValue(
                "$details",
                $"{expected} -> {targetState}; " +
                $"resolution={PaymentOutcomeCodec.ToStorage(resolution)}; " +
                $"evidence={evidence}");

            await audit.ExecuteNonQueryAsync();

            tx.Commit();
        });

    public Task<IReadOnlyList<CheckoutOperation>> GetOpenAsync() =>
        IoQueue.RunAsync<IReadOnlyList<CheckoutOperation>>(async () =>
        {
            using var c =
                _db.OpenConnection();

            using var q =
                c.CreateCommand();

            q.CommandText = """
                SELECT
                    snapshot,state,evidence,sale_id,
                    terminal_outcome,terminal_code,terminal_message,
                    terminal_submitted,resolution,resolution_actor,resolution_at
                FROM checkout_operations
                WHERE state NOT IN ('COMMITTED','NOT_CHARGED')
                ORDER BY updated_at;
                """;

            var result =
                new List<CheckoutOperation>();

            using var r =
                await q.ExecuteReaderAsync();

            while (await r.ReadAsync())
                result.Add(ReadOperation(r));

            return result;
        });

    public Task<CheckoutOperation?> GetAsync(
        string id) =>
        IoQueue.RunAsync<CheckoutOperation?>(async () =>
        {
            using var c =
                _db.OpenConnection();

            using var q =
                c.CreateCommand();

            q.CommandText = """
                SELECT
                    snapshot,state,evidence,sale_id,
                    terminal_outcome,terminal_code,terminal_message,
                    terminal_submitted,resolution,resolution_actor,resolution_at
                FROM checkout_operations
                WHERE id=$id;
                """;

            q.Parameters.AddWithValue(
                "$id",
                id);

            using var r =
                await q.ExecuteReaderAsync();

            if (!await r.ReadAsync())
                return null;

            return ReadOperation(r);
        });

    public Task<long?> FindSaleAsync(
        string id) =>
        IoQueue.RunAsync<long?>(async () =>
        {
            using var c =
                _db.OpenConnection();

            using var q =
                c.CreateCommand();

            q.CommandText = """
                SELECT sale_id
                FROM checkout_operations
                WHERE id=$id
                  AND state='COMMITTED';
                """;

            q.Parameters.AddWithValue(
                "$id",
                id);

            var value =
                await q.ExecuteScalarAsync();

            return value is null ||
                   value is DBNull
                ? null
                : Convert.ToInt64(value);
        });

    private static CheckoutOperation ReadOperation(
        SqliteDataReader r) =>
        new(
            Snapshot:
                JsonSerializer.Deserialize<CheckoutSnapshot>(
                    r.GetString(0))
                ?? throw new InvalidDataException(
                    "Zahlungsjournal beschädigt."),

            State:
                r.GetString(1),

            Evidence:
                r.GetString(2),

            SaleId:
                r.IsDBNull(3)
                    ? null
                    : r.GetInt64(3),

            TerminalOutcome:
                PaymentOutcomeCodec.ParseOutcome(
                    r.GetString(4)),

            TerminalCode:
                r.GetString(5),

            TerminalMessage:
                r.GetString(6),

            TerminalRequestSubmitted:
                r.GetInt64(7) == 1,

            Resolution:
                PaymentOutcomeCodec.ParseResolution(
                    r.GetString(8)),

            ResolutionActor:
                r.GetString(9),

            ResolutionAt:
                r.GetString(10));

    private static void ValidateTerminalTransition(
        string expected,
        string state,
        PaymentTerminalOutcome outcome,
        bool requestSubmitted)
    {
        var valid =
            outcome switch
            {
                PaymentTerminalOutcome.Approved =>
                    requestSubmitted &&
                    expected == "SENT" &&
                    state == "APPROVED",

                PaymentTerminalOutcome.Declined or
                PaymentTerminalOutcome.Cancelled =>
                    requestSubmitted &&
                    expected == "SENT" &&
                    state == "NOT_CHARGED",

                PaymentTerminalOutcome.NotSent =>
                    !requestSubmitted &&
                    expected == "PREPARED" &&
                    state == "NOT_CHARGED",

                PaymentTerminalOutcome.Unknown =>
                    requestSubmitted &&
                    expected == "SENT" &&
                    state == "UNKNOWN",

                _ =>
                    false
            };

        if (!valid)
        {
            throw new InvalidOperationException(
                "Ungültige Kombination aus Terminalergebnis und Checkout-Status.");
        }
    }

    private static string SafeText(
        string? value,
        int maxLength)
    {
        var text =
            (value ?? "")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        return text.Length <= maxLength
            ? text
            : text[..maxLength];
    }
}
