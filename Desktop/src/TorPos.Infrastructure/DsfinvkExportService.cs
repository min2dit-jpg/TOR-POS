using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// DSFinV-K 2.4 export (R131).
///
/// Until R131 this service only listed why no export existed and refused to
/// write one ("TOR erzeugt absichtlich keinen unvollständigen oder nur
/// scheinbar DSFinV-K-konformen Prüfdatensatz"). It now writes the 20 files of
/// the Einzelaufzeichnungs-, Stammdaten- and Kassenabschlussmodul together with
/// the unchanged official index.xml and GDPdU DTD, for every Kassenabschluss
/// (Z-Bericht) created in the chosen period.
///
/// The rule that made it refuse still holds, in a sharper form: a validation
/// runs the complete export in memory first. Anything that would make the data
/// wrong blocks it (missing company data, a VAT rate without a DSFinV-K key, a
/// Storno whose original receipt is in no closing, a value that does not fit
/// its column). What TOR does not record yet is not hidden either: each such
/// gap is reported as a warning and written into TOR-EXPORTPROTOKOLL.txt next
/// to the data, so nobody takes the file set for more complete than it is.
///
/// Vorgänge after the last Kassenabschluss belong to no closing yet (no Z_NR)
/// and are exported with the next one, as DSFinV-K 1.1.3 allows.
/// </summary>
public sealed class DsfinvkExportService : IDsfinvkExportService
{
    public const string ProtocolFileName = "TOR-EXPORTPROTOKOLL.txt";

    private readonly SqliteDatabase _db;
    private readonly ISettingsRepository _settings;

    public DsfinvkExportService(
        SqliteDatabase db,
        ISettingsRepository settings)
    {
        _db = db;
        _settings = settings;
    }

    public static IReadOnlyList<DsfinvkTable> OfficialTables { get; } =
        DsfinvkIndex.Parse(Encoding.UTF8.GetString(Resource("TorPos.Dsfinvk.index.xml")));

    public async Task<DsfinvkPreflightReport> ValidateAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var plan = await BuildPlanAsync(from, to, ct);
        return plan.Report;
    }

    public async Task<string> ExportAsync(DateTimeOffset from, DateTimeOffset to, string targetDirectory, CancellationToken ct = default)
    {
        var plan = await BuildPlanAsync(from, to, ct);
        if (!plan.Report.Ready)
        {
            throw new InvalidOperationException(
                "DSFinV-K Export gesperrt: " + string.Join(" | ", plan.Report.Issues.Where(x => x.Blocking).Select(x => x.Message)));
        }

        Directory.CreateDirectory(targetDirectory);
        var name = $"DSFinV-K_{SafeName(plan.Master.KasseSerial)}_{from:yyyyMMdd}-{to:yyyyMMdd}_{DateTime.Now:yyyyMMdd-HHmmss}";
        var final = Path.Combine(targetDirectory, name);
        if (Directory.Exists(final))
            throw new InvalidOperationException($"Zielordner existiert bereits: {final}");

        // Written into a working folder first and renamed when complete, so an
        // interrupted export never leaves a folder that looks finished.
        var working = final + ".unvollstaendig";
        if (Directory.Exists(working))
            throw new InvalidOperationException($"Ein unvollständiger Export liegt bereits vor: {working}");
        Directory.CreateDirectory(working);

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var fileLines = new List<string>();
        foreach (var table in OfficialTables)
        {
            var path = Path.Combine(working, table.FileName);
            var count = 0;
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            await using (var writer = new StreamWriter(stream, utf8))
            {
                writer.NewLine = DsfinvkCsv.RecordDelimiter;
                await writer.WriteLineAsync(DsfinvkCsv.Header(table));
                foreach (var line in plan.Csv[table.Name])
                {
                    await writer.WriteLineAsync(line);
                    count++;
                }
            }

            fileLines.Add($"  {table.FileName,-26} {count,7} Datensätze  SHA-256 {Sha256(path)}");
        }

        await File.WriteAllBytesAsync(Path.Combine(working, "index.xml"), Resource("TorPos.Dsfinvk.index.xml"), ct);
        await File.WriteAllBytesAsync(Path.Combine(working, "gdpdu-01-09-2004.dtd"), Resource("TorPos.Dsfinvk.gdpdu-01-09-2004.dtd"), ct);

        var protocol = new List<string>
        {
            "TOR POS Pro - DSFinV-K 2.4 Export",
            "",
            $"Erstellt:          {DsfinvkCsv.Timestamp(DateTimeOffset.Now)}",
            $"Software:          {plan.Master.SoftwareBrand} {plan.Master.SoftwareVersion}",
            $"Kasse:             {plan.Master.KasseSerial}",
            $"Zeitraum:          {DsfinvkCsv.Timestamp(from)} bis {DsfinvkCsv.Timestamp(to)}",
            $"Fiskalfreigabe:    {(FiscalRelease.ProductionAllowed ? "ja" : "NEIN - Prüf-/Testdatensatz")}",
            "",
            "Kassenabschlüsse:",
        };
        protocol.AddRange(plan.Closings.Select(c => $"  Z_NR {c.ZNumber,6}  {DsfinvkCsv.Timestamp(c.CreatedAt)}  {c.Vorgaenge,5} Vorgänge"));
        protocol.Add("");
        protocol.Add("Dateien (index.xml und gdpdu-01-09-2004.dtd unverändert aus DSFinV-K 2.4 des BZSt):");
        protocol.AddRange(fileLines);
        protocol.Add("");
        protocol.Add("Hinweise:");
        protocol.AddRange(plan.Report.Issues.Count == 0
            ? new[] { "  keine" }
            : plan.Report.Issues.Select(x => $"  {x.Code}: {x.Message}"));
        await File.WriteAllLinesAsync(Path.Combine(working, ProtocolFileName), protocol, utf8, ct);

        Directory.Move(working, final);
        return final;
    }

    // ------------------------------------------------------------------ plan

    private sealed record ClosingSummary(long ZNumber, DateTimeOffset CreatedAt, int Vorgaenge);

    private sealed record ExportPlan(
        DsfinvkPreflightReport Report,
        DsfinvkMasterData Master,
        IReadOnlyList<ClosingSummary> Closings,
        IReadOnlyDictionary<string, List<string>> Csv);

    private sealed record ClosingRow(long ZNumber, DateTimeOffset CreatedAt, string FromUtc, string ToUtc, DsfinvkMasterData? Master);

    private async Task<ExportPlan> BuildPlanAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        return await IoQueue.RunAsync(async () =>
        {
            var issues = new List<DsfinvkPreflightIssue>();
            var csv = OfficialTables.ToDictionary(t => t.Name, _ => new List<string>());
            var summaries = new List<ClosingSummary>();

            if (to < from)
                issues.Add(new("RANGE", "Enddatum liegt vor dem Startdatum."));

            await using var c = _db.OpenConnection();

            // R132: the master data as they are now - used for closings from
            // before R132, which did not store their own.
            var master = await DsfinvkMasterDataStore.CurrentAsync(c, ct);

            var closings = await LoadClosingsAsync(c, ct);
            var inRange = closings.Where(z => z.CreatedAt >= from && z.CreatedAt <= to).ToList();
            if (inRange.Count == 0)
                issues.Add(new("NO_CLOSING", "Im gewählten Zeitraum gibt es keinen Kassenabschluss (Z-Bericht). Exportiert werden nur abgeschlossene Zeiträume."));

            var withoutSnapshot = inRange.Where(z => z.Master is null).ToList();
            if (withoutSnapshot.Count > 0)
                CheckMasterData(master, issues, "");
            foreach (var closing in inRange.Where(z => z.Master is not null))
                CheckMasterData(closing.Master!, issues, $"Z_NR {closing.ZNumber}: ");

            var products = await LoadProductsAsync(c, ct);
            var tseMasterData = await TseMasterDataRepository.LoadAllAsync(c, ct);
            var tseWithoutMasterData = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var tseWithUnknownAlgorithm = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var salesWithoutImHaus = 0;
            var withoutStart = 0;
            void NoteTse(string transactionNumber, string serial) =>
                CheckTse(transactionNumber, serial, tseMasterData, tseWithoutMasterData, tseWithUnknownAlgorithm);
            var outages = await LoadOutagesAsync(c, ct);
            var allocationBySale = await LoadAllocationGroupsAsync(c, ct);
            foreach (var pair in await RestaurantBestellungExportLoader.LoadSaleAllocationGroupsAsync(c, ct))
                allocationBySale[pair.Key] = pair.Value;
            var allocationByTraining = await TrainingReceiptRepository.LoadAllocationGroupsAsync(c, ct);
            var sales = new SaleRepository(_db);

            var movementsWithoutCase = 0;
            var movementsWithoutTse = 0;
            var anyOrder = false;
            var anyCancelledOrder = false;
            var anyWithoutTse = new List<long>();

            foreach (var closing in inRange)
            {
                var saleList = new List<Sale>();
                foreach (var id in await IdsAsync(c,
                    "SELECT id FROM sales WHERE created_at_utc > $from AND created_at_utc <= $to ORDER BY receipt_number;",
                    closing, ct))
                {
                    var sale = await sales.GetByIdAsync(id, ct)
                        ?? throw new InvalidOperationException($"Verkauf {id} konnte nicht gelesen werden.");
                    saleList.Add(sale);
                    if (sale.ImHaus is null)
                        salesWithoutImHaus++;
                    if (sale.StartedAt is null)
                        withoutStart++;
                    NoteTse(sale.TseTransactionNumber, sale.TseSerialNumber);
                    if (!sale.TseOutage && sale.TseTransactionNumber.Length == 0)
                        anyWithoutTse.Add(sale.ReceiptNumber);
                }

                var movements = await LoadCashMovementsAsync(c, closing, ct);
                movementsWithoutCase += movements.Count(m => m.BusinessCase is null);
                movementsWithoutTse += movements.Count(m => m.Tse is null);
                foreach (var movement in movements)
                    if (movement.Tse is { Outage: false } signedMovement)
                        NoteTse(signedMovement.TransactionNumber, signedMovement.SerialNumber);

                var trainings = await TrainingReceiptRepository.LoadInPeriodAsync(c, closing.FromUtc, closing.ToUtc, ct);
                foreach (var training in trainings)
                {
                    if (training.Receipt.StartedAt is null)
                        withoutStart++;
                    CheckVat(training.Receipt.Lines, $"Trainingsvorgang {training.Receipt.ReceiptNumber}", issues);
                    if (training.Tse is { Outage: false } signedTraining)
                        NoteTse(signedTraining.TransactionNumber, signedTraining.SerialNumber);
                }

                var aborted = await TseVorgangService.LoadAbortedInPeriodAsync(c, closing.FromUtc, closing.ToUtc, ct);
                foreach (var vorgang in aborted)
                {
                    CheckVat(vorgang.Lines, $"Abgebrochener Vorgang {vorgang.Number}", issues);
                    if (vorgang.Tse is { Outage: false } signedAbort)
                        NoteTse(signedAbort.TransactionNumber, signedAbort.SerialNumber);
                }

                var orderRecords = await OrderBestellungRepository.LoadInPeriodAsync(c, closing.FromUtc, closing.ToUtc, ct);
                orderRecords.AddRange(
                    await RestaurantBestellungExportLoader.LoadInPeriodAsync(
                        c,
                        closing.FromUtc,
                        closing.ToUtc,
                        ct));
                foreach (var record in orderRecords)
                {
                    CheckVat(record.Lines, $"Bestellung P{record.ParkNumber:000000} ({record.Sequence})", issues);
                    if (record.Tse is { Outage: false } signedRecord)
                        NoteTse(signedRecord.TransactionNumber, signedRecord.SerialNumber);
                }

                var orders = await LoadOrdersAsync(c, closing, ct);
                anyOrder |= orders.Count > 0;
                anyCancelledOrder |= orders.Any(o => o.Cancelled);
                foreach (var loaded in orders)
                {
                    if (loaded.Order.VorgangStartedAt is null)
                        withoutStart++;
                    NoteTse(loaded.Order.TseTransactionNumber, loaded.Order.TseSerialNumber);
                }

                foreach (var sale in saleList)
                    CheckVat(sale.Lines, $"Beleg {sale.ReceiptNumber}", issues);
                foreach (var order in orders)
                    CheckVat(order.Order.Lines, $"Bestellung {order.Order.DisplayNumber}", issues);

                var input = new DsfinvkClosingInput
                {
                    Closing = new DsfinvkClosing(closing.ZNumber, closing.CreatedAt),
                    Master = closing.Master ?? master,
                    Sales = saleList,
                    CashMovements = movements,
                    Trainings = trainings,
                    Aborted = aborted,
                    OrderRecords = orderRecords,
                    Orders = orders.Select(o => o.Order).ToList(),
                    OriginalOf = originalId => FindOriginal(c, closings, originalId),
                    OutageReasonAt = at => OutageReasonAt(outages, at),
                    AllocationGroupBySaleId = allocationBySale,
                    AllocationGroupByTrainingId = allocationByTraining,
                    ProductOf = productId => products.TryGetValue(productId, out var p) ? p : null,
                    TseMasterDataOf = serial => tseMasterData.TryGetValue(serial, out var tse) ? tse : null,
                };

                if (issues.Any(x => x.Blocking))
                    continue;

                try
                {
                    var rows = DsfinvkClosingBuilder.Build(input);
                    foreach (var table in OfficialTables)
                        csv[table.Name].AddRange(rows.For(table.Name).Select(row => DsfinvkCsv.Row(table, row)));
                    summaries.Add(new ClosingSummary(closing.ZNumber, closing.CreatedAt, saleList.Count + movements.Count + orders.Count + trainings.Count + aborted.Count + orderRecords.Count));
                }
                catch (InvalidOperationException ex)
                {
                    issues.Add(new("DATA", $"Z_NR {closing.ZNumber}: {ex.Message}"));
                }
            }

            var lastClosing = closings.Count == 0 ? null : closings[^1];
            var openFrom = lastClosing?.ToUtc ?? "";
            var open = await ScalarLongAsync(c,
                "SELECT (SELECT COUNT(*) FROM sales WHERE created_at_utc > $from) + (SELECT COUNT(*) FROM cash_movements WHERE created_at_utc > $from AND movement_type IN ('EINLAGE','ENTNAHME') AND fiscal_mode <> 'TEST_ONLY') + (SELECT COUNT(*) FROM training_receipts WHERE created_at_utc > $from) + (SELECT COUNT(*) FROM aborted_vorgaenge WHERE ended_at_utc > $from) + (SELECT COUNT(*) FROM order_bestellungen WHERE created_at_utc > $from);",
                openFrom, ct);
            open += await RestaurantBestellungExportLoader.CountAfterAsync(
                c,
                openFrom,
                ct);
            if (open > 0)
                issues.Add(new("OPEN_PERIOD", open == 1
                    ? "1 Vorgang nach dem letzten Kassenabschluss gehört noch zu keinem Z-Bericht und ist nicht enthalten."
                    : $"{open} Vorgänge nach dem letzten Kassenabschluss gehören noch zu keinem Z-Bericht und sind nicht enthalten.", Blocking: false));

            // What TOR does not record yet. Stated, not hidden.
            if (!FiscalRelease.ProductionAllowed)
                issues.Add(new("TEST_DATA", "TOR ist fiskalisch nicht freigegeben - der Export ist ein Prüf-/Testdatensatz.", Blocking: false));
            if (withoutSnapshot.Count > 0)
                issues.Add(new("STAMMDATEN", (withoutSnapshot.Count == 1 ? "1 Kassenabschluss stammt" : $"{withoutSnapshot.Count} Kassenabschlüsse stammen") + $" aus der Zeit vor R132 ohne eigene Stammdaten; dafür werden die aktuellen Einstellungen verwendet (z. B. Z_NR {withoutSnapshot[0].ZNumber}).", Blocking: false));
            if (withoutStart > 0)
                issues.Add(new("BON_START", (withoutStart == 1 ? "1 Vorgang stammt" : $"{withoutStart} Vorgänge stammen") + " aus der Zeit vor R136, als Vorgangsbeginn und TSE-Startzeit nicht gespeichert wurden; BON_START und TSE_TA_START bleiben dort leer.", Blocking: false));
            if (salesWithoutImHaus > 0)
                issues.Add(new("INHAUS", (salesWithoutImHaus == 1 ? "1 Verkauf stammt" : $"{salesWithoutImHaus} Verkäufe stammen") + " aus der Zeit vor R133, als Im Haus/Außer Haus nicht gespeichert wurde; INHAUS bleibt dort leer (die Steuersätze sind korrekt erfasst).", Blocking: false));
            if (tseWithoutMasterData.Count > 0)
                issues.Add(new("TSE_STAMMDATEN", $"Für TSE {string.Join(", ", tseWithoutMasterData)} liegen Zertifikat, öffentlicher Schlüssel, Signaturalgorithmus und Zeitformat nicht vor. Bitte einen TSE-Export (TAR) erstellen - TOR übernimmt die Daten daraus.", Blocking: false));
            if (tseWithUnknownAlgorithm.Count > 0)
                issues.Add(new("TSE_ALGORITHMUS", $"Der Signaturalgorithmus von TSE {string.Join(", ", tseWithUnknownAlgorithm)} ist keinem Namen aus DSFinV-K Anhang E zugeordnet; TSE_SIG_ALGO bleibt leer.", Blocking: false));
            if (movementsWithoutCase > 0)
                issues.Add(new("KASSENBEWEGUNG", $"{movementsWithoutCase} Einlage(n)/Entnahme(n) aus der Zeit vor R134 ohne Geschäftsvorfall-Art; sie erscheinen als allgemeine Einzahlung/Auszahlung.", Blocking: false));
            if (movementsWithoutTse > 0)
                issues.Add(new("KASSENBEWEGUNG_TSE", $"{movementsWithoutTse} Einlage(n)/Entnahme(n) ohne gespeichertes TSE-Ergebnis (vor R134 wurden sie nicht abgesichert).", Blocking: false));
            if (anyOrder)
                issues.Add(new("BESTELLUNG", "Bestellungen aus der Zeit vor R137 werden mit ihrem zuletzt gespeicherten Positionsstand exportiert; ihre Änderungen nach der TSE-Signierung sind nicht einzeln nachvollziehbar.", Blocking: false));
            if (anyCancelledOrder)
                issues.Add(new("BESTELLSTORNO", "Stornierte Bestellungen aus der Zeit vor R137 sind nicht als eigene TSE-gesicherte Gegenbuchung erfasst (DSFinV-K 4.2.3).", Blocking: false));
            if (anyWithoutTse.Count > 0)
                issues.Add(new("OHNE_TSE", $"{anyWithoutTse.Count} Belege ohne gespeichertes TSE-Ergebnis (z. B. Beleg {anyWithoutTse[0]}).", Blocking: false));

            var ready = issues.All(x => !x.Blocking);
            if (!ready)
            {
                csv = OfficialTables.ToDictionary(t => t.Name, _ => new List<string>());
                summaries.Clear();
            }

            return new ExportPlan(
                new DsfinvkPreflightReport(Ready: ready, Version: DsfinvkClosingBuilder.TaxonomyVersion, Issues: issues),
                master,
                summaries,
                csv);
        });
    }

    private static void CheckTse(
        string transactionNumber,
        string serial,
        IReadOnlyDictionary<string, TseMasterData> known,
        ISet<string> missing,
        ISet<string> unknownAlgorithm)
    {
        if (transactionNumber.Length == 0 || serial.Length == 0)
            return;
        if (!known.TryGetValue(serial, out var tse))
            missing.Add(serial);
        else if (tse.SignatureAlgorithm.Length == 0)
            unknownAlgorithm.Add($"{serial} ({tse.SignatureAlgorithmOid})");
    }

    private static void CheckMasterData(DsfinvkMasterData master, List<DsfinvkPreflightIssue> issues, string prefix)
    {
        void Add(string message) => issues.Add(new("MASTER_DATA", prefix + message));

        void Required(string value, string key, string label, int maxLength)
        {
            if (value.Length == 0)
                Add($"Pflicht-Stammdatum fehlt: {label} ({key})");
            else if (value.Length > maxLength)
                Add($"{label} ist länger als {maxLength} Zeichen ({key}).");
        }

        Required(master.CompanyName, "company.name", "Firmenname", 60);
        Required(master.Street, "company.street", "Straße", 60);
        Required(master.Zip, "company.zip", "PLZ", 10);
        Required(master.City, "company.city", "Ort", 62);

        // § 14 Abs. 4 Nr. 2 UStG, DSFinV-K Anhang E: Steuernummer or USt-IdNr.
        if (master.TaxNumber.Length == 0 && master.VatId.Length == 0)
            Add("Steuernummer (company.tax_no) oder USt-IdNr. (company.vat_id) muss angegeben sein.");
        if (master.TaxNumber.Length > 20)
            Add("Steuernummer ist länger als 20 Zeichen.");
        if (master.VatId.Length > 15)
            Add("USt-IdNr. ist länger als 15 Zeichen.");
        if (master.SoftwareVersion.Length > 50)
            Add("Softwareversion ist länger als 50 Zeichen.");

        if (master.KasseSerial.Length == 0 || master.KasseSerial.Length > 70 || master.KasseSerial.IndexOfAny(new[] { '/', '_' }) >= 0)
            Add("Seriennummer der Kasse fehlt oder enthält '/' bzw. '_' (DSFinV-K Anhang E).");
    }

    private static void CheckVat(IEnumerable<CartLine> lines, string where, List<DsfinvkPreflightIssue> issues)
    {
        foreach (var rate in lines.SelectMany(MenuVatPolicy.EffectiveRates).Distinct())
        {
            try { DsfinvkClosingBuilder.VatKey(rate); }
            catch (UnsupportedVatRateException ex) { issues.Add(new("VAT", $"{where}: {ex.Message}")); }
        }
    }

    // --------------------------------------------------------------- loading

    private static async Task<List<ClosingRow>> LoadClosingsAsync(SqliteConnection c, CancellationToken ct)
    {
        var closings = new List<ClosingRow>();
        await using var q = c.CreateCommand();
        q.CommandText = "SELECT z_number,created_at,period_from,period_to,master_data FROM z_report_archive ORDER BY z_number;";
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            closings.Add(new ClosingRow(
                r.GetInt64(0),
                DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture),
                UtcText(DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture)),
                UtcText(DateTimeOffset.Parse(r.GetString(3), CultureInfo.InvariantCulture)),
                DsfinvkMasterDataRules.Deserialize(r.GetString(4))));
        }

        return closings;
    }

    /// <summary>
    /// The same UTC text form as the generated created_at_utc columns, so a
    /// closing's Vorgänge are selected exactly the way the Z-Bericht counted
    /// them (after the previous closing, up to and including this one).
    /// </summary>
    private static string UtcText(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static async Task<List<long>> IdsAsync(SqliteConnection c, string sql, ClosingRow closing, CancellationToken ct)
    {
        var ids = new List<long>();
        await using var q = c.CreateCommand();
        q.CommandText = sql;
        q.Parameters.AddWithValue("$from", closing.FromUtc);
        q.Parameters.AddWithValue("$to", closing.ToUtc);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            ids.Add(r.GetInt64(0));
        return ids;
    }

    private static async Task<List<DsfinvkCashMovement>> LoadCashMovementsAsync(SqliteConnection c, ClosingRow closing, CancellationToken ct)
    {
        var movements = new List<DsfinvkCashMovement>();
        await using var q = c.CreateCommand();
        // R134: only fiscal movements. Test entries (fiscal_mode TEST_ONLY)
        // never reach a closing, as for R132's closing guard.
        q.CommandText = """
            SELECT m.id,m.created_at,m.movement_type,m.amount_cents,m.reason,m.actor,m.business_case,
                   t.movement_id,t.serial_number,t.transaction_number,t.signature_counter,t.signature,
                   t.log_time,t.outage,t.outage_reason,t.start_log_time
            FROM cash_movements m
            LEFT JOIN cash_movement_tse_signatures t ON t.movement_id=m.id
            WHERE m.created_at_utc > $from AND m.created_at_utc <= $to
              AND m.movement_type IN ('EINLAGE','ENTNAHME')
              AND m.fiscal_mode <> 'TEST_ONLY'
            ORDER BY m.id;
            """;
        q.Parameters.AddWithValue("$from", closing.FromUtc);
        q.Parameters.AddWithValue("$to", closing.ToUtc);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            CashBusinessCase? businessCase = Enum.TryParse<CashBusinessCase>(r.GetString(6), out var parsed) ? parsed : null;
            var tse = r.IsDBNull(7)
                ? null
                : new DsfinvkTseResult(
                    r.GetString(8),
                    r.GetString(9),
                    r.GetString(10),
                    r.GetString(11),
                    string.IsNullOrWhiteSpace(r.GetString(12)) ? null : DateTimeOffset.Parse(r.GetString(12), CultureInfo.InvariantCulture),
                    r.GetInt64(13) != 0,
                    r.GetString(14),
                    string.IsNullOrWhiteSpace(r.GetString(15)) ? null : DateTimeOffset.Parse(r.GetString(15), CultureInfo.InvariantCulture));

            movements.Add(new DsfinvkCashMovement(
                r.GetInt64(0),
                DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture),
                r.GetString(2) == "EINLAGE" ? CashMovementKind.Einlage : CashMovementKind.Entnahme,
                r.GetInt64(3),
                r.GetString(4),
                r.GetString(5),
                businessCase,
                tse));
        }

        return movements;
    }

    private sealed record LoadedOrder(ParkedReceipt Order, bool Cancelled);

    /// <summary>
    /// Orders that were handed to the TSE (signed, or recorded as an outage).
    /// Simulation and training orders never reach the TSE and are not
    /// Vorgänge in the fiscal sense.
    /// </summary>
    private static async Task<List<LoadedOrder>> LoadOrdersAsync(SqliteConnection c, ClosingRow closing, CancellationToken ct)
    {
        var orders = new List<LoadedOrder>();
        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT id,park_number,pickup_number,created_at,updated_at,created_by,
                       discount_cents,total_cents,status,COALESCE(im_haus,0),
                       tse_client_id,tse_transaction_number,tse_signature_counter,
                       tse_serial_number,tse_signature,tse_log_time,tse_outage,
                       vorgang_started_at,COALESCE(tse_start_log_time,'')
                FROM parked_receipts
                WHERE COALESCE(is_training,0)=0
                  AND (tse_transaction_number<>'' OR tse_outage=1)
                  AND status<>'SIMULATED'
                  AND NOT EXISTS (SELECT 1 FROM order_bestellungen b WHERE b.parked_receipt_id=parked_receipts.id)
                ORDER BY park_number;
                """;
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var createdAt = DateTimeOffset.Parse(r.GetString(3), CultureInfo.InvariantCulture);
                var createdUtc = UtcText(createdAt);
                if (string.CompareOrdinal(createdUtc, closing.FromUtc) <= 0 || string.CompareOrdinal(createdUtc, closing.ToUtc) > 0)
                    continue;

                orders.Add(new LoadedOrder(new ParkedReceipt
                {
                    Id = r.GetInt64(0),
                    ParkNumber = r.GetInt64(1),
                    PickupNumber = r.GetInt64(2),
                    CreatedAt = createdAt,
                    UpdatedAt = DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture),
                    CreatedBy = r.GetString(5),
                    DiscountCents = r.GetInt64(6),
                    TotalCents = r.GetInt64(7),
                    ImHaus = r.GetInt64(9) != 0,
                    TseClientId = r.GetString(10),
                    TseTransactionNumber = r.GetString(11),
                    TseSignatureCounter = r.GetString(12),
                    TseSerialNumber = r.GetString(13),
                    TseSignature = r.GetString(14),
                    TseLogTime = string.IsNullOrWhiteSpace(r.GetString(15)) ? null : DateTimeOffset.Parse(r.GetString(15), CultureInfo.InvariantCulture),
                    TseOutage = r.GetInt64(16) != 0,
                    VorgangStartedAt = r.IsDBNull(17) ? null : DateTimeOffset.Parse(r.GetString(17), CultureInfo.InvariantCulture),
                    TseStartLogTime = string.IsNullOrWhiteSpace(r.GetString(18)) ? null : DateTimeOffset.Parse(r.GetString(18), CultureInfo.InvariantCulture),
                }, r.GetString(8) == "CANCELLED"));
            }
        }

        foreach (var order in orders)
        {
            var lines = new List<CartLine>();
            await using var q = c.CreateCommand();
            q.CommandText = """
                SELECT product_id,product_name,variant_name,barcode,
                       CASE WHEN COALESCE(quantity_milli,0)<>0 THEN quantity_milli ELSE CAST(ROUND(quantity*1000.0) AS INTEGER) END,
                       unit_price_cents,vat_rate,pfand_cents,
                       COALESCE(list_unit_price_cents,0),COALESCE(promotion_id,0),COALESCE(promotion_name,''),
                       COALESCE(promotion_percent,0),COALESCE(promotion_discount_unit_cents,0),
                       line_total_cents,
                       COALESCE(NULLIF(unit,''),(SELECT p.unit FROM products p WHERE p.id=parked_receipt_items.product_id),'Stück')
                FROM parked_receipt_items WHERE parked_receipt_id=$id ORDER BY id;
                """;
            q.Parameters.AddWithValue("$id", order.Order.Id);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                lines.Add(new CartLine
                {
                    ProductId = r.GetInt64(0),
                    ProductName = r.GetString(1),
                    VariantName = r.GetString(2),
                    Barcode = r.GetString(3),
                    Quantity = QuantityStorage.FromMilli(r.GetInt64(4)),
                    UnitPriceCents = r.GetInt64(5),
                    VatRate = Convert.ToDecimal(r.GetDouble(6)),
                    PfandCents = r.GetInt64(7),
                    ListUnitPriceCents = r.GetInt64(8) > 0 ? r.GetInt64(8) : r.GetInt64(5) + r.GetInt64(12),
                    PromotionId = r.GetInt64(9),
                    PromotionName = r.GetString(10),
                    PromotionPercent = r.GetInt32(11),
                    PromotionDiscountUnitCents = r.GetInt64(12),
                    // V-1/K-1: stored total and unit, as signed and printed.
                    PersistedLineTotalCents = r.GetInt64(13),
                    Unit = r.GetString(14),
                });
            }

            order.Order.Lines = lines;
        }

        return orders;
    }

    private static async Task<Dictionary<long, string>> LoadAllocationGroupsAsync(SqliteConnection c, CancellationToken ct)
    {
        var groups = new Dictionary<long, string>();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT cashed_sale_id,park_number FROM parked_receipts
            WHERE cashed_sale_id IS NOT NULL
              AND COALESCE(is_training,0)=0
              AND (tse_transaction_number<>'' OR tse_outage=1
                   OR EXISTS (SELECT 1 FROM order_bestellungen b WHERE b.parked_receipt_id=parked_receipts.id));
            """;
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            groups[r.GetInt64(0)] = DsfinvkClosingBuilder.OrderAllocationGroup(new ParkedReceipt { ParkNumber = r.GetInt64(1) });
        return groups;
    }

    private static async Task<Dictionary<long, DsfinvkProductInfo>> LoadProductsAsync(SqliteConnection c, CancellationToken ct)
    {
        var products = new Dictionary<long, DsfinvkProductInfo>();
        await using var q = c.CreateCommand();
        q.CommandText = "SELECT id,sku,unit FROM products;";
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            products[r.GetInt64(0)] = new DsfinvkProductInfo(r.GetString(1), r.GetString(2));
        return products;
    }

    private sealed record OutageRow(DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string Reason);

    private static async Task<List<OutageRow>> LoadOutagesAsync(SqliteConnection c, CancellationToken ct)
    {
        var outages = new List<OutageRow>();
        await using var q = c.CreateCommand();
        q.CommandText = "SELECT started_at,ended_at,reason FROM tse_outage_log ORDER BY id;";
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            outages.Add(new OutageRow(
                DateTimeOffset.Parse(r.GetString(0), CultureInfo.InvariantCulture),
                string.IsNullOrWhiteSpace(r.GetString(1)) ? null : DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture),
                r.GetString(2)));
        }

        return outages;
    }

    /// <summary>
    /// A sale is committed before it is signed, so an outage discovered while
    /// signing is opened a moment AFTER the sale's own timestamp. The record
    /// that was open at the sale, or opened within two minutes after it, is the
    /// one that documents it.
    /// </summary>
    private static string OutageReasonAt(IReadOnlyList<OutageRow> outages, DateTimeOffset at) =>
        outages
            .Where(o => o.StartedAt <= at.AddMinutes(2) && (o.EndedAt is null || o.EndedAt >= at))
            .OrderByDescending(o => o.StartedAt)
            .Select(o => o.Reason)
            .FirstOrDefault() ?? "";

    private static DsfinvkOriginalReference? FindOriginal(SqliteConnection c, IReadOnlyList<ClosingRow> closings, long originalSaleId)
    {
        using var q = c.CreateCommand();
        q.CommandText = "SELECT receipt_number,created_at_utc FROM sales WHERE id=$id;";
        q.Parameters.AddWithValue("$id", originalSaleId);
        using var r = q.ExecuteReader();
        if (!r.Read())
            return null;

        var receipt = r.GetInt64(0);
        var createdUtc = r.GetString(1);
        var closing = closings.FirstOrDefault(z =>
            string.CompareOrdinal(createdUtc, z.FromUtc) > 0 && string.CompareOrdinal(createdUtc, z.ToUtc) <= 0);

        return closing is null
            ? null
            : new DsfinvkOriginalReference(closing.ZNumber, closing.CreatedAt, DsfinvkClosingBuilder.SaleBonId(receipt));
    }

    // --------------------------------------------------------------- helpers

    private static async Task<long> ScalarLongAsync(SqliteConnection c, string sql, string from, CancellationToken ct)
    {
        await using var q = c.CreateCommand();
        q.CommandText = sql;
        q.Parameters.AddWithValue("$from", from);
        return Convert.ToInt64(await q.ExecuteScalarAsync(ct));
    }

    private static byte[] Resource(string name)
    {
        using var stream = typeof(DsfinvkExportService).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Eingebettete DSFinV-K-Datei fehlt: {name}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string SafeName(string value)
    {
        foreach (var ch in Path.GetInvalidFileNameChars())
            value = value.Replace(ch, '-');
        return value.Length == 0 ? "KASSE" : value;
    }
}
