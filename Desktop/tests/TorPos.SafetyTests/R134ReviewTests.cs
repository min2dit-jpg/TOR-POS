using System.Text;
using TorPos.Core;
using TorPos.Infrastructure;

// R134: Einlage / Entnahme are Geschäftsvorfälle.
//
// AEAO zu § 146a Nr. 1.10.2 names Privatentnahme, Privateinlage,
// Wechselgeld-Einlage, Lohnzahlung aus der Kasse and Geldtransit as
// Geschäftsvorfälle; everything recorded as one has to be secured by the TSE
// (Nr. 1.8, 2.2.2). No receipt is required for them (Nr. 2.5.5). Before R134 a
// movement was only "EINLAGE"/"ENTNAHME" with a reason text, always stored as a
// test entry, and never signed.
public static class R134ReviewTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "r134-cash-movements");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r134.db"));

        using (var c = db.OpenConnection())
        {
            using var q = c.CreateCommand();
            q.CommandText = "SELECT (SELECT COUNT(*) FROM pragma_table_info('cash_movements') WHERE name='business_case') + (SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='cash_movement_tse_signatures');";
            assert(Convert.ToInt64(q.ExecuteScalar()) == 2 && SchemaMigrationService.TargetSchemaVersion >= 14,
                "R134 schema migration V14 adds the business case and the TSE record of cash movements");
        }

        assert(CashBusinessCases.For(CashMovementKind.Einlage).SequenceEqual(new[] { CashBusinessCase.Geldtransit, CashBusinessCase.Privateinlage, CashBusinessCase.Einzahlung }) &&
               CashBusinessCases.For(CashMovementKind.Entnahme).SequenceEqual(new[] { CashBusinessCase.Geldtransit, CashBusinessCase.Privatentnahme, CashBusinessCase.Lohnzahlung, CashBusinessCase.Auszahlung }) &&
               CashBusinessCases.For(CashMovementKind.CashCount).Count == 0,
            "R134 an Einlage can be Geldtransit, Privateinlage or another Einzahlung; an Entnahme Geldtransit, Privatentnahme, Lohnzahlung or another Auszahlung");

        CashMovement Movement(CashMovementKind kind, long cents, CashBusinessCase businessCase) =>
            new(1, DateTimeOffset.Now, kind, cents, "Test", "kasse1", CashMovement.ProductionMode, businessCase);
        assert(FiscalProcessData.CashMovementText(Movement(CashMovementKind.Einlage, 10000, CashBusinessCase.Privateinlage)) == "Beleg^0.00_0.00_0.00_0.00_100.00^100.00:Bar" &&
               FiscalProcessData.CashMovementText(Movement(CashMovementKind.Entnahme, 10000, CashBusinessCase.Privatentnahme)) == "Beleg^0.00_0.00_0.00_0.00_-100.00^-100.00:Bar",
            "R134 processData reproduces the Anhang I examples 'Privateinlage 100 bar' and 'Privatentnahme 100'");

        // ---------- recording rules ----------
        var audit = new AuditLogRepository(db);
        var repository = new CashMovementRepository(db, audit);
        async Task<string?> RefusalAsync(CashMovementRequest request)
        {
            try { await repository.AddAsync(request, "kasse1"); return null; }
            catch (InvalidOperationException ex) { return ex.Message; }
        }

        var noCase = await RefusalAsync(new CashMovementRequest(CashMovementKind.Einlage, 1000, "Wechselgeld"));
        var wrongCase = await RefusalAsync(new CashMovementRequest(CashMovementKind.Entnahme, 1000, "Privat", CashBusinessCase.Privateinlage));
        var production = await RefusalAsync(new CashMovementRequest(CashMovementKind.Entnahme, 1000, "Bank", CashBusinessCase.Geldtransit, Production: true));
        long stored;
        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM cash_movements;";
            stored = Convert.ToInt64(await q.ExecuteScalarAsync());
        }
        assert(noCase is not null && wrongCase is not null && production is not null && stored == 0,
            "R134 an Einlage/Entnahme without its type, with a type of the other direction, or as a real booking while the fiscal circuit breaker is off is refused and nothing is stored");

        var testEntry = await repository.AddAsync(new CashMovementRequest(CashMovementKind.Einlage, 10000, "Wechselgeld", CashBusinessCase.Geldtransit), "kasse1");
        assert(testEntry.FiscalMode == CashMovement.TestMode && testEntry.BusinessCase == CashBusinessCase.Geldtransit,
            "R134 in test mode the movement keeps its type and stays a test entry");

        // ---------- signing ----------
        var settings = new SettingsRepository(db);
        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["company.name"] = "R134 Imbiss",
            ["company.street"] = "Hauptstraße 1",
            ["company.zip"] = "10115",
            ["company.city"] = "Berlin",
            ["company.tax_no"] = "27/123/45678",
        });

        async Task<CashMovement> ProductionMovementAsync(CashMovementKind kind, long cents, CashBusinessCase businessCase, string reason)
        {
            await Task.Delay(15);
            var now = DateTimeOffset.Now;
            await using var c = db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = "INSERT INTO cash_movements(created_at,movement_type,amount_cents,reason,actor,fiscal_mode,business_case) VALUES($at,$type,$amount,$reason,'kasse1','PRODUCTION',$case); SELECT last_insert_rowid();";
            q.Parameters.AddWithValue("$at", now.ToString("O"));
            q.Parameters.AddWithValue("$type", kind == CashMovementKind.Einlage ? "EINLAGE" : "ENTNAHME");
            q.Parameters.AddWithValue("$amount", cents);
            q.Parameters.AddWithValue("$reason", reason);
            q.Parameters.AddWithValue("$case", businessCase.ToString());
            var id = Convert.ToInt64(await q.ExecuteScalarAsync());
            return new CashMovement(id, now, kind, cents, reason, "kasse1", CashMovement.ProductionMode, businessCase);
        }

        var outages = new TseOutageRepository(db, audit);
        var provider = new RecordingTseProvider();
        var signing = new CashMovementFiscalSigningService(new TseFailSafeService(provider, outages, audit), settings, repository);

        var inactive = await ProductionMovementAsync(CashMovementKind.Einlage, 5000, CashBusinessCase.Privateinlage, "Privat eingelegt");
        var inactiveResult = await signing.SignAsync(inactive, "kasse1");
        assert(!inactiveResult.Signed && provider.Starts.Count == 0 && await outages.GetOpenAsync() is not null,
            "R134 with the TSE not active a real movement is recorded as a documented TSE outage, without a transaction");
        await outages.CloseOpenAsync("kasse1");

        await settings.SaveManyAsync(new Dictionary<string, string> { ["tse.status"] = "AKTIV", ["tse.client_id"] = "KASSE-1" });
        var toBank = await ProductionMovementAsync(CashMovementKind.Entnahme, 20000, CashBusinessCase.Geldtransit, "Bankeinzahlung");
        var signedResult = await signing.SignAsync(toBank, "kasse1");
        assert(signedResult.Signed &&
               provider.Starts.Count == 1 && provider.Starts[0].ProcessData.Length == 0 && provider.Starts[0].ProcessType == "" &&
               provider.Finishes.Count == 1 && provider.Finishes[0].ProcessType == "Kassenbeleg-V1" &&
               Encoding.UTF8.GetString(provider.Finishes[0].ProcessData) == "Beleg^0.00_0.00_0.00_0.00_-200.00^-200.00:Bar",
            "R134 a real Geldtransit to the bank is signed as Kassenbeleg-V1: start empty, finish with the 0 % container and the cash outflow");

        var secondRecord = false;
        try { await repository.RecordTseResultAsync(toBank.Id, SaleTseResult.Outage("x")); }
        catch (InvalidOperationException) { secondRecord = true; }
        assert(secondRecord, "R134 the TSE record of a movement is final - no second result, no signing afterwards (R129)");

        provider.FailStart = true;
        var wages = await ProductionMovementAsync(CashMovementKind.Entnahme, 15000, CashBusinessCase.Lohnzahlung, "Vorschuss Aushilfe");
        var failed = await signing.SignAsync(wages, "kasse1");
        provider.FailStart = false;
        assert(!failed.Signed && failed.OutageMessage.Contains("Fake TSE Start failed"),
            "R134 a TSE failure while signing is recorded with its reason");

        // ---------- export ----------
        await Task.Delay(15);
        await new BusinessManagementService(db, settings, audit).CreateZArchiveAsync("kasse1", "TEST");
        var exporter = new DsfinvkExportService(db, settings);
        var from = DateTimeOffset.Now.AddHours(-1);
        var to = DateTimeOffset.Now.AddMinutes(1);
        var report = await exporter.ValidateAsync(from, to);
        var folder = await exporter.ExportAsync(from, to, Path.Combine(dir, "out"));

        var heads = File.ReadAllLines(Path.Combine(folder, "transactions.csv")).Skip(1).ToArray();
        var lines = File.ReadAllLines(Path.Combine(folder, "lines.csv")).Skip(1).ToArray();
        var tse = File.ReadAllLines(Path.Combine(folder, "transactions_tse.csv")).Skip(1).ToArray();
        string Line(CashMovement m, string[] rows) => rows.Single(r => r.Contains($";\"KB-{m.Id}\";"));

        assert(heads.Length == 3 && heads.All(h => !h.Contains($"\"KB-{testEntry.Id}\"")),
            "R134 the three real movements are exported; the test entry never reaches a closing");
        assert(Line(toBank, lines).Split(';')[8] == "\"Geldtransit\"" &&
               Line(inactive, lines).Split(';')[8] == "\"Privateinlage\"" &&
               Line(wages, lines).Split(';')[8] == "\"Lohnzahlung\"" &&
               Line(toBank, heads).Contains("\"Geldtransit (zur Bank oder in den Tresor)\""),
            "R134 GV_TYP is the chosen business case (DSFinV-K Anhang C), the receipt name says what it was");
        assert(Line(toBank, tse).Contains("\"Kassenbeleg-V1\"") && Line(toBank, tse).Contains("\"Beleg^0.00_0.00_0.00_0.00_-200.00^-200.00:Bar\"") &&
               Line(wages, tse).Contains("\"TSE-Ausfall: Fake TSE Start failed\"") &&
               Line(inactive, tse).Contains("TSE-Ausfall: TSE ist nicht aktiv"),
            "R134 TSE_Transaktionen carries the signed Kassenbeleg-V1 or the stored outage reason of each movement");
        assert(report.Ready && report.Issues.All(x => x.Code is not ("KASSENBEWEGUNG" or "KASSENBEWEGUNG_TSE")),
            "R134 movements recorded under R134 raise no hint about a missing type or TSE result");
    }

    private sealed class RecordingTseProvider : ITseProvider
    {
        public readonly List<TseTransactionStartRequest> Starts = new();
        public readonly List<TseTransactionFinishRequest> Finishes = new();
        public bool FailStart;

        public string ProviderId => "FAKE";
        public string DisplayName => "Fake TSE";
        public string PreferredProduct => "Fake";
        public bool SdkAvailable => true;
        public bool ActivationAvailable => true;
        public bool TransactionAvailable => true;
        public bool ExportAvailable => true;

        public TseRuntimeStatus GetRuntimeStatus() => new(true, true, "1.0", "", "OK");

        public Task<IReadOnlyList<string>> FindSdkLibrariesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public TseRuntimeStatus ConfigureSdkLibrary(string libraryPath) => GetRuntimeStatus();

        public Task<TseProbeResult> ProbeAsync(CancellationToken ct = default) =>
            Task.FromResult(new TseProbeResult(TseConnectionState.Ready, "OK"));

        public Task<TseActivationResult> ActivateAsync(TseActivationRequest request, CancellationToken ct = default) =>
            Task.FromResult(new TseActivationResult(true, "OK"));

        public Task<TseTransactionResult> StartTransactionAsync(TseTransactionStartRequest request, CancellationToken ct = default)
        {
            if (FailStart)
                return Task.FromResult(new TseTransactionResult(false, "Fake TSE Start failed"));
            Starts.Add(request);
            return Task.FromResult(new TseTransactionResult(true, "OK", 11, 1, DateTimeOffset.UtcNow, "FAKE-SERIAL", "c3RhcnQ="));
        }

        public Task<TseTransactionResult> UpdateTransactionAsync(TseTransactionUpdateRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TseTransactionResult> FinishTransactionAsync(TseTransactionFinishRequest request, CancellationToken ct = default)
        {
            Finishes.Add(request);
            return Task.FromResult(new TseTransactionResult(true, "OK", request.TransactionNumber, 2, DateTimeOffset.UtcNow, "FAKE-SERIAL", "ZmluaXNo"));
        }

        public Task<TseExportResult> ExportTarAsync(string targetPath, CancellationToken ct = default) =>
            Task.FromResult(new TseExportResult(false, "Fake export"));
    }
}
