using System.Net.Http;
using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

// R103: digital/QR receipt - a paperless alternative shown on screen
// whenever BON EIN/AUS is off for a sale, served from a small local
// TcpListener-based HTTP server on the till itself (deliberately not
// System.Net.HttpListener, which needs a one-time admin "netsh http add
// urlacl" reservation to bind anything but localhost - this cashier app
// must keep working under a normal Windows user account). Unlike ZVT/TSE
// hardware, this server is real, in-process, and fully testable end to
// end - a genuine HTTP request against it is made below, not just a
// unit-level check of its pieces.
public static class R103ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert,
        Func<Func<Task>, string, Task> reject)
    {
        var dir = Path.Combine(root, "r103-digital-receipt");
        Directory.CreateDirectory(dir);

        // 1) DigitalReceiptToken: unguessable and unique - never the sale's
        // own receipt number, or a customer's Bon could be enumerated by
        // guessing/incrementing a URL.
        var tokenA = DigitalReceiptToken.New();
        var tokenB = DigitalReceiptToken.New();
        assert(
            tokenA.Length >= 20 && tokenA != tokenB && !tokenA.Contains('+') && !tokenA.Contains('/'),
            $"R103 DigitalReceiptToken.New produces long, unique, URL-safe tokens (actual lengths: {tokenA.Length}/{tokenB.Length}, equal: {tokenA == tokenB})");

        // 2) DigitalReceiptHtml: carries the same legally-relevant content
        // as the printed receipt - this page may be the customer's only
        // copy of the Beleg, not a stripped-down summary.
        var testSale = new Sale
        {
            Id = 1,
            ReceiptNumber = 103001,
            CreatedAt = DateTimeOffset.Now,
            PaymentMethod = PaymentMethod.Mixed,
            TotalCents = 1000,
            CashPortionCents = 400,
            CardPortionCents = 600,
            Lines = new[] { new CartLine { ProductName = "R103 Artikel", Quantity = 2, UnitPriceCents = 500, VatRate = 19m } }
        };
        var testHtml = DigitalReceiptHtml.Render(testSale, "R103 Testladen", "Teststraße 1", "12/345", "DE123456789", fiscalTestMode: true);
        assert(
            testHtml.Contains("R103 Testladen") && testHtml.Contains("103001") && testHtml.Contains("R103 Artikel") &&
            testHtml.Contains("TESTBON") && testHtml.Contains("Bar") && testHtml.Contains("Karte"),
            "R103 the rendered HTML carries company name, receipt number, line items, the test-mode banner, and the Bar/Karte split");

        // R122 CORRECTION (audit finding F5): this fixture used to omit the
        // company address, the eAS serial and the TSE log time, and the page
        // still printed "Elektronischer Beleg gem. §6 KassenSichV" - which is
        // exactly the defect F5 describes, silently accepted here because the
        // assertion only looked for the fields that WERE set. The fixture now
        // represents what it always claimed to: a genuinely complete, signed
        // Beleg. R122ReviewTests covers the incomplete case explicitly.
        var realSale = new Sale
        {
            Id = 2,
            ReceiptNumber = 103002,
            CreatedAt = DateTimeOffset.Now,
            PaymentMethod = PaymentMethod.Cash,
            TotalCents = 500,
            CashPortionCents = 500,
            TseSerialNumber = "TSE-SERIAL-1",
            TseTransactionNumber = "42",
            TseSignatureCounter = "7",
            TseSignature = "abcdef123456",
            TseLogTime = DateTimeOffset.Now,
            Lines = new[] { new CartLine { ProductName = "R103 Artikel 2", Quantity = 1, UnitPriceCents = 500, VatRate = 19m } }
        };
        var realHtml = DigitalReceiptHtml.Render(
            realSale, "R103 Testladen", "Teststr. 1, 10115 Berlin", "", "", fiscalTestMode: false, easSerial: "TORPOS-R103");
        assert(
            !realHtml.Contains("TESTBON") && realHtml.Contains("TSE-SERIAL-1") && realHtml.Contains("abcdef123456") && realHtml.Contains("KassenSichV"),
            "R103 a non-test sale's page shows the real TSE fields and no TESTBON banner");

        // 3) The server itself: start it, register a real sale, and make an
        // actual HTTP request against it - a genuine end-to-end check,
        // unlike anything touching ZVT/TSE hardware in this suite.
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r103.db"));
        var sales = new SaleRepository(db);
        var settings = new SettingsRepository(db);

        // R132: company data are set up before the first sale; changing them
        // afterwards needs a closing first (DSFinV-K 3.2).
        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["company.name"] = "R103 Server GmbH",
            // R115: the server no longer binds every interface, so the test
            // pins it to loopback instead of depending on this machine
            // happening to have a LAN IPv4.
            ["receipt.digital_qr.bind_address"] = "127.0.0.1"
        });

        long saleId;
        using (var c = db.OpenConnection())
        using (var q = c.CreateCommand())
        {
            q.CommandText = """
                INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,cash_portion_cents,card_portion_cents)
                VALUES(103099,$now,'CASH',700,700,'TEST_TSE_NOT_CONNECTED','SALE',700,0);
                SELECT last_insert_rowid();
                """;
            q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
            saleId = Convert.ToInt64(q.ExecuteScalar());
            using var item = c.CreateCommand();
            item.CommandText = "INSERT INTO sale_items(sale_id,product_id,product_name,quantity,unit_price_cents,vat_rate,line_total_cents) VALUES($sale,1,'R103 Server-Artikel',1,700,19,700);";
            item.Parameters.AddWithValue("$sale", saleId);
            item.ExecuteNonQuery();
        }

        var server = new DigitalReceiptService(db, settings, sales);
        await server.StartAsync();
        assert(
            server.IsRunning && server.Port > 0 && server.BoundAddress == "127.0.0.1",
            $"R103/R115 the local receipt server starts on exactly the configured address (running: {server.IsRunning}, port: {server.Port}, bound: {server.BoundAddress})");

        try
        {
            var token = await server.RegisterAsync(saleId);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var okResponse = await http.GetAsync($"http://{server.BoundAddress}:{server.Port}/r/{token}");
            var okBody = await okResponse.Content.ReadAsStringAsync();
            assert(
                okResponse.IsSuccessStatusCode && okBody.Contains("R103 Server GmbH") && okBody.Contains("R103 Server-Artikel"),
                $"R103 a real HTTP GET against the running server for a registered token returns 200 with the right sale's content (status: {okResponse.StatusCode})");

            var missingResponse = await http.GetAsync($"http://{server.BoundAddress}:{server.Port}/r/this-token-does-not-exist");
            assert(
                missingResponse.StatusCode == System.Net.HttpStatusCode.NotFound,
                $"R103 an unknown token returns 404, not the wrong sale or a server error (actual: {missingResponse.StatusCode})");

            var rootResponse = await http.GetAsync($"http://{server.BoundAddress}:{server.Port}/");
            assert(
                rootResponse.StatusCode == System.Net.HttpStatusCode.NotFound,
                $"R103 a path outside the one /r/ route also returns 404, not an unhandled server error (actual: {rootResponse.StatusCode})");
        }
        finally
        {
            await server.StopAsync();
        }

        assert(
            !server.IsRunning,
            "R103 StopAsync actually stops the server (IsRunning false afterward)");
    }
}
