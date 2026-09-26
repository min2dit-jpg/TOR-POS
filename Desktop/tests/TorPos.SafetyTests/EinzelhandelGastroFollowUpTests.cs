using System.IO.Compression;
using TorPos.Core;
using TorPos.Infrastructure;

// Einzelhandel/Gastro follow-ups from the 24.09.2026 review: O-9, O-10, O-13,
// O-14, O-17 and the PDF text encoding (§6).
public static class EinzelhandelGastroFollowUpTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "einzelhandel-gastro-followup");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "eg.db");
        var db = await SafetyDatabase.CreateCurrentAsync(path);

        // O-9: the Sonstiges clean-up ran on every start and SQLite's ASCII-only
        // UPPER() never matched 'Döner'.
        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = """
                INSERT OR IGNORE INTO categories(name,is_active,edition_scope) VALUES('Sonstiges',1,'ALL');
                UPDATE categories SET is_active=1 WHERE name='Sonstiges';
                INSERT OR IGNORE INTO categories(name,is_active,edition_scope) VALUES('Döner',1,'ALL');
                UPDATE categories SET edition_scope='ALL' WHERE name='Döner';
                """;
            await q.ExecuteNonQueryAsync();
        }
        await new SqliteDatabase(path).InitializeAsync();
        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "SELECT is_active FROM categories WHERE name='Sonstiges';";
            assert(Convert.ToInt64(await q.ExecuteScalarAsync()) == 1,
                "O-9 a Sonstiges category created after the one-time migration survives the next start");
            q.CommandText = "SELECT edition_scope FROM categories WHERE name='Döner';";
            assert((string?)await q.ExecuteScalarAsync() == "IMBISS",
                "O-9 the legacy edition scope now matches 'Döner' although SQLite UPPER() folds ASCII only");
        }

        // O-10: an article used in an active menu cannot be deactivated silently.
        var repo = new ProductRepository(db);
        long categoryId;
        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "SELECT id FROM categories WHERE is_active=1 ORDER BY id LIMIT 1;";
            categoryId = Convert.ToInt64(await q.ExecuteScalarAsync());
        }
        var component = await repo.SaveWithStockAsync(new Product { CategoryId = categoryId, Name = "O10 Pommes", BasePriceCents = 300, VatRate = 7m }, null, 0, "o10");
        var menu = await repo.SaveWithStockAsync(new Product { CategoryId = categoryId, Name = "O10 Menü", BasePriceCents = 800, VatRate = 7m }, null, 0, "o10",
            comboItems: new[] { new ProductComboItem(0, component, "O10 Pommes", 1m, 0) });
        var refused = "";
        try { await repo.DeactivateProductAsync(component, "o10"); }
        catch (InvalidOperationException ex) { refused = ex.Message; }
        async Task<long> ScalarAsync(string sql)
        {
            await using var c = db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = sql;
            q.Parameters.AddWithValue("$m", menu);
            q.Parameters.AddWithValue("$p", component);
            return Convert.ToInt64(await q.ExecuteScalarAsync());
        }
        var recipeRows = await ScalarAsync("SELECT COUNT(*) FROM product_combo_items WHERE product_id=$m AND component_product_id=$p;");
        assert(refused.Contains("O10 Menü", StringComparison.Ordinal) && recipeRows == 1 &&
               await ScalarAsync("SELECT is_active FROM products WHERE id=$p;") == 1,
            $"O-10 deactivating a menu component is refused with the menu's name and the recipe stays intact (refused='{refused}', rows={recipeRows})");
        await repo.DeactivateProductAsync(menu, "o10");
        await repo.DeactivateProductAsync(component, "o10");
        assert(await ScalarAsync("SELECT COUNT(*) FROM products WHERE id IN ($m,$p) AND is_active=1;") == 0,
            "O-10 once the menu is deactivated its former component can be deactivated as well");

        // O-13: SMTPS on 465 and relays without login.
        var smtps = new ReportEmailService.MailConfig("a@b.de", "c@d.de", "mail.example", 465, true, "user@d.de", "pw");
        var submission = smtps with { Port = 587 };
        var relay = smtps with { Port = 25, Username = "", Password = "" };
        assert(ReportEmailService.UsesImplicitTls(smtps) && !ReportEmailService.UsesImplicitTls(submission) &&
               !ReportEmailService.UsesImplicitTls(smtps with { UseSsl = false }),
            "O-13 port 465 with TLS uses implicit TLS; 587 keeps STARTTLS");
        assert(ReportEmailService.RequiresAuthentication(submission) && !ReportEmailService.RequiresAuthentication(relay),
            "O-13 SMTP AUTH is required only when a user name is configured");

        // O-14: retention-relevant exports are part of the full backup.
        var data = Path.Combine(dir, "data");
        Directory.CreateDirectory(Path.Combine(data, "TseExports"));
        Directory.CreateDirectory(Path.Combine(data, "DATEV", "Kassenbuch"));
        await File.WriteAllTextAsync(Path.Combine(data, "TseExports", "export-1.tar"), "tar");
        await File.WriteAllTextAsync(Path.Combine(data, "DATEV", "Kassenbuch", "kb.csv"), "csv");
        var package = await new FullBackupService(db, data).CreateAsync(Path.Combine(dir, "packages"));
        using (var zip = ZipFile.OpenRead(package))
        {
            assert(zip.GetEntry("TseExports/export-1.tar") is not null && zip.GetEntry("DATEV/Kassenbuch/kb.csv") is not null,
                "O-14 the full backup contains TSE exports and DATEV exports (retention duty)");
        }

        // O-17: a partial return of goods from a Bon with Leergut.
        var lines = new[]
        {
            new CartLine { ProductId = 9001, ProductName = "Cola", Quantity = 1m, UnitPriceCents = 500, VatRate = 19m },
            new CartLine { ProductId = PfandProducts.Bottle25, ProductName = "PFAND-RÜCKGABE", Quantity = 12m, UnitPriceCents = -25, VatRate = 19m }
        };
        var paidTotal = lines.Sum(x => x.LineTotalCents); // 2,00 EUR paid by card
        var oldBaseRefused = false;
        try { CumulativeReturnProration.Allocate(0, 500, 0, 0, paidTotal, paidTotal, 0); }
        catch (InvalidOperationException) { oldBaseRefused = true; }
        var goods = ReturnProrationBase.Of(lines, paidTotal, 0);
        var refund = CumulativeReturnProration.Allocate(0, 500, 0, 0, goods.SubtotalCents, goods.TotalCents, goods.CashCents);
        assert(oldBaseRefused && paidTotal == 200 &&
               refund.TotalCents == 500 && refund.CardCents == 200 && refund.CashCents == 300 && refund.DiscountCents == 0,
            "O-17 returning the Cola from 'Cola 5,00 + Leergut -3,00' refunds 5,00: 2,00 to the card, the 3,00 bottle credit in cash");

        // O-8: a Bon cashed before midnight but after the last Tagesabschluss
        // (the evening trade of an Imbiss) can still be reversed after 00:00.
        var nightDb = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "o8-night.db"));
        var nightSales = new SaleRepository(nightDb);
        var yesterday = DateTimeOffset.Now.Date.AddDays(-1);
        long lateId, earlyId;
        await using (var c = nightDb.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = """
                INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents) VALUES(81001,$early,'CASH',500,500);
                INSERT INTO daily_closings(closed_at,operator_name) VALUES($closed,'o8');
                INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents) VALUES(81002,$late,'CASH',700,700);
                """;
            q.Parameters.AddWithValue("$early", new DateTimeOffset(yesterday.AddHours(12)).ToString("O"));
            q.Parameters.AddWithValue("$closed", new DateTimeOffset(yesterday.AddHours(20)).ToString("O"));
            q.Parameters.AddWithValue("$late", new DateTimeOffset(yesterday.AddHours(23).AddMinutes(30)).ToString("O"));
            await q.ExecuteNonQueryAsync();
            q.CommandText = "SELECT id FROM sales WHERE receipt_number=81002;";
            lateId = Convert.ToInt64(await q.ExecuteScalarAsync());
            q.CommandText = "SELECT id FROM sales WHERE receipt_number=81001;";
            earlyId = Convert.ToInt64(await q.ExecuteScalarAsync());
        }
        var lateReason = await nightSales.CheckReversalAllowedAsync(lateId, forFullStorno: true);
        var earlyReason = await nightSales.CheckReversalAllowedAsync(earlyId, forFullStorno: true);
        assert(
            lateReason is null && earlyReason?.Contains("Tagesabschluss", StringComparison.Ordinal) == true &&
            await nightSales.GetOpenZPeriodStartAsync() is { } periodStart && periodStart.Date == yesterday,
            $"O-8 yesterday's 23:30 Bon after the last Tagesabschluss stays reversible; the one before it does not (late='{lateReason}', early='{earlyReason}')");

        // §6: PDF text is WinAnsi (cp1252); Turkish letters outside it are transliterated, never "?".
        var pdf = SimplePdfWriter.PdfTextBytes("Dürüm … „Şiş“ Ağa İlık");
        var cp1252 = System.Text.Encoding.GetEncoding(1252);
        assert(!pdf.Contains((byte)'?') && cp1252.GetString(pdf) == "Dürüm … „Sis“ Aga Ilik",
            "PDF reports keep …, „“ and umlauts and write ş/ğ/ı/İ as s/g/i/I instead of '?'");
    }
}
