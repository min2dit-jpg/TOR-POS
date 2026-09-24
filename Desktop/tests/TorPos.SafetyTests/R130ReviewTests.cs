using System.Globalization;
using System.Text;
using TorPos.Core;
using TorPos.Infrastructure;

// R130: the data handed to the TSE follows DSFinV-K 2.4 Anhang I.
//
// Found while starting the DSFinV-K export: FiscalProcessData produced a format
// of TOR's own ("Beleg^timestamp^Betrag-Summe:...^UStNormal:...^Beleg-Nr:..."),
// sent that data already at StartTransaction, and signed a Storno as
// "AVBelegstorno" - which Anhang B and I rule out for a till secured by a TSE.
// Seven older tests had pinned the invented format (R78, R80, R82, R83, R101,
// R108, R121); they were rewritten. The expected strings below are the worked
// examples printed in Anhang I itself, so this test does not grade the code
// against its own idea of the format.
public static class R130ReviewTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        CartLine Line(string name, long unitCents, decimal vat, decimal qty = 1m, string variant = "") =>
            new() { ProductName = name, VariantName = variant, Quantity = qty, UnitPriceCents = unitCents, VatRate = vat };

        Sale SaleOf(PaymentMethod method, params CartLine[] lines)
        {
            var total = lines.Sum(l => l.LineTotalCents);
            return new Sale
            {
                ReceiptNumber = 130000,
                CreatedAt = DateTimeOffset.Now,
                PaymentMethod = method,
                TotalCents = total,
                CashPortionCents = method == PaymentMethod.Cash ? total : 0,
                CardPortionCents = method == PaymentMethod.Card ? total : 0,
                Lines = lines
            };
        }

        string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

        // ---------- Kassenbeleg-V1, examples from Anhang I ----------
        assert(
            Text(FiscalProcessData.BuildKassenbeleg(SaleOf(PaymentMethod.Cash, Line("Ware", 10000, 19m)))) ==
                "Beleg^100.00_0.00_0.00_0.00_0.00^100.00:Bar",
            "R130 Anhang I example '100 Umsatz 19%, Bar bezahlt' is reproduced exactly");
        assert(
            Text(FiscalProcessData.BuildKassenbeleg(SaleOf(PaymentMethod.Card, Line("A", 5000, 19m), Line("B", 5000, 7m)))) ==
                "Beleg^50.00_50.00_0.00_0.00_0.00^100.00:Unbar",
            "R130 Anhang I example '50 Umsatz 19%, 50 Umsatz 7%, Bezahlt mit Visa' is reproduced exactly");
        assert(
            Text(FiscalProcessData.BuildKassenbeleg(SaleOf(PaymentMethod.Cash, Line("A", 405, 19m), Line("B", 300, 7m)))) ==
                "Beleg^4.05_3.00_0.00_0.00_0.00^7.05:Bar",
            "R130 the processData of the Anhang I QR-code example is reproduced exactly");

        var cardOnly = Text(FiscalProcessData.BuildKassenbeleg(SaleOf(PaymentMethod.Card, Line("A", 250, 19m))));
        assert(!cardOnly.Contains(":Bar"),
            $"R130 a payment of 0.00 is left out, as Anhang I requires (actual: {cardOnly})");

        var withZeroRate = Text(FiscalProcessData.BuildKassenbeleg(SaleOf(PaymentMethod.Cash, Line("A", 200, 19m), Line("B", 100, 0m))));
        assert(withZeroRate == "Beleg^2.00_0.00_0.00_0.00_1.00^3.00:Bar",
            $"R130 a 0 % position goes into the fifth tax container (actual: {withZeroRate})");

        var mixedStorno = new Sale
        {
            ReceiptNumber = 130001, TransactionType = "STORNO", OriginalSaleId = 1, OriginalReceiptNumber = 129999,
            CreatedAt = DateTimeOffset.Now, PaymentMethod = PaymentMethod.Mixed,
            TotalCents = 1000, CashPortionCents = 400, CardPortionCents = 600,
            Lines = new[] { Line("A", 1000, 19m) }
        };
        var mixedStornoText = Text(FiscalProcessData.BuildKassenbeleg(mixedStorno));
        assert(mixedStornoText == "Beleg^-10.00_0.00_0.00_0.00_0.00^-4.00:Bar_-6.00:Unbar",
            $"R130 a Storno of a mixed payment reverses every amount, cash still first (actual: {mixedStornoText})");

        var unsupported = false;
        try { FiscalProcessData.BuildKassenbeleg(SaleOf(PaymentMethod.Cash, Line("A", 100, 16m))); }
        catch (UnsupportedVatRateException ex) { unsupported = ex.Rate == 16m; }
        assert(unsupported,
            "R130 a VAT rate without a place in the Anhang I tax containers is refused instead of being put into a guessed container");

        // ---------- Bestellung-V1, example from Anhang I ----------
        var eis = new ParkedReceipt
        {
            Lines = new[] { Line("Eisbecher \"Himbeere\"", 399, 7m, 2m), Line("Eiskaffee", 299, 7m) }
        };
        assert(
            Text(FiscalProcessData.BuildBestellung(eis)) == "2;\"Eisbecher \"\"Himbeere\"\"\";3.99\r1;\"Eiskaffee\";2.99",
            "R130 Anhang I example '2 x Eisbecher \"Himbeere\" zu je 3.99 und 1 x Eiskaffee zu 2.99' is reproduced exactly, quotes doubled, CR between lines");

        assert(
            FiscalProcessData.Quantity(1m) == "1" && FiscalProcessData.Quantity(0.5m) == "0.5" &&
            FiscalProcessData.Quantity(0.4516m) == "0.451" && FiscalProcessData.Quantity(2.000m) == "2",
            "R130 quantities use as few decimals as possible, at most three, cut off rather than rounded");
        assert(
            FiscalProcessData.BestellungText(new ParkedReceipt { Lines = new[] { Line("Döner", 850, 7m, 1m, "Groß") } }) == "1;\"Döner · Groß\";8.50",
            "R130 a variant is named the way the receipt prints it");

        // The same bytes on a Turkish or English Windows.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            var turkish = Text(FiscalProcessData.BuildKassenbeleg(SaleOf(PaymentMethod.Cash, Line("A", 123456, 19m))));
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var german = Text(FiscalProcessData.BuildKassenbeleg(SaleOf(PaymentMethod.Cash, Line("A", 123456, 19m))));
            assert(turkish == "Beleg^1234.56_0.00_0.00_0.00_0.00^1234.56:Bar" && turkish == german,
                "R130 amounts use '.' and no thousands separator whatever the Windows language");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        // ---------- what actually reaches the TSE ----------
        var dir = Path.Combine(root, "r130-process-data");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r130.db"));
        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        var outages = new TseOutageRepository(db, audit);
        var provider = new RecordingTseProvider();
        var failSafe = new TseFailSafeService(provider, outages, audit);
        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["tse.status"] = "AKTIV",
            ["tse.client_id"] = "KASSE-1",
        });

        var receiptNumber = 130100L;
        async Task<Sale> StoredSaleAsync(params CartLine[] lines)
        {
            var sale = SaleOf(PaymentMethod.Cash, lines);
            await using var c = db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = "INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status) VALUES($r,$now,'CASH',$t,$t,'TEST_FIXTURE'); SELECT last_insert_rowid();";
            q.Parameters.AddWithValue("$r", ++receiptNumber);
            q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
            q.Parameters.AddWithValue("$t", sale.TotalCents);
            sale.Id = Convert.ToInt64(await q.ExecuteScalarAsync());
            return sale;
        }

        var saleSigning = new SaleFiscalSigningService(failSafe, settings, new SaleRepository(db));
        var signed = await StoredSaleAsync(Line("A", 1000, 19m));
        await saleSigning.SignAsync(signed, "tester");
        assert(provider.Starts.Count == 1 && provider.Starts[0].ProcessData.Length == 0 && provider.Starts[0].ProcessType == "",
            "R130 StartTransaction of a sale carries neither processType nor processData (Anhang I)");
        assert(provider.Finishes.Count == 1 && provider.Finishes[0].ProcessType == "Kassenbeleg-V1" &&
               Text(provider.Finishes[0].ProcessData) == "Beleg^10.00_0.00_0.00_0.00_0.00^10.00:Bar",
            "R130 FinishTransaction of a sale carries Kassenbeleg-V1 and the Anhang I processData");
        assert(!signed.TseOutage && signed.TseTransactionNumber == "7",
            "R130 the sale is signed normally with the corrected data");

        var badRate = await StoredSaleAsync(Line("A", 1000, 16m));
        await saleSigning.SignAsync(badRate, "tester");
        assert(provider.Starts.Count == 1,
            "R130 a sale whose data cannot be valid never opens a TSE transaction");
        var openOutage = await outages.GetOpenAsync();
        assert(badRate.TseOutage && openOutage is not null && openOutage.Reason.Contains("16"),
            "R130 it is documented as a TSE outage naming the unsupported rate, so the receipt carries the outage note instead of a silent gap");
        await outages.CloseOpenAsync("tester");

        // F-5: Kassenbeleg-V1 refuses a sale whose positions do not add up to
        // its total ("inkonsistent"). That must end as a documented outage too.
        var inconsistent = await StoredSaleAsync(Line("A", 1000, 19m));
        inconsistent.TotalCents += 1;
        inconsistent.CashPortionCents += 1;
        var escaped = false;
        try { await saleSigning.SignAsync(inconsistent, "tester"); }
        catch (InvalidOperationException) { escaped = true; }
        var inconsistentOutage = await outages.GetOpenAsync();
        assert(!escaped && provider.Starts.Count == 1 && inconsistent.TseOutage &&
               inconsistentOutage is not null && inconsistentOutage.Reason.Contains("inkonsistent"),
            "F-5 an inconsistent Kassenbeleg never opens a TSE transaction and is documented as an outage instead of escaping as an exception");
        await outages.CloseOpenAsync("tester");

        var orders = new ParkedReceiptRepository(db);
        var orderSigning = new OrderFiscalSigningService(failSafe, settings, orders);
        var order = await orders.ParkAsync(new[] { Line("Döner", 850, 7m, 2m) }, 0, "tester", assignPickupNumber: true, orderPrint: false);
        await orderSigning.SignAsync(order, "tester");
        assert(provider.Starts.Count == 2 && provider.Starts[1].ProcessData.Length == 0 && provider.Starts[1].ProcessType == "",
            "R130 StartTransaction of an order carries neither processType nor processData (Anhang I)");
        assert(provider.Finishes.Count == 2 && provider.Finishes[1].ProcessType == "Bestellung-V1" &&
               Text(provider.Finishes[1].ProcessData) == "2;\"Döner\";8.50",
            "R130 FinishTransaction of an order carries Bestellung-V1 and the Anhang I processData");
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
            return Task.FromResult(new TseTransactionResult(true, "OK", 7, 1, DateTimeOffset.UtcNow, "FAKE-SERIAL", "c3RhcnQ="));
        }

        public Task<TseTransactionResult> UpdateTransactionAsync(TseTransactionUpdateRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("Kassenbeleg-V1 uses no UpdateTransaction (Anhang I).");

        public Task<TseTransactionResult> FinishTransactionAsync(TseTransactionFinishRequest request, CancellationToken ct = default)
        {
            Finishes.Add(request);
            return Task.FromResult(new TseTransactionResult(true, "OK", request.TransactionNumber, 2, DateTimeOffset.UtcNow, "FAKE-SERIAL", "ZmluaXNo"));
        }

        public Task<TseExportResult> ExportTarAsync(string targetPath, CancellationToken ct = default) =>
            Task.FromResult(new TseExportResult(false, "Fake export"));
    }
}
