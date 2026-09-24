using System.Text.Json;
using TorPos.Core;
using TorPos.Infrastructure;

// F-6: the program can stop after the TSE finished a transaction but before
// the sale record got the signature. On restart the till used to ask the TSE
// to finish it again; the TSE refuses, and a correctly signed sale was
// recorded as an outage. The TSE answer is now journaled the moment it
// arrives and used on restart; if even the journal was not written, the
// outage names the transaction so the signature can be found in the TAR.
public static class F6TseFinishJournalTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "f6-tse-finish-journal");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "f6.db"));
        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["company.name"] = "F6 Kiosk",
            ["tse.status"] = "AKTIV",
            ["tse.client_id"] = "KASSE-1",
        });
        var provider = new OneShotTseProvider();
        var tse = new TseFailSafeService(provider, new TseOutageRepository(db, audit), audit);
        var vorgaenge = new TseVorgangService(db, tse, settings);
        var sales = new SaleRepository(db);
        var signing = new SaleFiscalSigningService(tse, settings, sales) { Vorgaenge = vorgaenge };

        // Crash after the TSE finished: FinishAsync ran (TSE signed, answer
        // journaled), the program stopped before the sale got the signature.
        await vorgaenge.StartAsync("f6-crash", false, DateTimeOffset.Now.AddMinutes(-1), "kasse1");
        var saleId = await InsertSaleAsync(db, 606001, 450);
        await InsertCommittedCheckoutAsync(db, "op-f6", saleId, "f6-crash");
        var tseAnswer = await vorgaenge.FinishAsync("f6-crash", FiscalProcessData.KassenbelegProcessType, "Beleg^0.00_4.50_0.00_0.00_0.00^4.50:Bar", "kasse1", $"SALE:{saleId}");
        var finishesAfterCrash = provider.Finishes;

        var recovered = await signing.FinishCommittedVorgaengeAsync("SYSTEM");
        var sale = (await sales.GetByIdAsync(saleId))!;
        var again = await signing.FinishCommittedVorgaengeAsync("SYSTEM");

        assert(
            tseAnswer.Signed && recovered == 1 && again == 0 &&
            provider.Finishes == finishesAfterCrash &&
            !sale.TseOutage &&
            sale.TseTransactionNumber == tseAnswer.TransactionNumber &&
            sale.TseSignature == tseAnswer.Signature &&
            await vorgaenge.GetJournaledFinishAsync("f6-crash") is null,
            "F-6 after a crash the sale gets the signature the TSE already returned, without a second finish call - it is not turned into an outage, and it is recovered exactly once");

        // Crash before even the journal was written: the attempt is known, the
        // TSE refuses a second finish, and the outage names the transaction.
        await vorgaenge.StartAsync("f6-early", false, DateTimeOffset.Now.AddMinutes(-1), "kasse1");
        var earlySale = await InsertSaleAsync(db, 606002, 300);
        await InsertCommittedCheckoutAsync(db, "op-f6-early", earlySale, "f6-early");
        var early = await vorgaenge.GetAsync("f6-early");
        await using (var c = db.OpenConnection())
        await using (var q = c.CreateCommand())
        {
            q.CommandText = "UPDATE tse_vorgaenge SET finish_attempted_at=$at WHERE id='f6-early';";
            q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
            await q.ExecuteNonQueryAsync();
        }
        provider.RefuseFinish = true;
        await signing.FinishCommittedVorgaengeAsync("SYSTEM");
        var earlyRecorded = (await sales.GetByIdAsync(earlySale))!;
        string auditText;
        await using (var c = db.OpenConnection())
        await using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT group_concat(details,' | ') FROM audit_log WHERE event_type='TSE_UNAVAILABLE';";
            auditText = Convert.ToString(await q.ExecuteScalarAsync()) ?? "";
        }

        assert(
            earlyRecorded.TseOutage &&
            auditText.Contains($"TSE-Transaktion {early!.TransactionNumber} wurde vor einem Programmabbruch möglicherweise bereits abgeschlossen", StringComparison.Ordinal),
            "F-6 if the program stopped before the TSE answer was journaled, the outage record names the transaction that may already be signed, so it can be found in the TSE export");
    }

    private static async Task<long> InsertSaleAsync(SqliteDatabase db, long receipt, long cents)
    {
        await Task.Delay(15);
        await using var c = db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = "INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,cash_portion_cents,im_haus,started_at) VALUES($r,$at,'CASH',$t,$t,'TEST_FIXTURE','SALE',$t,0,$at); SELECT last_insert_rowid();";
        q.Parameters.AddWithValue("$r", receipt);
        q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        q.Parameters.AddWithValue("$t", cents);
        var id = Convert.ToInt64(await q.ExecuteScalarAsync());
        await using var item = c.CreateCommand();
        item.CommandText = "INSERT INTO sale_items(sale_id,product_id,product_name,quantity,unit_price_cents,vat_rate,line_total_cents) VALUES($s,1,'Artikel',1,$t,19,$t);";
        item.Parameters.AddWithValue("$s", id);
        item.Parameters.AddWithValue("$t", cents);
        await item.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task InsertCommittedCheckoutAsync(SqliteDatabase db, string operationId, long saleId, string vorgangId)
    {
        var snapshot = new CheckoutSnapshot(operationId, Array.Empty<CartLine>(), 0, PaymentMethod.Cash, "kasse1", null,
            TseVorgangId: vorgangId, StartedAt: DateTimeOffset.Now.AddMinutes(-1));
        await using var c = db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = "INSERT INTO checkout_operations(id,state,snapshot,updated_at,sale_id) VALUES($id,'COMMITTED',$s,$at,$sale);";
        q.Parameters.AddWithValue("$id", operationId);
        q.Parameters.AddWithValue("$s", JsonSerializer.Serialize(snapshot));
        q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        q.Parameters.AddWithValue("$sale", saleId);
        await q.ExecuteNonQueryAsync();
    }

    private sealed class OneShotTseProvider : ITseProvider
    {
        private ulong _next = 900;
        private ulong _counter = 1;
        public int Finishes { get; private set; }
        public bool RefuseFinish { get; set; }

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
            var log = DateTimeOffset.UtcNow.AddSeconds(-20);
            return Task.FromResult(new TseTransactionResult(true, "OK", _next++, _counter++, new DateTimeOffset(log.Ticks - log.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero), "FAKE-SERIAL", "c3RhcnQ="));
        }

        public Task<TseTransactionResult> UpdateTransactionAsync(TseTransactionUpdateRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TseTransactionResult> FinishTransactionAsync(TseTransactionFinishRequest request, CancellationToken ct = default)
        {
            Finishes++;
            return Task.FromResult(RefuseFinish
                ? new TseTransactionResult(false, "Transaktion ist nicht offen")
                : new TseTransactionResult(true, "OK", request.TransactionNumber, _counter++, DateTimeOffset.UtcNow, "FAKE-SERIAL", "ZmluaXNo"));
        }

        public Task<TseExportResult> ExportTarAsync(string targetPath, CancellationToken ct = default) =>
            Task.FromResult(new TseExportResult(false, "Fake export"));
    }
}
