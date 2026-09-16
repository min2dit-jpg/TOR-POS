using System.Text;
using TorPos.Core;
using TorPos.Infrastructure;

// R135: training sales are recorded and secured as AVTraining.
//
// AEAO zu § 146a Nr. 1.11.1 lists Trainingsbuchungen among the Vorgänge that
// have to be secured, and DSFinV-K 4.2.6 explains why: training operators were
// used to keep cash takings out of the records. Anhang B: AVTraining has no
// effect on the closing; payment types may be recorded for training purposes.
// Before R135 a training sale left nothing but an audit line.
public static class R135ReviewTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "r135-training");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r135.db"));

        using (var c = db.OpenConnection())
        {
            using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('training_receipts','training_receipt_items','training_tse_signatures');";
            assert(Convert.ToInt64(q.ExecuteScalar()) == 3 && SchemaMigrationService.TargetSchemaVersion >= 15,
                "R135 schema migration V15 adds the training tables");
        }

        assert(SaleModePolicy.RecordsTrainingFiscally(true, true, true, true) &&
               !SaleModePolicy.RecordsTrainingFiscally(false, true, true, true) &&
               !SaleModePolicy.RecordsTrainingFiscally(true, true, false, true) &&
               !SaleModePolicy.RecordsTrainingFiscally(true, false, true, true),
            "R135 training is recorded fiscally exactly on a till that books for real; a test till records nothing fiscal");

        var line = new CartLine { ProductName = "Döner", Quantity = 1, UnitPriceCents = 1000, VatRate = 19m };
        var trainingSale = new Sale
        {
            TransactionType = FiscalProcessData.TrainingTransactionType,
            PaymentMethod = PaymentMethod.Mixed, TotalCents = 1000, CashPortionCents = 400, CardPortionCents = 600,
            Lines = new[] { line }
        };
        assert(FiscalProcessData.KassenbelegText(trainingSale) == "AVTraining^10.00_0.00_0.00_0.00_0.00^4.00:Bar_6.00:Unbar",
            "R135 processData of a training sale is Kassenbeleg-V1 with Vorgangstyp AVTraining and the training payments (Anhang I, B)");

        var trainings = new TrainingReceiptRepository(db);
        var refused = false;
        try { await trainings.RecordAsync(new CheckoutSnapshot("r135", new[] { line }, 0, PaymentMethod.Cash, "azubi", null)); }
        catch (InvalidOperationException) { refused = true; }
        long stored;
        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM training_receipts;";
            stored = Convert.ToInt64(await q.ExecuteScalarAsync());
        }
        assert(refused && stored == 0,
            "R135 while the fiscal circuit breaker is off nothing is recorded as AVTraining");

        // ---------- a till that books for real (written directly, breaker stays off) ----------
        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["company.name"] = "R135 Imbiss",
            ["company.street"] = "Hauptstraße 1",
            ["company.zip"] = "10115",
            ["company.city"] = "Berlin",
            ["company.tax_no"] = "27/123/45678",
            ["tse.status"] = "AKTIV",
            ["tse.client_id"] = "KASSE-1",
        });

        var number = 0L;
        async Task<long> TrainingAsync(string method, long cash, long card, params CartLine[] lines)
        {
            await Task.Delay(15);
            await using var c = db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = "INSERT INTO training_receipts(training_number,created_at,operator_name,payment_method,discount_cents,total_cents,cash_portion_cents,card_portion_cents,im_haus) VALUES($n,$at,'azubi',$m,0,$t,$cash,$card,1); SELECT last_insert_rowid();";
            q.Parameters.AddWithValue("$n", ++number);
            q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
            q.Parameters.AddWithValue("$m", method);
            q.Parameters.AddWithValue("$t", cash + card);
            q.Parameters.AddWithValue("$cash", cash);
            q.Parameters.AddWithValue("$card", card);
            var id = Convert.ToInt64(await q.ExecuteScalarAsync());
            foreach (var l in lines)
            {
                await using var item = c.CreateCommand();
                item.CommandText = "INSERT INTO training_receipt_items(training_id,product_id,product_name,variant_name,barcode,quantity,unit_price_cents,vat_rate,pfand_cents,line_total_cents,list_unit_price_cents,promotion_id,promotion_name,promotion_percent,promotion_discount_unit_cents) VALUES($t,1,$n,'','',$q,$u,$v,0,$lt,$u,0,'',0,0);";
                item.Parameters.AddWithValue("$t", id);
                item.Parameters.AddWithValue("$n", l.ProductName);
                item.Parameters.AddWithValue("$q", (double)l.Quantity);
                item.Parameters.AddWithValue("$u", l.UnitPriceCents);
                item.Parameters.AddWithValue("$v", (double)l.VatRate);
                item.Parameters.AddWithValue("$lt", l.LineTotalCents);
                await item.ExecuteNonQueryAsync();
            }
            return id;
        }

        // a real sale in the same period, to see that training changes nothing about it
        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,cash_portion_cents,im_haus) VALUES(135001,$at,'CASH',700,700,'TEST_FIXTURE','SALE',700,0); SELECT last_insert_rowid();";
            q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
            var id = Convert.ToInt64(await q.ExecuteScalarAsync());
            await using var item = c.CreateCommand();
            item.CommandText = "INSERT INTO sale_items(sale_id,product_id,product_name,quantity,unit_price_cents,vat_rate,line_total_cents) VALUES($s,1,'Tee',1,700,7,700);";
            item.Parameters.AddWithValue("$s", id);
            await item.ExecuteNonQueryAsync();
        }

        var trainingId = await TrainingAsync("MIXED", 400, 600, line);
        var outages = new TseOutageRepository(db, audit);
        var provider = new RecordingTseProvider();
        var signing = new TrainingFiscalSigningService(new TseFailSafeService(provider, outages, audit), settings, trainings);

        var training = await LoadTrainingAsync(db, trainingId);
        var signed = await signing.SignAsync(training, "azubi");
        assert(signed.Signed && provider.Starts.Count == 1 && provider.Starts[0].ProcessData.Length == 0 &&
               provider.Finishes.Count == 1 && provider.Finishes[0].ProcessType == "Kassenbeleg-V1" &&
               Encoding.UTF8.GetString(provider.Finishes[0].ProcessData) == "AVTraining^10.00_0.00_0.00_0.00_0.00^4.00:Bar_6.00:Unbar",
            "R135 a training sale is signed like a receipt - start empty, finish Kassenbeleg-V1 - with Vorgangstyp AVTraining");

        var second = false;
        try { await trainings.RecordTseResultAsync(trainingId, SaleTseResult.Outage("x")); }
        catch (InvalidOperationException) { second = true; }
        var updateRefused = false;
        try
        {
            await using var c = db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = "UPDATE training_receipts SET total_cents=0;";
            await q.ExecuteNonQueryAsync();
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { updateRefused = true; }
        assert(second && updateRefused,
            "R135 a training record and its TSE record are final, like every other Vorgang");

        await settings.SaveManyAsync(new Dictionary<string, string> { ["tse.status"] = "" });
        var offline = await LoadTrainingAsync(db, await TrainingAsync("CASH", 500, 0, new CartLine { ProductName = "Cola", Quantity = 1, UnitPriceCents = 500, VatRate = 19m }));
        var outage = await signing.SignAsync(offline, "azubi");
        assert(!outage.Signed && provider.Starts.Count == 1 && outage.OutageMessage.Contains("Trainingsvorgang"),
            "R135 with the TSE not active the training is kept as a documented outage");

        var guardBlocks = false;
        try { await settings.SaveManyAsync(new Dictionary<string, string> { ["company.name"] = "Anders" }); }
        catch (MasterDataChangeRequiresClosingException) { guardBlocks = true; }
        assert(guardBlocks, "R135 a training Vorgang also waits for the closing (R132 master data guard)");

        await Task.Delay(15);
        var z = await new BusinessManagementService(db, settings, audit).CreateZArchiveAsync("chef", "TEST");
        assert(z.GrossCents == 700 && z.ReceiptCount == 1 && z.CashCents == 700,
            "R135 training has no effect on the Z-Bericht: only the real 7,00 sale is counted");

        var exporter = new DsfinvkExportService(db, settings);
        var from = DateTimeOffset.Now.AddHours(-1);
        var to = DateTimeOffset.Now.AddMinutes(1);
        var report = await exporter.ValidateAsync(from, to);
        var folder = await exporter.ExportAsync(from, to, Path.Combine(dir, "out"));
        var heads = File.ReadAllLines(Path.Combine(folder, "transactions.csv")).Skip(1).ToArray();
        var payments = File.ReadAllLines(Path.Combine(folder, "datapayment.csv")).Skip(1).ToArray();
        var cases = File.ReadAllLines(Path.Combine(folder, "businesscases.csv")).Skip(1).ToArray();
        var closingPayments = File.ReadAllLines(Path.Combine(folder, "payment.csv")).Skip(1).ToArray();
        var tse = File.ReadAllLines(Path.Combine(folder, "transactions_tse.csv")).Skip(1).ToArray();

        assert(heads.Count(h => h.Contains(";\"AVTraining\";\"Training\";")) == 2 &&
               payments.Any(p => p.Contains(";\"TR-1\";\"Bar\";\"Bar\";;;4,00")) && payments.Any(p => p.Contains(";\"TR-1\";\"Unbar\";\"Karte\";;;6,00")),
            "R135 the export lists both training Vorgänge as AVTraining with their training payments");
        assert(cases.Length == 1 && cases[0].Contains(";7,00;") && closingPayments.Length == 1 && closingPayments[0].EndsWith(";7,00"),
            "R135 the closing totals contain only the real sale (Anhang B: no effect on the Kassenabschluss)");
        assert(tse.Single(t => t.Contains(";\"TR-1\";")).Contains("\"AVTraining^10.00_0.00_0.00_0.00_0.00^4.00:Bar_6.00:Unbar\"") &&
               tse.Single(t => t.Contains(";\"TR-2\";")).Contains("TSE-Ausfall: TSE ist nicht aktiv") &&
               report.Issues.All(x => x.Code != "TRAINING"),
            "R135 each training carries its signed processData or its outage reason; the hint that training is missing is gone");
    }

    private static async Task<Sale> LoadTrainingAsync(SqliteDatabase db, long id)
    {
        await using var c = db.OpenConnection();
        var all = await TrainingReceiptRepository.LoadInPeriodAsync(c, "", "9999", CancellationToken.None);
        return all.Single(t => t.Receipt.Id == id).Receipt;
    }

    private sealed class RecordingTseProvider : ITseProvider
    {
        public readonly List<TseTransactionStartRequest> Starts = new();
        public readonly List<TseTransactionFinishRequest> Finishes = new();

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
            Starts.Add(request);
            return Task.FromResult(new TseTransactionResult(true, "OK", 21, 1, DateTimeOffset.UtcNow, "FAKE-SERIAL", "c3RhcnQ="));
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
