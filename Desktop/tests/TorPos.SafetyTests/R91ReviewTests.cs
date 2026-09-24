using Microsoft.Data.Sqlite;
using TorPos.Infrastructure;

// R91: third round of the same bug family as R88/R90, found by auditing
// every remaining Build*Async report in BusinessManagementService.cs for
// the identical mistake - summing sales/sale_items without excluding
// transaction_type STORNO/RETURN. Fixed three more reports:
// BuildTurnoverSummaryAsync (UMSATZBERICHTE: Heute/Woche/Monat),
// BuildMonthlyTurnoverAsync (MONATSUMSATZ, 24-month view) and
// BuildSalesStatisticsAsync (VERKAUFSSTATISTIK, top products - both the
// standalone report and the monthly-package copy in
// BusinessManagementReports.cs). All three previously counted a BON
// STORNO/Teilretoure's amount as ADDITIONAL revenue instead of excluding
// it. Already-correct reports (GetPeriodSummaryAsync/X-/Z-Report,
// KASSENJOURNAL's intentional full listing, the GDPdU export's
// intentional full audit trail) were re-checked and left untouched.
public static class R91ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert,
        Func<Func<Task>, string, Task> reject)
    {
        var dir = Path.Combine(root, "r91-turnover-reports");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r91.db"));
        var management = new BusinessManagementService(db, new SettingsRepository(db), new AuditLogRepository(db));

        long InsertSale(SqliteConnection c, long receipt, string transactionType, long? originalSaleId, long totalCents, string productName, decimal quantity)
        {
            long saleId;
            using (var q = c.CreateCommand())
            {
                q.CommandText = """
                    INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,original_sale_id)
                    VALUES($r,$now,'CASH',$t,$t,'TEST_FIXTURE',$type,$original);
                    SELECT last_insert_rowid();
                    """;
                q.Parameters.AddWithValue("$r", receipt);
                q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
                q.Parameters.AddWithValue("$t", totalCents);
                q.Parameters.AddWithValue("$type", transactionType);
                q.Parameters.AddWithValue("$original", (object?)originalSaleId ?? DBNull.Value);
                saleId = Convert.ToInt64(q.ExecuteScalar());
            }
            using (var q = c.CreateCommand())
            {
                q.CommandText = """
                    INSERT INTO sale_items(sale_id,product_id,product_name,quantity,unit_price_cents,vat_rate,line_total_cents)
                    VALUES($sale,1,$name,$qty,$t,19,$t);
                    """;
                q.Parameters.AddWithValue("$sale", saleId);
                q.Parameters.AddWithValue("$name", productName);
                q.Parameters.AddWithValue("$qty", quantity);
                q.Parameters.AddWithValue("$t", totalCents);
                q.ExecuteNonQuery();
            }
            return saleId;
        }

        long originalSaleId;
        using (var c = db.OpenConnection())
        {
            originalSaleId = InsertSale(c, 91001, "SALE", null, 2000, "R91 Testartikel", 2);
            InsertSale(c, 91002, "STORNO", originalSaleId, 2000, "R91 Testartikel", 2);
        }

        // V-2 supersedes R91's "count only the SALE": the reversal is now
        // netted out exactly like on the X/Z (Umsatz nach Storno/Retouren),
        // instead of being ignored. A fully stornoed Bon therefore nets to 0,
        // and it is still never counted twice.
        var turnover = await management.BuildTurnoverSummaryAsync();
        var turnoverText = string.Join("\n", turnover.Lines);
        assert(
            turnoverText.Contains("HEUTE: 1 Bons | Umsatz 0,00 EUR | Bar 0,00 EUR | Karte 0,00 EUR | Storno/Retoure 20,00 EUR"),
            "R91/V-2 UMSATZBERICHTE count the SALE once and net its own STORNO out, as the Z does");

        var monthly = await management.BuildMonthlyTurnoverAsync();
        var monthlyText = string.Join("\n", monthly.Lines);
        var thisMonth = DateTime.Today.ToString("yyyy-MM");
        assert(
            monthlyText.Contains($"{thisMonth} | 1 | 0,00 EUR | 20,00 EUR"),
            "R91/V-2 MONATSUMSATZ counts the SALE once and nets its STORNO out for the current month");

        var stats = await management.BuildSalesStatisticsAsync();
        var statsText = string.Join("\n", stats.Lines);
        assert(
            !statsText.Contains("R91 Testartikel", StringComparison.Ordinal),
            "R91/V-2 VERKAUFSSTATISTIK nets the STORNO lines out, so a fully stornoed article is not shown as sold");
    }
}
