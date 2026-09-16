using System.Net.Http;
using TorPos.Infrastructure;

// R115: closes finding G2 (High) from this session's full project audit.
//
// The digital receipt server bound IPAddress.Any - every interface, including
// a guest WiFi or a WAN-facing NIC - and served fiscal receipt content over
// plain HTTP with no authentication beyond the token. On top of that it had
// no socket timeout, no request size limit and no concurrency cap, and its
// tokens never expired even though the 404 page always claimed a link could
// be "abgelaufen".
public static class R115ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "r115-receipt-server");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r115.db"));
        var sales = new SaleRepository(db);
        var settings = new SettingsRepository(db);

        long InsertSale(long receipt)
        {
            using var c = db.OpenConnection();
            using var q = c.CreateCommand();
            q.CommandText = """
                INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,cash_portion_cents,card_portion_cents)
                VALUES($r,$now,'CASH',500,500,'TEST_FIXTURE','SALE',500,0);
                SELECT last_insert_rowid();
                """;
            q.Parameters.AddWithValue("$r", receipt);
            q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
            var id = Convert.ToInt64(q.ExecuteScalar());
            using var item = c.CreateCommand();
            item.CommandText = "INSERT INTO sale_items(sale_id,product_id,product_name,quantity,unit_price_cents,vat_rate,line_total_cents) VALUES($sale,1,'R115 Artikel',1,500,19,500);";
            item.Parameters.AddWithValue("$sale", id);
            item.ExecuteNonQuery();
            return id;
        }

        // Age a token by rewriting its created_at, the same way a QR printed
        // days ago would look today.
        void AgeToken(string token, TimeSpan age)
        {
            using var c = db.OpenConnection();
            using var q = c.CreateCommand();
            q.CommandText = "UPDATE digital_receipts SET created_at=$at WHERE token=$t;";
            q.Parameters.AddWithValue("$at", DateTimeOffset.Now.Subtract(age).ToString("O"));
            q.Parameters.AddWithValue("$t", token);
            q.ExecuteNonQuery();
        }

        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["company.name"] = "R115 Testladen",
            ["receipt.digital_qr.bind_address"] = "127.0.0.1",
            ["receipt.digital_qr.ttl_hours"] = "24"
        });

        var saleId = InsertSale(115001);
        var server = new DigitalReceiptService(db, settings, sales);
        await server.StartAsync();

        assert(
            server.IsRunning && server.BoundAddress == "127.0.0.1",
            $"R115 the receipt server binds exactly one configured address instead of every interface (bound: {server.BoundAddress})");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            // A fresh token works.
            var fresh = await server.RegisterAsync(saleId);
            var freshResponse = await http.GetAsync($"http://{server.BoundAddress}:{server.Port}/r/{fresh}");
            assert(
                freshResponse.IsSuccessStatusCode,
                $"R115 a freshly issued receipt link is served normally (status: {freshResponse.StatusCode})");

            // The same token, 25 hours old against a 24 hour window, is gone.
            AgeToken(fresh, TimeSpan.FromHours(25));
            var expiredResponse = await http.GetAsync($"http://{server.BoundAddress}:{server.Port}/r/{fresh}");
            assert(
                expiredResponse.StatusCode == System.Net.HttpStatusCode.NotFound,
                $"R115 a receipt link past its configured lifetime is no longer served (status: {expiredResponse.StatusCode})");

            // Just inside the window still works - the cut-off is the
            // configured age, not merely "not today".
            var borderline = await server.RegisterAsync(saleId);
            AgeToken(borderline, TimeSpan.FromHours(23));
            var borderlineResponse = await http.GetAsync($"http://{server.BoundAddress}:{server.Port}/r/{borderline}");
            assert(
                borderlineResponse.IsSuccessStatusCode,
                $"R115 a link still inside the lifetime keeps working (status: {borderlineResponse.StatusCode})");

            // A request that never terminates its request line must not be
            // able to hold the connection or grow the server's buffers
            // without bound.
            using var raw = new System.Net.Sockets.TcpClient();
            await raw.ConnectAsync(System.Net.IPAddress.Loopback, server.Port);
            using (var rawStream = raw.GetStream())
            {
                var junk = System.Text.Encoding.ASCII.GetBytes(new string('A', 64 * 1024));
                try { await rawStream.WriteAsync(junk); await rawStream.FlushAsync(); }
                catch { /* server may already have closed the connection */ }
            }

            // The server must still answer normally afterwards.
            var afterFlood = await http.GetAsync($"http://{server.BoundAddress}:{server.Port}/r/{borderline}");
            assert(
                afterFlood.IsSuccessStatusCode,
                $"R115 an over-long request never terminated by a newline is dropped without taking the server down (status: {afterFlood.StatusCode})");
        }
        finally
        {
            await server.StopAsync();
        }

        assert(
            !server.IsRunning && server.BoundAddress is not null,
            "R115 stopping the server clears IsRunning while the last bound address stays readable for diagnostics");
    }
}
