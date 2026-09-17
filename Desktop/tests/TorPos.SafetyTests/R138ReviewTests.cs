using System.Text;
using System.Text.Json;
using TorPos.Core;
using TorPos.Infrastructure;

// R138: no Kassenbeleg transaction waits open with a parked receipt, a booked
// sale is never ended as an abort, and a receipt discount is part of the order.
//
// AEAO zu § 146a Nr. 2.2.2 / 2.2.3.3: the transaction ends when the Vorgang ends.
// Nr. 2.2.3.6.2: long-running Vorgänge are secured as "Bestellung", their
// payment as "Kassenbeleg". BMF Kassen-FAQ: Vorgänge that are not completed "werden
// entweder als Bestellungen in eigenen Transaktionen oder als ‚andere Vorgänge'
// abgesichert, die in der DSFinV-K über den Abrechnungskreis oder eine
// Referenzierung miteinander verknüpft sind"; "Alle Veränderungen müssen
// nachvollziehbar in Form einer Bestellung abgebildet werden. Die Summe aus der
// Menge multipliziert mit dem Bruttopreis aller Bestellungen muss dem
// Gesamtbruttobetrag der entsprechenden Rechnungen entsprechen."
public static class R138ReviewTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "r138-park");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r138.db"));

        CartLine L(long id, string name, decimal qty, long price, decimal vat) =>
            new() { ProductId = id, ProductName = name, Quantity = qty, UnitPriceCents = price, VatRate = vat };

        // ---------- the discount as order positions ----------
        var lines = new[] { L(1, "Döner", 1, 700, 7m), L(2, "Cola", 1, 250, 19m) };
        var withDiscount = OrderBestellungDelta.WithDiscount(lines, 100);
        var discountLines = withDiscount.Where(OrderBestellungDelta.IsDiscount).ToList();
        var receiptVat = VatSummaryCalculator.Compute(lines, 100);
        assert(discountLines.Count == 2 && discountLines.Sum(l => l.LineTotalCents) == -100 &&
               withDiscount.Sum(l => l.LineTotalCents) == 850 &&
               discountLines.All(d => receiptVat.Single(v => v.Rate == d.VatRate).GrossCents ==
                                      lines.Where(l => l.VatRate == d.VatRate).Sum(l => l.LineTotalCents) + d.LineTotalCents) &&
               OrderBestellungDelta.WithDiscount(lines, 0).Count == 2,
            "R138 a receipt discount becomes order positions per VAT rate, split exactly as on the receipt, so the positions add up to the amount paid");

        // ---------- a parked receipt is an order ----------
        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["company.name"] = "R138 Kiosk",
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
        var records = new OrderBestellungRepository(db);
        var orders = new OrderFiscalSigningService(tse, settings, parked) { Vorgaenge = vorgaenge, Bestellungen = records };
        var sales = new SaleRepository(db);
        var saleSigning = new SaleFiscalSigningService(tse, settings, sales) { Vorgaenge = vorgaenge };

        await vorgaenge.StartAsync("kiosk-1", false, DateTimeOffset.Now.AddMinutes(-2), "kasse1");
        var receipt = await parked.ParkAsync(lines, 100, "kasse1");
        await orders.SignInVorgangAsync(receipt, "kiosk-1", DateTimeOffset.Now.AddMinutes(-2), "kasse1");
        var parkedText = Encoding.UTF8.GetString(provider.Finishes[^1].ProcessData);
        var opened = await vorgaenge.GetAsync("kiosk-1");
        assert(provider.Finishes[^1].ProcessType == "Bestellung-V1" && opened!.State == TseVorgangService.Finished &&
               parkedText.StartsWith("1;\"Döner\";7.00\r1;\"Cola\";2.50\r1;\"Rabatt\";-") &&
               (await records.SecuredAsync(receipt.Id)).Secured.Sum(l => l.LineTotalCents) == receipt.TotalCents,
            "R138 parking ends the running transaction as Bestellung-V1 with the positions and the discount - no Kassenbeleg transaction stays open while the receipt waits");

        // at payment the discount is 2,00 instead of 1,00: the change is secured before the receipt
        var atPayment = (await parked.GetOpenByIdAsync(receipt.Id))!;
        var orderedLines = atPayment.Lines;
        atPayment.DiscountCents = 200;
        var discountChange = await orders.SecureChangeAsync(atPayment, orderedLines, "", null, "kasse1");
        var (_, net) = await records.SecuredAsync(receipt.Id);
        assert(discountChange is { Kind: OrderBestellungKind.Aenderung } &&
               discountChange.Lines.All(OrderBestellungDelta.IsDiscount) &&
               discountChange.Lines.Count(l => l.Quantity < 0) == 2 && discountChange.Lines.Count(l => l.Quantity > 0) == 2 &&
               net.Sum(l => l.LineTotalCents) == 750,
            "R138 a changed discount is an order change of its own; all orders together equal the gross amount of the receipt (BMF Kassen-FAQ)");

        // ---------- a booked sale whose transaction was never ended ----------
        await vorgaenge.StartAsync("sale-crash", false, DateTimeOffset.Now.AddMinutes(-1), "kasse1");
        var crashStart = await vorgaenge.GetAsync("sale-crash");
        var crashedSale = await InsertSaleAsync(db, 138001, 950);
        await InsertCommittedCheckoutAsync(db, "op-crash", crashedSale, "sale-crash");

        await vorgaenge.StartAsync("sale-signed", false, DateTimeOffset.Now.AddMinutes(-1), "kasse1");
        var signedSale = await InsertSaleAsync(db, 138002, 300);
        await sales.RecordTseResultAsync(signedSale, SaleTseResult.SignedResult("KASSE-1", "999", "9", "FAKE-SERIAL", "c2ln", DateTimeOffset.Now));
        await InsertCommittedCheckoutAsync(db, "op-signed", signedSale, "sale-signed");

        await vorgaenge.StartAsync("no-sale", false, DateTimeOffset.Now.AddMinutes(-1), "kasse1");

        var finishesBefore = provider.Finishes.Count;
        var finished = await saleSigning.FinishCommittedVorgaengeAsync("SYSTEM");
        var aborted = await vorgaenge.AbortOrphansAsync(null, "SYSTEM");
        var crashed = (await sales.GetByIdAsync(crashedSale))!;
        long abortedRows;
        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM aborted_vorgaenge WHERE vorgang_id IN ('sale-crash','sale-signed');";
            abortedRows = Convert.ToInt64(await q.ExecuteScalarAsync());
        }
        var crashFinish = provider.Finishes[finishesBefore];
        assert(finished == 2 &&
               crashFinish.ProcessType == "Kassenbeleg-V1" && crashFinish.TransactionNumber.ToString() == crashStart!.TransactionNumber &&
               Encoding.UTF8.GetString(crashFinish.ProcessData) == "Beleg^9.50_0.00_0.00_0.00_0.00^9.50:Bar" &&
               crashed.TseTransactionNumber == crashStart.TransactionNumber && crashed.TseStartLogTime == crashStart.StartLogTime &&
               (await vorgaenge.GetAsync("sale-crash"))!.State == TseVorgangService.Finished &&
               (await vorgaenge.GetAsync("sale-signed"))!.State == TseVorgangService.Finished,
            "R138 a sale booked before its transaction was ended is finished with its own receipt data on its own transaction; one already signed only has its Vorgang closed");
        assert(aborted == 1 && abortedRows == 0 && (await vorgaenge.GetAsync("no-sale"))!.State == TseVorgangService.Aborted &&
               provider.Finishes.Count == finishesBefore + 2,
            "R138 only a Vorgang without a booked sale ends as aborted; a paid sale is never recorded as AVBelegabbruch");

        // ---------- export ----------
        await Task.Delay(15);
        await new BusinessManagementService(db, settings, audit).CreateZArchiveAsync("chef", "TEST");
        var exporter = new DsfinvkExportService(db, settings);
        var folder = await exporter.ExportAsync(DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddMinutes(1), Path.Combine(dir, "out"));
        // Bonpos: BON_ID is field 3, ARTIKELTEXT field 6, GV_TYP field 8.
        var bon = $"\"BE-{receipt.ParkNumber}-1\"";
        var positions = File.ReadAllLines(Path.Combine(folder, "lines.csv")).Skip(1)
            .Select(p => p.Split(';')).Where(f => f[3] == bon).ToList();
        assert(positions.Count(f => f[6] == "\"Rabatt\"" && f[8] == "\"Rabatt\"") == 2 &&
               positions.Count(f => f[8] == "\"Umsatz\"") == 2,
            "R138 the discount of a parked receipt is exported as its own Rabatt positions of the AVBestellung");
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

    private sealed class RecordingTseProvider : ITseProvider
    {
        public readonly List<TseTransactionFinishRequest> Finishes = new();
        private ulong _next = 800;
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
            var log = DateTimeOffset.UtcNow.AddSeconds(-20);
            return Task.FromResult(new TseTransactionResult(true, "OK", _next++, _counter++, new DateTimeOffset(log.Ticks - log.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero), "FAKE-SERIAL", "c3RhcnQ="));
        }

        public Task<TseTransactionResult> UpdateTransactionAsync(TseTransactionUpdateRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TseTransactionResult> FinishTransactionAsync(TseTransactionFinishRequest request, CancellationToken ct = default)
        {
            Finishes.Add(request);
            return Task.FromResult(new TseTransactionResult(true, "OK", request.TransactionNumber, _counter++, DateTimeOffset.UtcNow, "FAKE-SERIAL", "ZmluaXNo"));
        }

        public Task<TseExportResult> ExportTarAsync(string targetPath, CancellationToken ct = default) =>
            Task.FromResult(new TseExportResult(false, "Fake export"));
    }
}
