using System.Globalization;
using System.Text;
using System.Text.Json;
using TorPos.Core;
using TorPos.Infrastructure;

// R136: the TSE transaction starts when the Vorgang begins.
//
// AEAO zu § 146a Nr. 2.2.2: the recording system has to start logging the
// Vorgang in the TSE "unmittelbar mit Beginn" of the Vorgang; Nr. 2.2.3.3: it is
// ended before the receipt is issued, and no Vorgang stays open at a closing.
// Nr. 1.11.1 lists Belegabbrüche among the Vorgänge to secure. Until R136 TOR
// started and finished the transaction together after payment, so BON_START and
// TSE_TA_START stayed empty and an emptied cart left no trace.
public static class R136ReviewTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "r136-vorgangsbeginn");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r136.db"));

        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = """
                SELECT (SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('tse_vorgaenge','aborted_vorgaenge','aborted_vorgang_items'))
                     + (SELECT COUNT(*) FROM pragma_table_info('sales') WHERE name='started_at')
                     + (SELECT COUNT(*) FROM pragma_table_info('sale_tse_signatures') WHERE name='start_log_time')
                     + (SELECT COUNT(*) FROM pragma_table_info('cash_movement_tse_signatures') WHERE name='start_log_time')
                     + (SELECT COUNT(*) FROM pragma_table_info('training_receipts') WHERE name='started_at')
                     + (SELECT COUNT(*) FROM pragma_table_info('training_tse_signatures') WHERE name='start_log_time')
                     + (SELECT COUNT(*) FROM pragma_table_info('parked_receipts') WHERE name IN ('vorgang_started_at','tse_start_log_time'));
                """;
            assert(Convert.ToInt64(await q.ExecuteScalarAsync()) == 10 && SchemaMigrationService.TargetSchemaVersion >= 16,
                "R136 schema migration V16 adds the Vorgang tables, Vorgangsbeginn and the TSE start log time");
        }

        // ---------- the cart decides when a Vorgang begins and ends ----------
        var tracker = new TseVorgangCartTracker();
        var t0 = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.FromHours(2));
        var doner = new CartLine { ProductId = 1, ProductName = "Döner", Quantity = 1, UnitPriceCents = 700, VatRate = 7m };
        var cart = new List<CartLine>();

        var none = tracker.OnCartChanged(cart, 0, false, true, t0);
        cart.Add(doner);
        var testTill = new TseVorgangCartTracker().OnCartChanged(cart, 0, false, false, t0);
        var start = tracker.OnCartChanged(cart, 0, true, true, t0);
        doner.Quantity = 2;
        var again = tracker.OnCartChanged(cart, 0, true, true, t0.AddSeconds(5));
        doner.Quantity = 3; // changed after the last cart update: not what was in the cart
        cart.Clear();
        var abort = tracker.OnCartChanged(cart, 0, true, true, t0.AddSeconds(9));
        assert(none.Kind == TseVorgangActionKind.None && testTill.Kind == TseVorgangActionKind.None &&
               start.Kind == TseVorgangActionKind.Start && start.StartedAt == t0 && start.VorgangId.Length > 0 &&
               again.Kind == TseVorgangActionKind.None &&
               abort.Kind == TseVorgangActionKind.Abort && abort.VorgangId == start.VorgangId && abort.StartedAt == t0 &&
               abort.Lines.Single().Quantity == 2 && abort.Lines.Single().VatRate == 7m && tracker.VorgangId is null,
            "R136/R2026 the Vorgang starts with the first position and aborts with the snapshotted current Im-Haus VAT; food remains 7% from 01.01.2026");

        cart.Add(new CartLine { ProductId = 2, ProductName = "Cola", Quantity = 1, UnitPriceCents = 300, VatRate = 19m });
        var paid = tracker.OnCartChanged(cart, 0, false, true, t0.AddMinutes(1));
        var released = tracker.Release();
        cart.Clear();
        var afterPayment = tracker.OnCartChanged(cart, 0, false, true, t0.AddMinutes(2));
        tracker.Adopt("parked-vorgang", t0, new[] { doner }, 0, false);
        var adopted = tracker.OnCartChanged(new[] { doner }, 0, false, true, t0.AddMinutes(3));
        assert(paid.Kind == TseVorgangActionKind.Start && released == paid.VorgangId &&
               afterPayment.Kind == TseVorgangActionKind.None &&
               adopted.Kind == TseVorgangActionKind.None && tracker.VorgangId == "parked-vorgang",
            "R136 payment, order acceptance or parking end the Vorgang explicitly; a recalled Vorgang is continued, not started again");

        // ---------- a till that books for real (TSE calls against a recording fake) ----------
        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["company.name"] = "R136 Imbiss",
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
        var sales = new SaleRepository(db);
        var saleSigning = new SaleFiscalSigningService(tse, settings, sales) { Vorgaenge = vorgaenge };

        var begin = DateTimeOffset.Now.AddMinutes(-2);
        await vorgaenge.StartAsync("sale-1", false, begin, "kasse1");
        await vorgaenge.StartAsync("sale-1", false, begin, "kasse1");
        var opened = await vorgaenge.GetAsync("sale-1");
        assert(provider.Starts.Count == 1 && provider.Starts[0].ProcessData.Length == 0 && provider.Starts[0].ProcessType == "" &&
               opened is { State: TseVorgangService.Open, TransactionNumber: "100", StartLogTime: not null } &&
               opened.StartedAt == begin,
            "R136 the TSE transaction is started at Vorgangsbeginn, without data (Anhang I), once per Vorgang");

        var saleId = await InsertSaleAsync(db, 136001, begin, 700, 7m);
        var sale = await sales.GetByIdAsync(saleId) ?? throw new InvalidOperationException("sale");
        await saleSigning.SignInVorgangAsync(sale, "sale-1", "kasse1");
        var reloaded = await sales.GetByIdAsync(saleId) ?? throw new InvalidOperationException("sale");
        assert(provider.Starts.Count == 1 && provider.Finishes.Count == 1 &&
               provider.Finishes[0].TransactionNumber == 100 && provider.Finishes[0].ProcessType == "Kassenbeleg-V1" &&
               Encoding.UTF8.GetString(provider.Finishes[0].ProcessData) == "Beleg^0.00_7.00_0.00_0.00_0.00^7.00:Bar" &&
               reloaded.TseStartLogTime == opened?.StartLogTime && reloaded.StartedAt == begin &&
               reloaded.TseLogTime is not null && reloaded.TseStartLogTime < reloaded.TseLogTime &&
               (await vorgaenge.GetAsync("sale-1"))!.State == TseVorgangService.Finished,
            "R136 payment finishes the SAME transaction with the receipt data; Vorgangsbeginn and TSE start time are stored with the sale");

        // KassenSichV §2: if immediate TSE start at the first position
        // failed, recovery before payment must NOT create a later replacement
        // transaction with a false start time.
        await settings.SaveManyAsync(new Dictionary<string, string> { ["tse.status"] = "" });
        await vorgaenge.StartAsync("sale-2", false, DateTimeOffset.Now, "kasse1");
        var failedStart = await vorgaenge.GetAsync("sale-2");
        await settings.SaveManyAsync(new Dictionary<string, string> { ["tse.status"] = "AKTIV" });
        var sale2 = await sales.GetByIdAsync(await InsertSaleAsync(db, 136002, failedStart!.StartedAt, 300, 19m)) ?? throw new InvalidOperationException("sale");
        await saleSigning.SignInVorgangAsync(sale2, "sale-2", "kasse1");
        sale2 = await sales.GetByIdAsync(sale2.Id) ?? throw new InvalidOperationException("sale");
        assert(failedStart.TransactionNumber == "" && failedStart.StartError.Contains("nicht aktiv") &&
               provider.Starts.Count == 1 && provider.Finishes.Count == 1 &&
               sale2.TseOutage && sale2.TseTransactionNumber == "" && sale2.TseStartLogTime is null,
            "R136 a missed immediate TSE start stays a documented outage and is never replaced by a late transaction at payment");

        // ---------- aborted Vorgang ----------
        await vorgaenge.StartAsync("abort-1", false, DateTimeOffset.Now.AddSeconds(-20), "kasse1");
        await vorgaenge.AbortAsync("abort-1", new[] { new CartLine { ProductId = 3, ProductName = "Ayran", Quantity = 2, UnitPriceCents = 250, VatRate = 7m } }, 0, "kasse1", "kasse1");
        await vorgaenge.AbortAsync("abort-1", Array.Empty<CartLine>(), 0, "kasse1", "kasse1");
        long abortedRows, abortedItems;
        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "SELECT (SELECT COUNT(*) FROM aborted_vorgaenge), (SELECT COUNT(*) FROM aborted_vorgang_items);";
            await using var r = await q.ExecuteReaderAsync();
            await r.ReadAsync();
            abortedRows = r.GetInt64(0);
            abortedItems = r.GetInt64(1);
        }
        var updateRefused = false;
        try
        {
            await using var c = db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = "UPDATE aborted_vorgaenge SET total_cents=0;";
            await q.ExecuteNonQueryAsync();
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { updateRefused = true; }
        assert(provider.Finishes.Count == 2 && provider.Finishes[1].ProcessType == "Kassenbeleg-V1" &&
               Encoding.UTF8.GetString(provider.Finishes[1].ProcessData) == "AVBelegabbruch^0.00_0.00_0.00_0.00_0.00^" &&
               abortedRows == 1 && abortedItems == 1 && updateRefused &&
               (await vorgaenge.GetAsync("abort-1"))!.State == TseVorgangService.Aborted,
            "R136 an emptied cart finishes its transaction as AVBelegabbruch (Anhang I) and is kept once, immutable, with its positions");

        // ---------- parked receipt keeps its Vorgang ----------
        var parked = new ParkedReceiptRepository(db);
        var parkLine = new CartLine { ProductId = 4, ProductName = "Lahmacun", Quantity = 1, UnitPriceCents = 500, VatRate = 7m };
        var parkedReceipt = await parked.ParkAsync(new[] { parkLine }, 0, "kasse1");
        await vorgaenge.StartAsync("park-1", false, DateTimeOffset.Now.AddMinutes(-1), "kasse1");
        await vorgaenge.ParkAsync("park-1", parkedReceipt.Id);
        await vorgaenge.StartAsync("orphan-1", false, DateTimeOffset.Now.AddMinutes(-1), "kasse1");
        await vorgaenge.StartAsync("recovered-1", false, DateTimeOffset.Now.AddMinutes(-1), "kasse1");
        var orphans = await vorgaenge.AbortOrphansAsync("recovered-1", "SYSTEM");
        var resumed = await vorgaenge.ResumeParkedAsync(parkedReceipt.Id);
        assert(orphans == 1 && (await vorgaenge.GetAsync("orphan-1"))!.State == TseVorgangService.Aborted &&
               (await vorgaenge.GetAsync("recovered-1"))!.State == TseVorgangService.Open &&
               resumed is { Id: "park-1", State: TseVorgangService.Open },
            "R136 a parked receipt keeps its Vorgang open and continues it when recalled; after a crash only Vorgänge without a cart are aborted");

        var guard = new DailyClosingGuard(parked, db);
        await vorgaenge.ParkAsync("park-1", parkedReceipt.Id);
        await parked.CancelAsync(parkedReceipt.Id);
        await vorgaenge.AbortParkedAsync(parkedReceipt.Id, new[] { parkLine }, 0, "kasse1", "kasse1");
        var blocked = await guard.CheckAsync();
        await vorgaenge.AbortAsync("recovered-1", Array.Empty<CartLine>(), 0, "SYSTEM", "SYSTEM");
        var allowed = await guard.CheckAsync();
        assert((await vorgaenge.GetAsync("park-1"))!.State == TseVorgangService.Aborted &&
               !blocked.Allowed && blocked.Message.Contains("Vorgang offen") && allowed.Allowed,
            "R136 a deleted parked receipt ends its Vorgang as aborted; a closing is refused while a Vorgang is open in the TSE (AEAO Nr. 2.2.3.3)");

        // ---------- order: Bestellung-V1 finishes the Vorgang begun with its first position ----------
        var orderSigning = new OrderFiscalSigningService(tse, settings, parked) { Vorgaenge = vorgaenge };
        var orderBegin = DateTimeOffset.Now.AddMinutes(-3);
        await vorgaenge.StartAsync("order-1", false, orderBegin, "kasse1");
        var order = await parked.ParkAsync(new[] { parkLine }, 0, "kasse1", assignPickupNumber: true, orderPrint: false);
        await orderSigning.SignInVorgangAsync(order, "order-1", orderBegin, "kasse1");
        var storedOrder = await parked.GetOpenByIdAsync(order.Id);
        assert(provider.Finishes[^1].ProcessType == "Bestellung-V1" && provider.Finishes[^1].TransactionNumber == provider.StartNumbers[^1] &&
               storedOrder is { VorgangStartedAt: not null, TseStartLogTime: not null } && storedOrder.VorgangStartedAt == orderBegin,
            "R136 order acceptance finishes the order's own transaction as Bestellung-V1 and stores its Vorgangsbeginn");
        await parked.CancelAsync(order.Id);

        // ---------- cash movement: TSE start time ----------
        var movementAt = DateTimeOffset.Now;
        long movementId;
        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "INSERT INTO cash_movements(created_at,movement_type,amount_cents,reason,actor,fiscal_mode,business_case) VALUES($at,'EINLAGE',5000,'Wechselgeld','kasse1','PRODUCTION','Einzahlung'); SELECT last_insert_rowid();";
            q.Parameters.AddWithValue("$at", movementAt.ToString("O"));
            movementId = Convert.ToInt64(await q.ExecuteScalarAsync());
        }
        var movementResult = await new CashMovementFiscalSigningService(tse, settings, new CashMovementRepository(db, audit))
            .SignAsync(new CashMovement(movementId, movementAt, CashMovementKind.Einlage, 5000, "Wechselgeld", "kasse1", CashMovement.ProductionMode, CashBusinessCase.Einzahlung), "kasse1");
        assert(movementResult.Signed && movementResult.StartLogTime is not null,
            "R136 a cash movement keeps the TSE start time of its transaction");

        // ---------- export ----------
        await Task.Delay(15);
        await new BusinessManagementService(db, settings, audit).CreateZArchiveAsync("chef", "TEST");
        var exporter = new DsfinvkExportService(db, settings);
        var from = DateTimeOffset.Now.AddHours(-1);
        var to = DateTimeOffset.Now.AddMinutes(1);
        var report = await exporter.ValidateAsync(from, to);
        var folder = await exporter.ExportAsync(from, to, Path.Combine(dir, "out"));
        var heads = Csv(Path.Combine(folder, "transactions.csv"));
        var tseRows = Csv(Path.Combine(folder, "transactions_tse.csv"));
        var cases = File.ReadAllLines(Path.Combine(folder, "businesscases.csv")).Skip(1).ToArray();

        var saleHead = heads.Single(h => h["BON_ID"] == "\"136001\"");
        var saleTse = tseRows.Single(h => h["BON_ID"] == "\"136001\"");
        var abortHeads = heads.Where(h => h["BON_TYP"] == "\"AVBelegabbruch\"").ToList();
        var abortTse = tseRows.Single(h => h["BON_ID"] == "\"AB-1\"");
        assert(saleHead["BON_START"] == $"\"{Timestamp(begin)}\"" && saleTse["TSE_TA_START"] == $"\"{TseTime(reloaded.TseStartLogTime!.Value)}\"" &&
               saleTse["TSE_TA_START"].Length > 0,
            "R136 BON_START is the Vorgangsbeginn at the till and TSE_TA_START the TSE start log time");
        assert(abortHeads.Count == 4 && abortHeads.Any(h => h["BON_ID"] == "\"AB-1\"" && h["BON_START"].Length > 0) &&
               abortTse["TSE_VORGANGSDATEN"] == "\"AVBelegabbruch^0.00_0.00_0.00_0.00_0.00^\"" &&
               cases.All(line => !line.Contains("5,00")),
            "R136 aborted Vorgänge are exported as AVBelegabbruch with their TSE data and without effect on the closing");
        assert(report.Ready && report.Issues.All(x => x.Code != "BON_START"),
            "R136 Vorgänge recorded under R136 raise no hint about a missing start time");

        // ---------- a closing waits for aborted Vorgänge too (R132 guard) ----------
        var guardDb = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r136-guard.db"));
        var guardSettings = new SettingsRepository(guardDb);
        await guardSettings.SaveManyAsync(new Dictionary<string, string> { ["tse.status"] = "AKTIV", ["tse.client_id"] = "KASSE-1" });
        var guardAudit = new AuditLogRepository(guardDb);
        var guardVorgaenge = new TseVorgangService(guardDb, new TseFailSafeService(new RecordingTseProvider(), new TseOutageRepository(guardDb, guardAudit), guardAudit), guardSettings);
        await using (var c = guardDb.OpenConnection())
            assert(!await DsfinvkMasterDataStore.HasOpenVorgaengeAsync(c, CancellationToken.None), "R136 guard fixture starts without Vorgänge");
        await guardVorgaenge.StartAsync("g-1", false, DateTimeOffset.Now, "kasse1");
        await guardVorgaenge.AbortAsync("g-1", Array.Empty<CartLine>(), 0, "kasse1", "kasse1");
        await using (var c = guardDb.OpenConnection())
            assert(await DsfinvkMasterDataStore.HasOpenVorgaengeAsync(c, CancellationToken.None),
                "R136 an aborted Vorgang belongs to the next closing, like every other Vorgang (master data guard)");

        // ---------- a payment journalled by an older build still matches its cart ----------
        var snapshot = new CheckoutSnapshot("op-136", new[] { parkLine }, 0, PaymentMethod.Cash, "kasse1", null, true, 500);
        var legacyJson = JsonSerializer.Serialize(snapshot).Replace(",\"TseVorgangId\":\"\",\"StartedAt\":null", "");
        assert(!legacyJson.Contains("TseVorgangId") &&
               JsonSerializer.Serialize(JsonSerializer.Deserialize<CheckoutSnapshot>(legacyJson)) == JsonSerializer.Serialize(snapshot),
            "R136 a journal entry without the new fields normalises to the same snapshot, so an open payment survives the update");
    }

    private static async Task<long> InsertSaleAsync(SqliteDatabase db, long receipt, DateTimeOffset startedAt, long cents, decimal vat)
    {
        await Task.Delay(15);
        await using var c = db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = "INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,cash_portion_cents,im_haus,started_at) VALUES($r,$at,'CASH',$t,$t,'TEST_FIXTURE','SALE',$t,0,$started); SELECT last_insert_rowid();";
        q.Parameters.AddWithValue("$r", receipt);
        q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        q.Parameters.AddWithValue("$t", cents);
        q.Parameters.AddWithValue("$started", startedAt.ToString("O"));
        var id = Convert.ToInt64(await q.ExecuteScalarAsync());
        await using var item = c.CreateCommand();
        item.CommandText = "INSERT INTO sale_items(sale_id,product_id,product_name,quantity,unit_price_cents,vat_rate,line_total_cents) VALUES($s,1,'Artikel',1,$t,$v,$t);";
        item.Parameters.AddWithValue("$s", id);
        item.Parameters.AddWithValue("$t", cents);
        item.Parameters.AddWithValue("$v", (double)vat);
        await item.ExecuteNonQueryAsync();
        return id;
    }

    private static List<Dictionary<string, string>> Csv(string path)
    {
        var lines = File.ReadAllLines(path);
        var header = lines[0].Split(';');
        return lines.Skip(1)
            .Select(line => line.Split(';'))
            .Select(cells => header.Select((name, i) => (name, value: i < cells.Length ? cells[i] : "")).ToDictionary(x => x.name, x => x.value))
            .ToList();
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static string TseTime(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private sealed class RecordingTseProvider : ITseProvider
    {
        public readonly List<TseTransactionStartRequest> Starts = new();
        public readonly List<ulong> StartNumbers = new();
        public readonly List<TseTransactionFinishRequest> Finishes = new();
        private ulong _next = 100;
        private ulong _counter = 1;

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
            var number = _next++;
            StartNumbers.Add(number);
            // a start log time clearly before the finish
            var log = DateTimeOffset.UtcNow.AddSeconds(-30);
            return Task.FromResult(new TseTransactionResult(true, "OK", number, _counter++, new DateTimeOffset(log.Ticks - log.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero), "FAKE-SERIAL", "c3RhcnQ="));
        }

        public Task<TseTransactionResult> UpdateTransactionAsync(TseTransactionUpdateRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("Kassenbeleg-V1 uses no UpdateTransaction (Anhang I).");

        public Task<TseTransactionResult> FinishTransactionAsync(TseTransactionFinishRequest request, CancellationToken ct = default)
        {
            Finishes.Add(request);
            return Task.FromResult(new TseTransactionResult(true, "OK", request.TransactionNumber, _counter++, DateTimeOffset.UtcNow, "FAKE-SERIAL", "ZmluaXNo"));
        }

        public Task<TseExportResult> ExportTarAsync(string targetPath, CancellationToken ct = default) =>
            Task.FromResult(new TseExportResult(false, "Fake export"));
    }
}
