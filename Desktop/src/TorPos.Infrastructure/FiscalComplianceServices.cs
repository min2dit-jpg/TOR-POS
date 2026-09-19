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

        // R134: an Einlage/Entnahme has to say what it is (AEAO zu § 146a
        // Nr. 1.10.2); a Kassensturz count is not a cash flow and has no type.
        if (request.Kind is CashMovementKind.Einlage or CashMovementKind.Entnahme)
        {
            if (request.BusinessCase is not CashBusinessCase businessCase)
                throw new InvalidOperationException("Art der Kassenbewegung fehlt (z. B. Geldtransit, Privatentnahme).");
            if (!CashBusinessCases.Allowed(request.Kind, businessCase))
                throw new InvalidOperationException($"{businessCase} passt nicht zu einer {request.Kind}.");
            // R139: a difference only comes from a Kassensturz (BookCashCountAsync).
            if (businessCase == CashBusinessCase.DifferenzSollIst)
                throw new InvalidOperationException("Eine Kassendifferenz wird nur beim Kassensturz gebucht.");
        }
        else if (request.BusinessCase is not null)
        {
            throw new InvalidOperationException("Ein Kassensturz hat keine Geschäftsvorfall-Art.");
        }

        // A production movement is a fiscal Vorgang - the same circuit
        // breaker as every real sale.
        if (request.Production)
            FiscalRelease.RequireProduction();
        var fiscalMode = request.Production ? CashMovement.ProductionMode : CashMovement.TestMode;
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
              created_at,movement_type,amount_cents,reason,actor,fiscal_mode,business_case)
            VALUES($time,$type,$amount,$reason,$actor,$mode,$case);
            SELECT last_insert_rowid();
            """;
        q.Parameters.AddWithValue("$mode", fiscalMode);
        q.Parameters.AddWithValue("$case", request.BusinessCase?.ToString() ?? "");
        q.Parameters.AddWithValue("$time", now.ToString("O"));
        q.Parameters.AddWithValue("$type", type);
        q.Parameters.AddWithValue("$amount", request.AmountCents);
        q.Parameters.AddWithValue("$reason", request.Reason.Trim());
        q.Parameters.AddWithValue("$actor", actor);
        var id = Convert.ToInt64(await q.ExecuteScalarAsync(ct));
        await _audit.WriteAsync(actor, "CASH_MOVEMENT", "CASH_MOVEMENT", id.ToString(), $"{type}; case={request.BusinessCase}; mode={fiscalMode}; amount_cents={request.AmountCents}; reason={request.Reason.Trim()}", ct);
        return new CashMovement(id, now, request.Kind, request.AmountCents, request.Reason.Trim(), actor, fiscalMode, request.BusinessCase);
    });
}

public async Task RecordTseResultAsync(long movementId, SaleTseResult result, CancellationToken ct = default)
{
    await IoQueue.RunAsync(async () =>
    {
        await using var c = _db.OpenConnection();
        await using (var existing = c.CreateCommand())
        {
            existing.CommandText = "SELECT COUNT(*) FROM cash_movement_tse_signatures WHERE movement_id=$id;";
            existing.Parameters.AddWithValue("$id", movementId);
            if (Convert.ToInt64(await existing.ExecuteScalarAsync(ct)) > 0)
                throw new InvalidOperationException(
                    $"Für Kassenbewegung {movementId} existiert bereits ein endgültiger TSE-Eintrag. Ein nachträgliches Signieren ist nicht vorgesehen (R129).");
        }

        await using var q = c.CreateCommand();
        q.CommandText = """
            INSERT INTO cash_movement_tse_signatures(
              movement_id,client_id,transaction_number,signature_counter,serial_number,
              signature,log_time,outage,outage_reason,created_at,start_log_time)
            VALUES($id,$client,$tanr,$sigz,$serial,$sig,$log,$outage,$reason,$at,$startlog);
            """;
        q.Parameters.AddWithValue("$id", movementId);
        q.Parameters.AddWithValue("$startlog", result.StartLogTime?.ToString("O") ?? "");
        q.Parameters.AddWithValue("$client", result.ClientId);
        q.Parameters.AddWithValue("$tanr", result.TransactionNumber);
        q.Parameters.AddWithValue("$sigz", result.SignatureCounter);
        q.Parameters.AddWithValue("$serial", result.SerialNumber);
        q.Parameters.AddWithValue("$sig", result.Signature);
        q.Parameters.AddWithValue("$log", result.LogTime?.ToString("O") ?? "");
        q.Parameters.AddWithValue("$outage", result.Signed ? 0 : 1);
        q.Parameters.AddWithValue("$reason", result.OutageMessage);
        q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        await q.ExecuteNonQueryAsync(ct);
    });
}

// R139: the cash of the period sold in cash - a Storno/Retoure paid back in cash
// takes it out again (R92). Pre-R101 rows derive the split from the method.
private const string CashSalesSum = """
    COALESCE(SUM(
      CASE
        WHEN COALESCE(transaction_type,'SALE')='SALE' THEN
          CASE WHEN cash_portion_cents<>0 OR card_portion_cents<>0 THEN cash_portion_cents
               WHEN payment_method='CASH' THEN total_cents ELSE 0 END
        WHEN transaction_type IN ('STORNO','RETURN') THEN
          -(CASE WHEN cash_portion_cents<>0 OR card_portion_cents<>0 THEN cash_portion_cents
                 WHEN payment_method='CASH' THEN total_cents ELSE 0 END)
        ELSE 0
      END),0)
    """;

/// <summary>
/// R139: the calculated cash in the drawer, without a break at a closing.
/// DSFinV-K Anhang C (Anfangsbestand): cash taken out at a closing is booked
/// (Geldtransit); the next Anfangsbestand is what is left. The Z-Bericht moves no
/// money, so resetting the calculated cash to a fixed start amount at every
/// closing - as the Kassensturz did until R139 - made an unbooked removal look
/// like a correct drawer and cash left in the drawer look like a surplus. The
/// origin is the last confirmed Kassensturz of the same mode.
/// </summary>
public Task<CashBalance> GetCashBalanceAsync(long initialBalanceCents, bool production, CancellationToken ct = default) =>
    IoQueue.RunAsync(async () =>
    {
        await using var c = _db.OpenConnection();
        return await BalanceAsync(c, null, initialBalanceCents, production, ct);
    });

/// <summary>
/// R139: a confirmed Kassensturz. In one database transaction: the calculated
/// cash, the difference booked as DifferenzSollIst (a surplus as Einlage, a
/// shortfall as Entnahme), and the count itself as the new origin. A real
/// booking is a fiscal Vorgang behind the same circuit breaker as a sale; the
/// caller signs the difference with the TSE.
/// </summary>
public Task<CashCountBooking> BookCashCountAsync(long countedCents, long initialBalanceCents, string note, bool production, string actor, CancellationToken ct = default) =>
    IoQueue.RunAsync(async () =>
    {
        if (countedCents < 0)
            throw new InvalidOperationException("Der gezählte Bestand darf nicht negativ sein.");
        if (production)
            FiscalRelease.RequireProduction();

        var mode = production ? CashMovement.ProductionMode : CashMovement.TestMode;
        var text = string.IsNullOrWhiteSpace(note) ? "Kassensturz" : note.Trim();

        CashBalance balance;
        CashMovement? difference = null;
        CashMovement count;
        await using (var c = _db.OpenConnection())
        {
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
            balance = await BalanceAsync(c, tx, initialBalanceCents, production, ct);
            var cents = countedCents - balance.ExpectedCents;
            if (cents != 0)
            {
                var kind = cents > 0 ? CashMovementKind.Einlage : CashMovementKind.Entnahme;
                difference = await InsertAsync(c, tx, kind, Math.Abs(cents), $"Kassendifferenz · {text}", actor, mode, CashBusinessCase.DifferenzSollIst, ct);
            }

            count = await InsertAsync(c, tx, CashMovementKind.CashCount, countedCents, text, actor, mode, null, ct);
            await tx.CommitAsync(ct);
        }

        await _audit.WriteAsync(actor, "CASH_COUNT", "CASH_MOVEMENT", count.Id.ToString(),
            $"mode={mode}; soll_cents={balance.ExpectedCents}; ist_cents={countedCents}; differenz_cents={countedCents - balance.ExpectedCents}; " +
            $"differenz_bewegung={difference?.Id.ToString() ?? "-"}; notiz={text}", ct);
        return new CashCountBooking(balance.ExpectedCents, countedCents, difference, count);
    });

private static async Task<CashBalance> BalanceAsync(SqliteConnection c, SqliteTransaction? tx, long initialBalanceCents, bool production, CancellationToken ct)
{
    var testMode = production ? 0 : 1;
    long originId = 0;
    var originUtc = "";
    var baseCents = initialBalanceCents;
    DateTimeOffset? countedAt = null;

    await using (var q = c.CreateCommand())
    {
        q.Transaction = tx;
        q.CommandText = """
            SELECT id,created_at,created_at_utc,amount_cents FROM cash_movements
            WHERE movement_type='CASH_COUNT' AND (fiscal_mode='TEST_ONLY')=$test
            ORDER BY id DESC LIMIT 1;
            """;
        q.Parameters.AddWithValue("$test", testMode);
        await using var r = await q.ExecuteReaderAsync(ct);
        if (await r.ReadAsync(ct))
        {
            originId = r.GetInt64(0);
            countedAt = DateTimeOffset.Parse(r.GetString(1), System.Globalization.CultureInfo.InvariantCulture);
            originUtc = r.GetString(2);
            baseCents = r.GetInt64(3);
        }
    }

    // Sales are only ever booked for real; a test till has none.
    long sales = 0;
    if (production)
    {
        await using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = $"SELECT {CashSalesSum} FROM sales WHERE created_at_utc > $since;";
        q.Parameters.AddWithValue("$since", originUtc);
        sales = Convert.ToInt64(await q.ExecuteScalarAsync(ct));
    }

    long movements;
    await using (var q = c.CreateCommand())
    {
        q.Transaction = tx;
        q.CommandText = """
            SELECT COALESCE(SUM(CASE WHEN movement_type='EINLAGE' THEN amount_cents
                                     WHEN movement_type='ENTNAHME' THEN -amount_cents ELSE 0 END),0)
            FROM cash_movements
            WHERE id > $origin AND (fiscal_mode='TEST_ONLY')=$test;
            """;
        q.Parameters.AddWithValue("$origin", originId);
        q.Parameters.AddWithValue("$test", testMode);
        movements = Convert.ToInt64(await q.ExecuteScalarAsync(ct));
    }

    return new CashBalance(baseCents + sales + movements, countedAt, baseCents);
}

private static async Task<CashMovement> InsertAsync(
    SqliteConnection c, SqliteTransaction tx, CashMovementKind kind, long cents, string reason, string actor,
    string mode, CashBusinessCase? businessCase, CancellationToken ct)
{
    var now = DateTimeOffset.Now;
    await using var q = c.CreateCommand();
    q.Transaction = tx;
    q.CommandText = """
        INSERT INTO cash_movements(created_at,movement_type,amount_cents,reason,actor,fiscal_mode,business_case)
        VALUES($time,$type,$amount,$reason,$actor,$mode,$case);
        SELECT last_insert_rowid();
        """;
    q.Parameters.AddWithValue("$time", now.ToString("O"));
    q.Parameters.AddWithValue("$type", kind switch
    {
        CashMovementKind.Einlage => "EINLAGE",
        CashMovementKind.Entnahme => "ENTNAHME",
        _ => "CASH_COUNT"
    });
    q.Parameters.AddWithValue("$amount", cents);
    q.Parameters.AddWithValue("$reason", reason);
    q.Parameters.AddWithValue("$actor", actor);
    q.Parameters.AddWithValue("$mode", mode);
    q.Parameters.AddWithValue("$case", businessCase?.ToString() ?? "");
    var id = Convert.ToInt64(await q.ExecuteScalarAsync(ct));
    return new CashMovement(id, now, kind, cents, reason, actor, mode, businessCase);
}

// R92: same bug family as R88/R90/R91, found on the same sweep - counted
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
        // R144: the TSE has to log the till under its serial number.
        var kassenSeriennummer = KassenSeriennummer.From(identity.EasSerial);
        var clientIdReady = KassenSeriennummer.ClientIdMatches(settings.GetValueOrDefault("tse.client_id"), identity.EasSerial);
        var companyReady = !string.IsNullOrWhiteSpace(settings.GetValueOrDefault("company.name")) && !string.IsNullOrWhiteSpace(settings.GetValueOrDefault("company.street")) && !string.IsNullOrWhiteSpace(settings.GetValueOrDefault("company.zip")) && !string.IsNullOrWhiteSpace(settings.GetValueOrDefault("company.city"));
        var tseActive = _tse.SdkAvailable && _tse.TransactionAvailable && string.Equals(settings.GetValueOrDefault("tse.status"), "AKTIV", StringComparison.OrdinalIgnoreCase);
        var edition = settings.GetValueOrDefault("installation.edition") ?? settings.GetValueOrDefault("business.mode") ?? "KIOSK";
        var commercialLicense = _commercialLicense.Check(edition);
        var isKiosk = string.Equals(edition, "KIOSK", StringComparison.OrdinalIgnoreCase);
        // Acceptance evidence lives in the central, source-controlled
        // FiscalRelease qualification set. These are deliberately NOT editable
        // settings, so a till operator cannot self-enable production.
        var dsfinvkImplementedAndValidated = FiscalRelease.DsfinvkValidated;
        var ksichvReceiptValidated = FiscalRelease.KassenSichVReceiptValidated;
        var parkedOrderTseValidated = FiscalRelease.ParkedOrderTseValidated;
        var pfandTaxValidated = FiscalRelease.PfandTaxValidated;
        var items = new List<FiscalReadinessItem>
        {
            new("EAS_ID", "Kassen-Seriennummer", KassenSeriennummer.IsValid(kassenSeriennummer), kassenSeriennummer),
            new("TSE_CLIENT", "TSE-Client-ID = Kassen-Seriennummer", clientIdReady, clientIdReady
                ? "Die TSE protokolliert die Kasse unter ihrer Seriennummer."
                : $"Die TSE-Client-ID muss {kassenSeriennummer} lauten - dieselbe Nummer steht auf dem Bon, im DSFinV-K-Export (KASSE_SERIENNR) und in der Mitteilung nach § 146a Abs. 4 AO."),
            new("COMPANY", "Bon-Firmendaten", companyReady, companyReady ? "Vollständiger Name und Anschrift vorhanden." : "Firma, Straße, PLZ und Ort müssen vollständig sein."),
            new("TSE", "Zertifizierte TSE", tseActive, tseActive ? "Swissbit TSE ist als AKTIV erkannt." : "Swissbit SDK / reale TSE-Signierung ist noch nicht produktiv freigegeben."),
            new("KASSENSICHV_CODE", "KassenSichV 2026 Code-Prüfungen", true, "§ 2 Transaktions- und § 6 Belegprüfungen sind als ausführbare Guards aktiv; dies ersetzt keine TSE-Zertifizierung oder Hardware-Abnahme.", Mandatory: false),
            new("DSFINVK", "DSFinV-K 2.4", dsfinvkImplementedAndValidated, "DSFinV-K-2.4 Export, Preflight, Z-/TSE-/Beleg-/Bestellstrukturen sind implementiert. Externe/finale Validierung eines vollständigen Prüfdatensatzes steht noch aus."),
            new("RECEIPT", "Beleg § 6 KassenSichV", ksichvReceiptValidated, "Papier- und Digitalbeleg besitzen gemeinsame §-6-Pflichtfeld-/MwSt.-Prüfungen. Reale TSE-Daten, QR und 80-mm-Beleg müssen noch physisch abgenommen werden."),
            new("PARKEN_TSE", "Parken / Bestellung", parkedOrderTseValidated, "Bestellung-V1, Änderung, Storno und Abrechnungskreis sind implementiert. Reale TSE-/DSFinV-K-Abnahme der Bestellkette steht noch aus."),
            new("PFAND", isKiosk ? "Pfand-Steuerlogik" : "IMBISS Extra-Steuerlogik", !isKiosk || pfandTaxValidated, isKiosk ? "Pfandverkauf/-rückgabe und DSFinV-K-GV-Typen sind implementiert; die fachlich/fiskale Endabnahme steht noch aus." : "Pfand ist in IMBISS nicht aktiv. Extras übernehmen die MwSt. aus der Warengruppe.", isKiosk),
            new("TSE_E2E", "Physische TSE-End-to-End-Abnahme", FiscalRelease.PhysicalTseE2EValidated, "BAR-Testbon, Start/Finish, QR, Zähler, Seriennummer, TAR-Export, Ausfall und Restart-Recovery müssen mit der realen zertifizierten TSE belegt sein."),
            new("INDEPENDENT_REVIEW", "Unabhängige Fiskalprüfung", FiscalRelease.IndependentFiscalReviewValidated, "Vor Produktivfreigabe muss die dokumentierte unabhängige Prüfung der fiskalischen Kernpfade abgeschlossen sein."),
            new("FISCAL_RELEASE", "TOR Produktivfreigabe", FiscalRelease.Enabled, FiscalRelease.Enabled ? "Alle source-controlled Release-Qualifikationen sind erfüllt." : "Fehlende Freigaben: " + string.Join(", ", FiscalRelease.MissingQualifications())),
            new("COMMERCIAL_LICENSE", "Kommerzielle Softwarelizenz", commercialLicense.IsActive, commercialLicense.Message)
        };
        var allowed = items.Where(x => x.Mandatory).All(x => x.Ready);
        return new FiscalReadinessReport(allowed, allowed ? "PRODUKTIV" : "TEST_ONLY", kassenSeriennummer, settings.GetValueOrDefault("legal.dsfinvk.version") ?? "2.4", items);
    });
}}
