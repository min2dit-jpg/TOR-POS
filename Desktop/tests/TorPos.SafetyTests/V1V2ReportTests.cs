using System.Globalization;
using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

// V-1: X/Z VAT split and the DATEV Kassenbuch use the stored line total
// (what the customer paid and the TSE signed), not quantity x unit price,
// and a Leergut payout does not break the Z/DATEV reconciliation.
// V-2: management reports (Umsatzberichte, Monatsumsatz, Verkaufsstatistik)
// are net of BON STORNO/Teilretoure, like the Z.
public static class V1V2ReportTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        // ---------------- V-1 ----------------
        var dir = Path.Combine(root, "v1-report-line-totals");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "v1.db"));
        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        var management = new BusinessManagementService(db, settings, audit);

        // Keep report fixtures inside the current local calendar day. CI often
        // runs shortly after midnight; using DateTimeOffset.Now.AddMinutes(-9)
        // would then seed yesterday while the HEUTE report correctly starts at
        // today's midnight, making this otherwise deterministic V-2 test flaky.
        var localToday = DateTime.Today;
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(localToday);
        var now = new DateTimeOffset(
            localToday.AddMinutes(20),
            localOffset);

        // 0.500 kg x 19.90 EUR/kg with 10 % Angebot: the checkout charges
        // 8.95 EUR (stored), while 0.5 x 17.91 EUR/kg would give 8.96 EUR.
        await SeedAsync(db, 1001, now.AddMinutes(-9), "SALE", null, total: 895, cash: 895, card: 0,
            ("V1 Baklava", quantityMilli: 500, unitPrice: 1791, vat: 7m, lineTotal: 895, unit: "kg"));
        await SeedAsync(db, 1002, now.AddMinutes(-8), "SALE", null, total: 500, cash: 500, card: 0,
            ("V1 Cola", quantityMilli: 1000, unitPrice: 500, vat: 19m, lineTotal: 500, unit: "Stück"));
        // Leergut payout: a sale with a negative cash portion.
        await SeedAsync(db, 1003, now.AddMinutes(-7), "SALE", null, total: -150, cash: -150, card: 0,
            ("V1 Leergut", quantityMilli: -1000, unitPrice: 150, vat: 19m, lineTotal: -150, unit: "Stück"));

        var z = await management.CreateZArchiveAsync("v1", "PRODUCTION_ALLOWED");
        long vat7;
        long vat19;
        await using (var c = db.OpenConnection())
        await using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT vat7_gross_cents,vat19_gross_cents FROM z_report_archive WHERE id=$id;";
            q.Parameters.AddWithValue("$id", z.Id);
            await using var r = await q.ExecuteReaderAsync();
            await r.ReadAsync();
            vat7 = r.GetInt64(0);
            vat19 = r.GetInt64(1);
        }

        assert(
            z.GrossCents == 1245 && z.CashCents == 1245 && vat7 == 895 && vat19 == 350 && vat7 + vat19 == z.GrossCents,
            $"V-1 the Z MwSt. split uses the stored 8.95 EUR weighed-promotion line, so 7 % + 19 % equal the Z turnover (7 %: {vat7}, 19 %: {vat19}, Z: {z.GrossCents})");

        var email = new ReportEmailService(settings, management);
        var datev = new DatevKassenbuchAsciiService(db, settings, management, email, audit);
        IReadOnlyList<DatevKassenbuchAsciiRow>? rows = null;
        string error = "";
        try
        {
            rows = await datev.BuildRowsAsync(z);
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
        }

        assert(
            rows is not null &&
            rows.Any(x => x.VatRate == 7m && x.SignedAmountCents == 895) &&
            rows.Any(x => x.VatRate == 19m && x.SignedAmountCents == 350),
            $"V-1 the DATEV Kassenbuch counts the Leergut payout and the stored line totals, so it reconciles with the Z cash ({error})");

        // ---------------- V-2 ----------------
        var dir2 = Path.Combine(root, "v2-report-reversals");
        Directory.CreateDirectory(dir2);
        var db2 = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir2, "v2.db"));
        var management2 = new BusinessManagementService(db2, new SettingsRepository(db2), new AuditLogRepository(db2));
        var sale = await SeedAsync(db2, 2001, now.AddMinutes(-6), "SALE", null, total: 1000, cash: 1000, card: 0,
            ("V2 Artikel", quantityMilli: 2000, unitPrice: 500, vat: 19m, lineTotal: 1000, unit: "Stück"));
        await SeedAsync(db2, 2002, now.AddMinutes(-5), "RETURN", sale, total: 500, cash: 500, card: 0,
            ("V2 Artikel", quantityMilli: 1000, unitPrice: 500, vat: 19m, lineTotal: 500, unit: "Stück"));

        var turnover = string.Join("\n", (await management2.BuildTurnoverSummaryAsync()).Lines);
        var monthly = string.Join("\n", (await management2.BuildMonthlyTurnoverAsync()).Lines);
        assert(
            turnover.Contains("HEUTE: 1 Bons | Umsatz 5,00 EUR | Bar 5,00 EUR | Karte 0,00 EUR | Storno/Retoure 5,00 EUR", StringComparison.Ordinal) &&
            monthly.Contains($"{now.ToString("yyyy-MM", CultureInfo.InvariantCulture)} | 1 | 5,00 EUR | 5,00 EUR", StringComparison.Ordinal),
            "V-2 Umsatzberichte and Monatsumsatz subtract a Teilretoure from turnover and Bar, like the Z");

        var stats = string.Join("\n", (await management2.BuildSalesStatisticsAsync()).Lines);
        assert(
            stats.Contains("V2 Artikel | 1 | 5,00 EUR", StringComparison.Ordinal),
            "V-2 Verkaufsstatistik shows the quantity and revenue that remain after a Teilretoure");
    }

    private static async Task<long> SeedAsync(
        SqliteDatabase db,
        long receipt,
        DateTimeOffset at,
        string type,
        long? original,
        long total,
        long cash,
        long card,
        (string Name, long quantityMilli, long unitPrice, decimal vat, long lineTotal, string unit) line)
    {
        await using var c = db.OpenConnection();
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync();
        long saleId;
        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = """
                INSERT INTO sales(
                  receipt_number,created_at,payment_method,
                  subtotal_cents,discount_cents,total_cents,fiscal_status,
                  transaction_type,original_sale_id,cash_portion_cents,card_portion_cents,started_at)
                VALUES($r,$at,'CASH',$total,0,$total,'PRODUCTION_ALLOWED',$type,$original,$cash,$card,$at);
                SELECT last_insert_rowid();
                """;
            q.Parameters.AddWithValue("$r", receipt);
            q.Parameters.AddWithValue("$at", at.ToString("O"));
            q.Parameters.AddWithValue("$total", total);
            q.Parameters.AddWithValue("$type", type);
            q.Parameters.AddWithValue("$original", (object?)original ?? DBNull.Value);
            q.Parameters.AddWithValue("$cash", cash);
            q.Parameters.AddWithValue("$card", card);
            saleId = Convert.ToInt64(await q.ExecuteScalarAsync());
        }

        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = """
                INSERT INTO sale_items(
                  sale_id,product_id,product_name,quantity,quantity_milli,
                  unit_price_cents,vat_rate,line_total_cents,unit)
                VALUES($sale,$product,$name,$qty,$milli,$price,$vat,$lineTotal,$unit);
                """;
            q.Parameters.AddWithValue("$sale", saleId);
            q.Parameters.AddWithValue("$product", receipt);
            q.Parameters.AddWithValue("$name", line.Name);
            q.Parameters.AddWithValue("$qty", line.quantityMilli / 1000.0);
            q.Parameters.AddWithValue("$milli", line.quantityMilli);
            q.Parameters.AddWithValue("$price", line.unitPrice);
            q.Parameters.AddWithValue("$vat", Convert.ToDouble(line.vat));
            q.Parameters.AddWithValue("$lineTotal", line.lineTotal);
            q.Parameters.AddWithValue("$unit", line.unit);
            await q.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return saleId;
    }
}
