using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

// R113: two critical fiscal-integrity bugs found by this session's own
// end-to-end audit of the whole project.
//
// F1 - SILENT UNSIGNED SALE. SaleFiscalSigningService/OrderFiscalSigningService
//      simply `return`ed when tse.status wasn't "AKTIV" or the client id was
//      blank. No outage row, no audit entry, and - because sale.TseOutage
//      stayed false - the printed receipt carried NO "TSE-AUSFALL" note. An
//      unsigned sale was indistinguishable from a signed one.
//
// F2 - OUTAGE DETECTION WASN'T WIRED UP. TseFailSafeService.ProbeAsync existed
//      but had no caller anywhere (every probe site used the raw ITseProvider),
//      and ITseOutageRepository.GetOpenAsync had no production caller at all,
//      so an open outage was never shown at the register.
//
// Both are fully testable end-to-end: signing runs strictly AFTER the durable
// commit and never calls FiscalRelease.RequireProduction(), so - unlike
// RecordStornoAsync/RecordReturnAsync - the success paths are reachable here.
public static class R113ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert,
        Func<Func<Task>, string, string, Task> rejectMessage)
    {
        _ = rejectMessage;

        var dir = Path.Combine(root, "r113-tse-outage");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r113.db"));

        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        var sales = new SaleRepository(db);
        var outages = new TseOutageRepository(db, audit);
        var provider = new FakeTseProvider();
        var failSafe = new TseFailSafeService(provider, outages, audit);
        var signing = new SaleFiscalSigningService(failSafe, settings, sales);

        var receiptCounter = 113000L;
        long InsertRawSale()
        {
            using var c = db.OpenConnection();
            using var q = c.CreateCommand();
            q.CommandText = """
                INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,cash_portion_cents,card_portion_cents)
                VALUES($r,$now,'CASH',1000,1000,'TEST_FIXTURE','SALE',1000,0);
                SELECT last_insert_rowid();
                """;
            q.Parameters.AddWithValue("$r", ++receiptCounter);
            q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
            return Convert.ToInt64(q.ExecuteScalar());
        }

        Sale NewSale(long id) => new()
        {
            Id = id,
            ReceiptNumber = receiptCounter,
            CreatedAt = DateTimeOffset.Now,
            PaymentMethod = PaymentMethod.Cash,
            TotalCents = 1000,
            CashPortionCents = 1000,
            Lines = new[] { new CartLine { ProductName = "R113 Artikel", Quantity = 1, UnitPriceCents = 1000, VatRate = 19m } }
        };

        async Task<bool> PersistedOutageAsync(long saleId)
        {
            await using var c = db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = "SELECT outage FROM sale_tse_signatures WHERE sale_id=$id;";
            q.Parameters.AddWithValue("$id", saleId);
            var value = await q.ExecuteScalarAsync();
            return value is not null && value != DBNull.Value && Convert.ToInt64(value) == 1;
        }

        // ---- F1: TSE status is not AKTIV -------------------------------
        // This is THE bug: previously this path returned silently.
        var inactiveSale = NewSale(InsertRawSale());
        await signing.SignAsync(inactiveSale, "tester");

        assert(
            inactiveSale.TseOutage && string.IsNullOrEmpty(inactiveSale.TseTransactionNumber),
            "R113 a sale signed while the TSE is not AKTIV is marked as a TSE outage and no signature is fabricated");
        assert(
            await PersistedOutageAsync(inactiveSale.Id),
            "R113 that outage is persisted to sale_tse_signatures (outage=1), not only set on the in-memory sale");
        assert(
            await outages.GetOpenAsync() is not null,
            "R113 an inactive TSE at signing time opens a durable outage record in tse_outage_log");

        // The receipt's legally required TSE-AUSFALL note is driven purely by
        // this flag (ReceiptPrintJob.TseOutage), so this is what makes the note
        // appear at all - the whole point of the fix.
        assert(
            inactiveSale.TseOutage,
            "R113 the sale carries the flag the printed receipt uses to emit its mandatory TSE-AUSFALL note");

        var inactiveReason = (await outages.GetOpenAsync())!.Reason;
        assert(
            inactiveReason.Contains("TSE ist nicht aktiv"),
            $"R113 the outage states WHY it happened instead of an empty/generic reason (actual: {inactiveReason})");

        await outages.CloseOpenAsync("tester");

        // ---- F1: client id missing while TSE reports AKTIV --------------
        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["tse.status"] = "AKTIV",
            ["tse.client_id"] = ""
        });

        var noClientSale = NewSale(InsertRawSale());
        await signing.SignAsync(noClientSale, "tester");
        assert(
            noClientSale.TseOutage && await PersistedOutageAsync(noClientSale.Id) && await outages.GetOpenAsync() is not null,
            "R113 a missing TSE client ID is documented as an outage too, instead of returning silently");
        await outages.CloseOpenAsync("tester");

        // ---- Negative control: a healthy TSE must NOT report an outage --
        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["tse.status"] = "AKTIV",
            ["tse.client_id"] = "KASSE-1"
        });

        var signedSale = NewSale(InsertRawSale());
        await signing.SignAsync(signedSale, "tester");
        assert(
            !signedSale.TseOutage && signedSale.TseTransactionNumber == "42" && signedSale.TseSerialNumber == "FAKE-SERIAL",
            "R113 a healthy TSE still signs normally - the fix does not turn good signatures into outages");
        assert(
            await outages.GetOpenAsync() is null,
            "R113 a successful signature leaves no open outage behind");

        // ---- F2: the probe now actually records outages ----------------
        provider.ProbeState = TseConnectionState.SdkMissing;
        await failSafe.ProbeAsync("tester");
        assert(
            await outages.GetOpenAsync() is not null,
            "R113 a failed TSE probe opens an outage - previously every probe site used the raw provider and recorded nothing");

        // ---- F2: an open outage is readable by the cashier-facing layer -
        var visible = await failSafe.GetOpenOutageAsync();
        assert(
            visible is not null && visible.State == "OPEN",
            "R113 TseFailSafeService.GetOpenOutageAsync surfaces the open outage the register badge shows");

        provider.ProbeState = TseConnectionState.Ready;
        await failSafe.ProbeAsync("tester");
        assert(
            await failSafe.GetOpenOutageAsync() is null,
            "R113 a later successful probe closes the outage again, so the badge clears by itself");
    }

    private sealed class FakeTseProvider : ITseProvider
    {
        public TseConnectionState ProbeState = TseConnectionState.Ready;

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
            Task.FromResult(new TseProbeResult(ProbeState, ProbeState == TseConnectionState.Ready ? "OK" : "Fake TSE nicht erreichbar"));

        public Task<TseActivationResult> ActivateAsync(TseActivationRequest request, CancellationToken ct = default) =>
            Task.FromResult(new TseActivationResult(true, "OK"));

        public Task<TseTransactionResult> StartTransactionAsync(TseTransactionStartRequest request, CancellationToken ct = default) =>
            Task.FromResult(new TseTransactionResult(true, "OK", 42, 1, DateTimeOffset.Now, "FAKE-SERIAL", "SIG-START"));

        public Task<TseTransactionResult> UpdateTransactionAsync(TseTransactionUpdateRequest request, CancellationToken ct = default) =>
            Task.FromResult(new TseTransactionResult(true, "OK", 42, 1, DateTimeOffset.Now, "FAKE-SERIAL", "SIG-UPDATE"));

        public Task<TseTransactionResult> FinishTransactionAsync(TseTransactionFinishRequest request, CancellationToken ct = default) =>
            Task.FromResult(new TseTransactionResult(true, "OK", 42, 2, DateTimeOffset.Now, "FAKE-SERIAL", "SIG-FINISH"));

        public Task<TseExportResult> ExportTarAsync(string targetPath, CancellationToken ct = default) =>
            Task.FromResult(new TseExportResult(false, "Fake export"));
    }
}
