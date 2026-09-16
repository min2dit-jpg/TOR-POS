using Microsoft.Data.Sqlite;
using TorPos.Infrastructure;

// R90: two related bugs found while re-auditing report queries after R88.
// (1) BuildOperatorSettlementAsync ("BEDIENERABRECHNUNG") summed EVERY sales
// row regardless of transaction_type, so a BON STORNO/Teilretoure's
// total_cents was silently ADDED into the operator's Umsatz/Bar/Karte
// instead of being excluded - the opposite of what a cash-reconciliation
// report needs. (2) RecordStornoAsync/RecordReturnAsync never wrote a
// sale_operators row for the reversal sale itself, so even if the report
// tried to attribute a Storno/Retoure to whoever processed it, that
// information didn't exist at the sale-row level (only in audit_log free
// text). This suite cannot call RecordStornoAsync/RecordReturnAsync
// end-to-end (FiscalRelease.RequireProduction() always throws first in a
// test build, same limitation R79/R82's own tests document) so fix (2) is
// verified by direct inspection plus this file's own manually-seeded rows
// standing in for what those methods now write; fix (1) is verified fully
// against the report query itself.
public static class R90ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert,
        Func<Func<Task>, string, Task> reject)
    {
        var dir = Path.Combine(root, "r90-operator-settlement");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r90.db"));
        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        var management = new BusinessManagementService(db, settings, audit);

        long InsertSale(SqliteConnection c, long receipt, string transactionType, long? originalSaleId, long totalCents, string paymentMethod, string operatorName)
        {
            long saleId;
            using (var q = c.CreateCommand())
            {
                q.CommandText = """
                    INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,original_sale_id)
                    VALUES($r,$now,$pm,$t,$t,'TEST_FIXTURE',$type,$original);
                    SELECT last_insert_rowid();
                    """;
                q.Parameters.AddWithValue("$r", receipt);
                q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
                q.Parameters.AddWithValue("$pm", paymentMethod);
                q.Parameters.AddWithValue("$t", totalCents);
                q.Parameters.AddWithValue("$type", transactionType);
                q.Parameters.AddWithValue("$original", (object?)originalSaleId ?? DBNull.Value);
                saleId = Convert.ToInt64(q.ExecuteScalar());
            }
            using (var q = c.CreateCommand())
            {
                q.CommandText = "INSERT INTO sale_operators(sale_id,operator_name) VALUES($sale,$op);";
                q.Parameters.AddWithValue("$sale", saleId);
                q.Parameters.AddWithValue("$op", operatorName);
                q.ExecuteNonQuery();
            }
            return saleId;
        }

        long originalSaleId;
        using (var c = db.OpenConnection())
        {
            // kassierer-a rings up 1000 cents cash.
            originalSaleId = InsertSale(c, 90001, "SALE", null, 1000, "CASH", "kassierer-a");
            // admin-x fully storno's it - this must NOT be added to kassierer-a's
            // (or anyone's) Umsatz, only shown separately.
            InsertSale(c, 90002, "STORNO", originalSaleId, 1000, "CASH", "admin-x");
            // kassierer-b rings up 500 cents card.
            InsertSale(c, 90003, "SALE", null, 500, "CARD", "kassierer-b");
            // admin-x also processes a partial Teilretoure against it.
            InsertSale(c, 90004, "RETURN", 90003, 200, "CASH", "admin-x");
        }

        var report = await management.BuildOperatorSettlementAsync();
        var text = string.Join("\n", report.Lines);

        assert(
            text.Contains("kassierer-a | 1 | 10,00 EUR | 10,00 EUR | 0,00 EUR"),
            "R90 an operator's Umsatz/Bar reflects only their SALE, not inflated by the STORNO reversing it");

        assert(
            text.Contains("kassierer-b | 1 | 5,00 EUR | 0,00 EUR | 5,00 EUR"),
            "R90 a card sale's own operator settlement is unaffected by a later Teilretoure against it");

        assert(
            !text.Contains("20,00 EUR") && !text.Contains("kassierer-a | 2"),
            "R90 the STORNO's 10,00 EUR is nowhere added on top of kassierer-a's own 10,00 EUR sale");

        assert(
            text.Contains("STORNO/RETOURE NACH BEARBEITER") &&
            text.Contains("admin-x | 2 | 12,00 EUR"),
            "R90 the reversals are shown separately, correctly attributed to admin-x who actually processed them (10,00 + 2,00 EUR) - not silently dropped and not attributed to the original cashier");

        var emptyDir = Path.Combine(root, "r90-operator-settlement-empty");
        Directory.CreateDirectory(emptyDir);
        var emptyDb = await SafetyDatabase.CreateCurrentAsync(Path.Combine(emptyDir, "empty.db"));
        var emptyManagement = new BusinessManagementService(emptyDb, new SettingsRepository(emptyDb), new AuditLogRepository(emptyDb));
        var emptyReport = await emptyManagement.BuildOperatorSettlementAsync();
        var emptyText = string.Join("\n", emptyReport.Lines);
        assert(
            emptyText.Split("(keine)").Length - 1 == 2,
            "R90 a day with no sales and no reversals still renders both sections cleanly instead of an empty/broken report");
    }
}
