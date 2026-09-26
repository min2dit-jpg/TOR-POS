using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// The TSE-Wechsel journal. Changes and the states of their (future) tax
/// office notification are rows of the existing audit_log, whose triggers
/// already reject UPDATE and DELETE - so the journal is append-only without a
/// schema change, and a record can be corrected only by a newer entry.
///
/// Recording a change never touches sales, TSE results or DSFinV-K data: the
/// old TSE's Vorgänge stay signed by the old TSE.
/// </summary>
public sealed class TseChangeJournal : ITseChangeJournal
{
    private readonly SqliteDatabase _db;

    public TseChangeJournal(SqliteDatabase db) => _db = db;

    public async Task RecordAsync(TseChangeRecord change, FiscalComplianceProfile profile, CancellationToken ct = default)
    {
        Validate(change);
        var initial = TseChangeNotificationRules.InitialFor(profile, change.Outcome);
        var notification = new TseChangeNotificationRecord(
            change.ChangeId,
            initial,
            change.OccurredAt,
            change.Actor,
            initial == TseChangeNotificationStatus.NotRequired
                ? $"Regelwerk {profile.Version}: keine gesonderte TSE-Wechsel-Meldung vorgeschrieben. Mitteilung nach § 146a Abs. 4 AO in Mein ELSTER eigenverantwortlich prüfen."
                : $"Regelwerk {profile.Version}: Meldung vorzubereiten.");

        await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
            if (await ExistsAsync(c, tx, change.ChangeId, ct))
                throw new InvalidOperationException($"TSE-Wechsel {change.ChangeId} ist bereits protokolliert.");
            await InsertAsync(c, tx, change.OccurredAt, change.Actor, TseChangeRecord.AuditEventType, change.ChangeId, change.ToJson(), ct);
            await InsertAsync(c, tx, notification.At, notification.Actor, TseChangeNotificationRecord.AuditEventType, change.ChangeId,
                JsonSerializer.Serialize(notification), ct);
            await tx.CommitAsync(ct);
        });
    }

    public async Task SetNotificationStatusAsync(
        string changeId,
        TseChangeNotificationStatus status,
        string actor,
        string note = "",
        string submissionReference = "",
        CancellationToken ct = default)
    {
        if (status == TseChangeNotificationStatus.Submitted && string.IsNullOrWhiteSpace(submissionReference))
            throw new InvalidOperationException("Eine übermittelte Meldung braucht die Referenz (z. B. ELSTER-Transferticket).");

        await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
            if (!await ExistsAsync(c, tx, changeId, ct))
                throw new InvalidOperationException($"TSE-Wechsel {changeId} ist nicht protokolliert.");
            var history = await LoadNotificationsAsync(c, tx, changeId, ct);
            var current = history.Count == 0 ? TseChangeNotificationStatus.NotRequired : history[^1].Status;
            if (!TseChangeNotificationRules.CanTransition(current, status))
                throw new InvalidOperationException($"Meldestatus {current} → {status} ist nicht zulässig.");
            var record = new TseChangeNotificationRecord(changeId, status, DateTimeOffset.Now, Actor(actor), note ?? "", submissionReference ?? "");
            await InsertAsync(c, tx, record.At, record.Actor, TseChangeNotificationRecord.AuditEventType, changeId, JsonSerializer.Serialize(record), ct);
            await tx.CommitAsync(ct);
        });
    }

    public Task<IReadOnlyList<TseChangeRecord>> ListAsync(CancellationToken ct = default) =>
        IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            return await LoadAllAsync(c, ct);
        });

    public Task<IReadOnlyList<TseChangeNotificationRecord>> NotificationHistoryAsync(string changeId, CancellationToken ct = default) =>
        IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            return (IReadOnlyList<TseChangeNotificationRecord>)await LoadNotificationsAsync(c, null, changeId, ct);
        });

    /// <summary>For the DSFinV-K preflight, on its own connection.</summary>
    public static async Task<IReadOnlyList<TseChangeRecord>> LoadAllAsync(SqliteConnection c, CancellationToken ct = default)
    {
        var list = new List<TseChangeRecord>();
        await using var q = c.CreateCommand();
        q.CommandText = "SELECT details FROM audit_log WHERE event_type=$event AND entity_type=$entity ORDER BY id;";
        q.Parameters.AddWithValue("$event", TseChangeRecord.AuditEventType);
        q.Parameters.AddWithValue("$entity", TseChangeRecord.AuditEntityType);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            try
            {
                list.Add(TseChangeRecord.FromJson(r.GetString(0)));
            }
            catch (JsonException)
            {
                // A row that cannot be read is skipped here, never repaired;
                // the audit log keeps the original.
            }
        }

        return list;
    }

    private static async Task<List<TseChangeNotificationRecord>> LoadNotificationsAsync(SqliteConnection c, SqliteTransaction? tx, string changeId, CancellationToken ct)
    {
        var list = new List<TseChangeNotificationRecord>();
        await using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT details FROM audit_log WHERE event_type=$event AND entity_type=$entity AND entity_id=$id ORDER BY id;";
        q.Parameters.AddWithValue("$event", TseChangeNotificationRecord.AuditEventType);
        q.Parameters.AddWithValue("$entity", TseChangeRecord.AuditEntityType);
        q.Parameters.AddWithValue("$id", changeId ?? "");
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var record = JsonSerializer.Deserialize<TseChangeNotificationRecord>(r.GetString(0));
            if (record is not null)
                list.Add(record);
        }

        return list;
    }

    private static async Task<bool> ExistsAsync(SqliteConnection c, SqliteTransaction tx, string changeId, CancellationToken ct)
    {
        await using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT COUNT(*) FROM audit_log WHERE event_type=$event AND entity_type=$entity AND entity_id=$id;";
        q.Parameters.AddWithValue("$event", TseChangeRecord.AuditEventType);
        q.Parameters.AddWithValue("$entity", TseChangeRecord.AuditEntityType);
        q.Parameters.AddWithValue("$id", changeId);
        return Convert.ToInt64(await q.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
    }

    private static async Task InsertAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset at, string actor, string eventType, string changeId, string details, CancellationToken ct)
    {
        await using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            INSERT INTO audit_log(created_at,actor,event_type,entity_type,entity_id,details)
            VALUES($at,$actor,$event,$entity,$id,$details);
            """;
        q.Parameters.AddWithValue("$at", at.ToString("O", CultureInfo.InvariantCulture));
        q.Parameters.AddWithValue("$actor", Actor(actor));
        q.Parameters.AddWithValue("$event", eventType);
        q.Parameters.AddWithValue("$entity", TseChangeRecord.AuditEntityType);
        q.Parameters.AddWithValue("$id", changeId);
        q.Parameters.AddWithValue("$details", details);
        await q.ExecuteNonQueryAsync(ct);
    }

    private static string Actor(string? actor) => string.IsNullOrWhiteSpace(actor) ? "SYSTEM" : actor.Trim();

    private static void Validate(TseChangeRecord change)
    {
        if (string.IsNullOrWhiteSpace(change.ChangeId))
            throw new ArgumentException("TSE-Wechsel ohne ID.", nameof(change));
        if (string.IsNullOrWhiteSpace(change.Next.SerialNumber))
            throw new ArgumentException("TSE-Wechsel ohne neue TSE-Seriennummer.", nameof(change));
        if (string.IsNullOrWhiteSpace(change.Reason))
            throw new ArgumentException("TSE-Wechsel ohne Grund.", nameof(change));
        if (string.IsNullOrWhiteSpace(change.KassenId))
            throw new ArgumentException("TSE-Wechsel ohne Kassen-ID.", nameof(change));
        if (change.Outcome == TseChangeOutcome.Failed && string.IsNullOrWhiteSpace(change.ErrorMessage))
            throw new ArgumentException("Fehlgeschlagener TSE-Wechsel ohne Fehlerbeschreibung.", nameof(change));
    }
}

/// <summary>
/// Builds a <see cref="TseChangeRecord"/> from the TSE settings before and after
/// a probe or activation and writes it - only when the TSE actually changed.
/// </summary>
public static class TseChangeRecorder
{
    public static async Task<TseChangeRecord?> RecordIfChangedAsync(
        ITseChangeJournal journal,
        ISystemIdentityRepository identity,
        IReadOnlyDictionary<string, string> settingsBefore,
        IReadOnlyDictionary<string, string> settingsAfter,
        string reason,
        string actor,
        TseChangeOutcome outcome,
        string errorCode = "",
        string errorMessage = "",
        CancellationToken ct = default)
    {
        // The last TSE the journal knew to be working is the reference once
        // there is one - the new TSE of a successful change, the old TSE of a
        // failed one. A failed activation already wrote the new serial into
        // the settings, and the later successful one must still be recorded;
        // a serial changed outside the journal is documented on the next check.
        var last = (await journal.ListAsync(ct)).LastOrDefault();
        var previous = last is null
            ? TseIdentity.FromSettings(settingsBefore)
            : last.Outcome == TseChangeOutcome.Succeeded ? last.Next : last.Previous;
        var next = TseIdentity.FromSettings(settingsAfter);
        if (!TseChangeDetector.IsChange(previous, next))
            return null;

        string Get(IReadOnlyDictionary<string, string> s, string key) => s.TryGetValue(key, out var v) ? (v ?? "").Trim() : "";
        var system = await identity.GetAsync(ct);
        var change = new TseChangeRecord(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.Now,
            previous,
            next,
            reason,
            string.IsNullOrWhiteSpace(actor) ? "SYSTEM" : actor.Trim(),
            system.EasSerial,
            Environment.MachineName,
            Get(settingsAfter, "tse.client_id"),
            Get(settingsAfter, "company.name"),
            outcome,
            errorCode ?? "",
            errorMessage ?? "");
        await journal.RecordAsync(change, GermanFiscalRulesets.Resolve(DateOnly.FromDateTime(DateTime.Now)), ct);
        return change;
    }
}
