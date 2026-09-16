using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed class SystemIdentityRepository : ISystemIdentityRepository
{
    private readonly SqliteDatabase _db;
    public SystemIdentityRepository(SqliteDatabase db) => _db = db;
public async Task<SystemIdentity> GetAsync(CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT eas_serial,manufacturer,model,created_at
            FROM system_identity
            WHERE id=1;
            """;
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            throw new InvalidOperationException("Systemidentität fehlt.");
        return new SystemIdentity(r.GetString(0), r.GetString(1), r.GetString(2), DateTimeOffset.Parse(r.GetString(3)));
    });
}}

public sealed class AuditLogRepository : IAuditLog
{
    private readonly SqliteDatabase _db;
    public AuditLogRepository(SqliteDatabase db) => _db = db;
public async Task WriteAsync(string actor, string eventType, string entityType, string entityId, string details, CancellationToken ct = default)
{
    await IoQueue.RunAsync(async () =>
    {
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            INSERT INTO audit_log(
              created_at,actor,event_type,entity_type,entity_id,details)
            VALUES($time,$actor,$event,$entity,$id,$details);
            """;
        q.Parameters.AddWithValue("$time", DateTimeOffset.Now.ToString("O"));
        q.Parameters.AddWithValue("$actor", actor ?? "SYSTEM");
        q.Parameters.AddWithValue("$event", eventType ?? "");
        q.Parameters.AddWithValue("$entity", entityType ?? "");
        q.Parameters.AddWithValue("$id", entityId ?? "");
        q.Parameters.AddWithValue("$details", details ?? "");
        await q.ExecuteNonQueryAsync(ct);
    });
}public async Task<string> ExportCsvAsync(string targetPath, DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("Zieldatei fehlt.", nameof(targetPath));
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? Environment.CurrentDirectory);
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT id,created_at,actor,event_type,entity_type,entity_id,details
            FROM audit_log
            WHERE ($from='' OR created_at >= $from)
              AND ($to='' OR created_at <= $to)
            ORDER BY id;
            """;
        q.Parameters.AddWithValue("$from", from?.ToString("O") ?? "");
        q.Parameters.AddWithValue("$to", to?.ToString("O") ?? "");
        static string Csv(string? value)
        {
            value ??= "";
            return "\"" + value.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
        }

        await using var writer = new StreamWriter(targetPath, false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteLineAsync("ID;CREATED_AT;ACTOR;EVENT_TYPE;ENTITY_TYPE;ENTITY_ID;DETAILS");
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(string.Join(";", r.GetInt64(0).ToString(), Csv(r.GetString(1)), Csv(r.GetString(2)), Csv(r.GetString(3)), Csv(r.GetString(4)), Csv(r.GetString(5)), Csv(r.GetString(6))));
        }

        await writer.FlushAsync(ct);
        return targetPath;
    });
}}

public sealed class TseOutageRepository : ITseOutageRepository
{
    private readonly SqliteDatabase _db;
    private readonly IAuditLog _audit;

    public TseOutageRepository(
        SqliteDatabase db,
        IAuditLog audit)
    {
        _db = db;
        _audit = audit;
    }
public async Task<TseOutage?> GetOpenAsync(CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT id,started_at,ended_at,reason,actor,state
            FROM tse_outage_log
            WHERE state='OPEN'
            ORDER BY id DESC
            LIMIT 1;
            """;
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return null;
        return new TseOutage(r.GetInt64(0), DateTimeOffset.Parse(r.GetString(1)), string.IsNullOrWhiteSpace(r.GetString(2)) ? null : DateTimeOffset.Parse(r.GetString(2)), r.GetString(3), r.GetString(4), r.GetString(5));
    });
}public async Task<TseOutage> OpenAsync(string reason, string actor = "SYSTEM", CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var existing = await GetOpenAsync(ct);
        if (existing is not null)
            return existing;
        var now = DateTimeOffset.Now;
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            INSERT INTO tse_outage_log(
              started_at,ended_at,reason,actor,state)
            VALUES($started,'',$reason,$actor,'OPEN');
            SELECT last_insert_rowid();
            """;
        q.Parameters.AddWithValue("$started", now.ToString("O"));
        q.Parameters.AddWithValue("$reason", string.IsNullOrWhiteSpace(reason) ? "TSE nicht erreichbar" : reason.Trim());
        q.Parameters.AddWithValue("$actor", actor);
        var id = Convert.ToInt64(await q.ExecuteScalarAsync(ct));
        await _audit.WriteAsync(actor, "TSE_OUTAGE_OPEN", "TSE_OUTAGE", id.ToString(), reason, ct);
        return new TseOutage(id, now, null, reason, actor, "OPEN");
    });
}public async Task CloseOpenAsync(string actor = "SYSTEM", CancellationToken ct = default)
{
    await IoQueue.RunAsync(async () =>
    {
        var existing = await GetOpenAsync(ct);
        if (existing is null)
            return;
        var now = DateTimeOffset.Now;
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            UPDATE tse_outage_log
            SET ended_at=$ended,
                state='CLOSED'
            WHERE id=$id
              AND state='OPEN';
            """;
        q.Parameters.AddWithValue("$ended", now.ToString("O"));
        q.Parameters.AddWithValue("$id", existing.Id);
        await q.ExecuteNonQueryAsync(ct);
        await _audit.WriteAsync(actor, "TSE_OUTAGE_CLOSED", "TSE_OUTAGE", existing.Id.ToString(), $"started_at={existing.StartedAt:O}; ended_at={now:O}", ct);
    });
}}

public sealed class CashMovementRepository : ICashMovementRepository
{
    private readonly SqliteDatabase _db;
    private readonly IAuditLog _audit;

    public CashMovementRepository(SqliteDatabase db, IAuditLog audit)
    {
        _db = db;
        _audit = audit;
    }
public async Task<CashMovement> AddAsync(CashMovementRequest request, string actor, CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        if (request.AmountCents < 0)
            throw new InvalidOperationException("Betrag darf nicht negativ sein.");
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new InvalidOperationException("Grund ist erforderlich.");
        var now = DateTimeOffset.Now;
        var type = request.Kind switch
        {
            CashMovementKind.Einlage => "EINLAGE",
            CashMovementKind.Entnahme => "ENTNAHME",
            CashMovementKind.CashCount => "CASH_COUNT",
            _ => throw new InvalidOperationException("Unbekannte Kassenbewegung.")};
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            INSERT INTO cash_movements(
              created_at,movement_type,amount_cents,reason,actor,fiscal_mode)
            VALUES($time,$type,$amount,$reason,$actor,'TEST_ONLY');
            SELECT last_insert_rowid();
            """;
        q.Parameters.AddWithValue("$time", now.ToString("O"));
        q.Parameters.AddWithValue("$type", type);
        q.Parameters.AddWithValue("$amount", request.AmountCents);
        q.Parameters.AddWithValue("$reason", request.Reason.Trim());
        q.Parameters.AddWithValue("$actor", actor);
        var id = Convert.ToInt64(await q.ExecuteScalarAsync(ct));
        await _audit.WriteAsync(actor, "CASH_MOVEMENT", "CASH_MOVEMENT", id.ToString(), $"{type}; amount_cents={request.AmountCents}; reason={request.Reason.Trim()}", ct);
        return new CashMovement(id, now, request.Kind, request.AmountCents, request.Reason.Trim(), actor, "TEST_ONLY");
    });
}// R92: same bug family as R88/R90/R91, found on the same sweep - counted
// a cash BON STORNO/Teilretoure's total_cents as MORE cash coming in,
// when real cash was actually handed back to the customer. This feeds the
// Kassensturz "erwarteter Bestand" the cashier compares against the
// physically counted drawer - an inflated expected figure would make a
// perfectly correct drawer look like it's short by exactly the reversed
// amount. Now nets STORNO/RETURN as a subtraction instead of an addition.
public async Task<long> GetExpectedCashCentsAsync(long openingBalanceCents, CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        // R117: the expected drawer content is counted from the last
        // Tagesabschluss, exactly like the Z-report period - not from the
        // current calendar day. With a calendar day, a Kassensturz taken at
        // 01:00 in a business that is still open compared the whole evening's
        // physical cash against only the sales made since midnight, showing a
        // large phantom surplus; and after a mid-day Z-report it kept counting
        // sales that had already been closed out.
        await using var c = _db.OpenConnection();

        var periodStart = "";
        await using (var last = c.CreateCommand())
        {
            last.CommandText = "SELECT closed_at FROM daily_closings ORDER BY id DESC LIMIT 1;";
            var value = await last.ExecuteScalarAsync(ct);
            if (value is string s && DateTimeOffset.TryParse(s, out var parsed))
                periodStart = parsed.ToString("O");
        }

        long cashSales;
        await using (var q = c.CreateCommand())
        {
            // R101: the cash portion of each sale - cash_portion_cents/
            // card_portion_cents when this row was written R101-aware (either
            // is nonzero, or both legitimately 0 on a genuine Mixed/Card row),
            // else derived from payment_method+total_cents for a historical
            // pre-R101 row (sales is append-only, so such a row can never be
            // backfilled - see the column's EnsureColumnAsync comment).
            q.CommandText = """
                SELECT COALESCE(SUM(
                  CASE
                    WHEN COALESCE(transaction_type,'SALE')='SALE' THEN
                      CASE WHEN cash_portion_cents<>0 OR card_portion_cents<>0 THEN cash_portion_cents
                           WHEN payment_method='CASH' THEN total_cents ELSE 0 END
                    WHEN transaction_type IN ('STORNO','RETURN') THEN
                      -(CASE WHEN cash_portion_cents<>0 OR card_portion_cents<>0 THEN cash_portion_cents
                             WHEN payment_method='CASH' THEN total_cents ELSE 0 END)
                    ELSE 0
                  END),0)
                FROM sales
                WHERE created_at > $since;
                """;
            q.Parameters.AddWithValue("$since", periodStart);
            cashSales = Convert.ToInt64(await q.ExecuteScalarAsync(ct));
        }

        long movements;
        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT COALESCE(SUM(
                  CASE
                    WHEN movement_type='EINLAGE' THEN amount_cents
                    WHEN movement_type='ENTNAHME' THEN -amount_cents
                    ELSE 0
                  END),0)
                FROM cash_movements
                WHERE created_at > $since;
                """;
            q.Parameters.AddWithValue("$since", periodStart);
            movements = Convert.ToInt64(await q.ExecuteScalarAsync(ct));
        }

        return openingBalanceCents + cashSales + movements;
    });
}}

public sealed class FiscalComplianceService : IFiscalComplianceService
{
    private readonly ISystemIdentityRepository _identity;
    private readonly ISettingsRepository _settings;
    private readonly ITseProvider _tse;
    private readonly ICommercialLicenseService _commercialLicense;

    public FiscalComplianceService(
        ISystemIdentityRepository identity,
        ISettingsRepository settings,
        ITseProvider tse,
        ICommercialLicenseService commercialLicense)
    {
        _identity = identity;
        _settings = settings;
        _tse = tse;
        _commercialLicense = commercialLicense;
    }
public async Task<FiscalReadinessReport> CheckAsync(CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var identity = await _identity.GetAsync(ct);
        var settings = await _settings.LoadAllAsync(ct);
        var companyReady = !string.IsNullOrWhiteSpace(settings.GetValueOrDefault("company.name")) && !string.IsNullOrWhiteSpace(settings.GetValueOrDefault("company.street")) && !string.IsNullOrWhiteSpace(settings.GetValueOrDefault("company.zip")) && !string.IsNullOrWhiteSpace(settings.GetValueOrDefault("company.city"));
        var tseActive = _tse.SdkAvailable && _tse.TransactionAvailable && string.Equals(settings.GetValueOrDefault("tse.status"), "AKTIV", StringComparison.OrdinalIgnoreCase);
        var edition = settings.GetValueOrDefault("installation.edition") ?? settings.GetValueOrDefault("business.mode") ?? "KIOSK";
        var commercialLicense = _commercialLicense.Check(edition);
        var isKiosk = string.Equals(edition, "KIOSK", StringComparison.OrdinalIgnoreCase);
        // These flags are intentionally code-level blockers, not editable settings.
        // They become true only after implementation + real hardware/export validation.
        const bool dsfinvkImplementedAndValidated = false;
        const bool ksichvReceiptValidated = false;
        const bool parkedOrderTseValidated = false;
        const bool pfandTaxValidated = false;
        const bool fiscalReleaseBuild = false;
        var items = new List<FiscalReadinessItem>
        {
            new("EAS_ID", "eAS-Seriennummer", !string.IsNullOrWhiteSpace(identity.EasSerial), identity.EasSerial),
            new("COMPANY", "Bon-Firmendaten", companyReady, companyReady ? "Vollständiger Name und Anschrift vorhanden." : "Firma, Straße, PLZ und Ort müssen vollständig sein."),
            new("TSE", "Zertifizierte TSE", tseActive, tseActive ? "Swissbit TSE ist als AKTIV erkannt." : "Swissbit SDK / reale TSE-Signierung ist noch nicht produktiv freigegeben."),
            new("DSFINVK", "DSFinV-K 2.4", dsfinvkImplementedAndValidated, "Preflight/Export-Gate ist implementiert; vollständiger DSFinV-K-Prüfdatensatz bleibt bis Z-/TSE-Datenmodell und offiziellem Descriptor gesperrt."),
            new("RECEIPT", "Beleg § 6 KassenSichV", ksichvReceiptValidated, "TSE-Transaktionsnummer, Signaturzähler und Prüfwert sind noch nicht real befüllt."),
            new("PARKEN_TSE", "Parken / Bestellung", parkedOrderTseValidated, "R83: IMBISS-Bestellannahme ruft bereits einen eigenen 'Bestellung-V1' TSE-Vorgang auf; das Format ist Entwurf und real noch nicht gegen echte TSE-Hardware/DSFinV-K validiert."),
            new("PFAND", isKiosk ? "Pfand-Steuerlogik" : "IMBISS Extra-Steuerlogik", !isKiosk || pfandTaxValidated, isKiosk ? "Pfandlogik ist noch nicht fachlich/fiskal abschließend validiert." : "Pfand ist in IMBISS nicht aktiv. Extras übernehmen die MwSt. aus der Warengruppe.", isKiosk),
            new("FISCAL_RELEASE", "TOR Produktivfreigabe", fiscalReleaseBuild, "Produktivfreigabe wird erst nach TSE-, DSFinV-K- und Belegtests gesetzt."),
            new("COMMERCIAL_LICENSE", "Kommerzielle Softwarelizenz", commercialLicense.IsActive, commercialLicense.Message)
        };
        var allowed = items.Where(x => x.Mandatory).All(x => x.Ready);
        return new FiscalReadinessReport(allowed, allowed ? "PRODUKTIV" : "TEST_ONLY", identity.EasSerial, settings.GetValueOrDefault("legal.dsfinvk.version") ?? "2.4", items);
    });
}}
