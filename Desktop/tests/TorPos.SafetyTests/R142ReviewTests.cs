using System.Text;
using TorPos.Core;
using TorPos.Infrastructure;

// R142: every action in training mode is secured and marked AVTraining.
//
// DSFinV-K Anhang B, AVTraining: "Es können sämtliche Vorgänge im Trainingsmodus
// durchgeführt werden. … Alle Handlungen des Trainingsmodus müssen dokumentiert,
// gesondert gekennzeichnet und mittels der DSFinV-K abgebildet werden. Sie haben
// jedoch keine Auswirkungen auf den Kassenabschluss." 4.2.6: they are recorded and
// secured. Until R142 a training order (IMBISS order mode) was not secured at all,
// and an aborted training Vorgang was exported as AVBelegabbruch.
public static class R142ReviewTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        assert(SaleModePolicy.SecuresVorgaenge(isTraining: true, recordsTrainingFiscally: true, canCommitProductionSale: false) &&
               !SaleModePolicy.SecuresVorgaenge(isTraining: true, recordsTrainingFiscally: false, canCommitProductionSale: false) &&
               SaleModePolicy.SecuresVorgaenge(isTraining: false, recordsTrainingFiscally: false, canCommitProductionSale: true) &&
               !SaleModePolicy.SecuresVorgaenge(isTraining: false, recordsTrainingFiscally: false, canCommitProductionSale: false),
            "R142 a training user's Vorgänge are secured exactly where training is recorded; a regular user's where the till books for real; a test till secures nothing");

        var dir = Path.Combine(root, "r142-training-orders");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r142.db"));
        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["company.name"] = "R142 Imbiss",
            ["company.street"] = "Hauptstraße 1",
            ["company.zip"] = "10115",
            ["company.city"] = "Berlin",
            ["company.tax_no"] = "27/123/45678",
            ["tse.status"] = "AKTIV",
            ["tse.client_id"] = "KASSE-1",
        });
        var provider = new RecordingTseProvider();
        var tse = new TseFailSafeService(provider, new TseOutageRepository(db, audit), audit);
        var vorgaenge = new TseVorgangService(db, tse, settings);
        var parked = new ParkedReceiptRepository(db);
        var orders = new OrderFiscalSigningService(tse, settings, parked) { Vorgaenge = vorgaenge, Bestellungen = new OrderBestellungRepository(db) };

        // an aborted training Vorgang
        await vorgaenge.StartAsync("training-abort", training: true, DateTimeOffset.Now.AddMinutes(-1), "azubi");
        await vorgaenge.AbortAsync("training-abort", new[] { new CartLine { ProductId = 1, ProductName = "Döner", Quantity = 1, UnitPriceCents = 700, VatRate = 7m } }, 0, "azubi", "azubi");
        assert(FiscalProcessData.AbortText(true) == "AVTraining^0.00_0.00_0.00_0.00_0.00^" &&
               Encoding.UTF8.GetString(provider.Finishes[^1].ProcessData) == "AVTraining^0.00_0.00_0.00_0.00_0.00^",
            "R142 an aborted training Vorgang is finished in the TSE as AVTraining, not AVBelegabbruch");

        // a training order and a real order
        await vorgaenge.StartAsync("training-order", training: true, DateTimeOffset.Now.AddMinutes(-1), "azubi");
        var trainingOrder = await parked.ParkAsync(new[] { new CartLine { ProductId = 2, ProductName = "Ayran", Quantity = 2, UnitPriceCents = 250, VatRate = 7m } }, 0, "azubi", assignPickupNumber: true, training: true);
        await orders.SignInVorgangAsync(trainingOrder, "training-order", DateTimeOffset.Now.AddMinutes(-1), "azubi");
        var realOrder = await parked.ParkAsync(new[] { new CartLine { ProductId = 3, ProductName = "Cola", Quantity = 1, UnitPriceCents = 300, VatRate = 19m } }, 0, "kasse1", assignPickupNumber: true);
        await orders.SignInVorgangAsync(realOrder, "", null, "kasse1");
        assert(provider.Finishes.Count(f => f.ProcessType == "Bestellung-V1") == 2,
            "R142 a training order is secured as Bestellung-V1 like a real one");

        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "UPDATE parked_receipts SET status='SIMULATED' WHERE id=$t; UPDATE parked_receipts SET status='CANCELLED' WHERE id=$r;";
            q.Parameters.AddWithValue("$t", trainingOrder.Id);
            q.Parameters.AddWithValue("$r", realOrder.Id);
            await q.ExecuteNonQueryAsync();
        }

        await Task.Delay(15);
        await new BusinessManagementService(db, settings, audit).CreateZArchiveAsync("chef", "TEST");
        var folder = await new DsfinvkExportService(db, settings).ExportAsync(DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddMinutes(1), Path.Combine(dir, "out"));
        var heads = File.ReadAllLines(Path.Combine(folder, "transactions.csv")).Skip(1).Select(l => l.Split(';')).ToList();
        // Bonkopf: BON_ID field 3, BON_TYP field 5, BON_NAME field 6
        var trainingHead = heads.Single(h => h[3] == $"\"BE-{trainingOrder.ParkNumber}-1\"");
        var realHead = heads.Single(h => h[3] == $"\"BE-{realOrder.ParkNumber}-1\"");
        var abortHead = heads.Single(h => h[3] == "\"AB-1\"");
        var cases = File.ReadAllLines(Path.Combine(folder, "businesscases.csv")).Skip(1).ToArray();
        assert(trainingHead[5] == "\"AVTraining\"" && trainingHead[6] == "\"Bestellung (Training)\"" &&
               realHead[5] == "\"AVBestellung\"" &&
               abortHead[5] == "\"AVTraining\"" && abortHead[6] == "\"Abbruch (Training)\"" &&
               cases.Length == 0,
            "R142 the training order and the aborted training Vorgang are exported as AVTraining, the real order stays AVBestellung, and nothing reaches the closing totals");
    }

    private sealed class RecordingTseProvider : ITseProvider
    {
        public readonly List<TseTransactionFinishRequest> Finishes = new();
        private ulong _next = 420;

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

        public Task<TseTransactionResult> StartTransactionAsync(TseTransactionStartRequest request, CancellationToken ct = default) =>
            Task.FromResult(new TseTransactionResult(true, "OK", _next++, 1, DateTimeOffset.UtcNow.AddSeconds(-5), "FAKE-SERIAL", "c3RhcnQ="));

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
