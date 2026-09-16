using Microsoft.Data.Sqlite;
using TorPos.Infrastructure;

// R92: fourth instance of the same bug family as R88/R90/R91, found on the
// same sweep for missing transaction_type handling. GetExpectedCashCentsAsync
// feeds the Kassensturz "erwarteter Bestand" a cashier compares against the
// physically counted drawer. It summed every CASH sales row for today
// regardless of transaction_type, so a cash BON STORNO/Teilretoure counted
// as MORE cash coming in - when real cash was actually handed back to the
// customer. A perfectly correct drawer would have looked "short" by exactly
// the reversed amount. Fixed to subtract STORNO/RETURN instead of adding it.
public static class R92ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert,
        Func<Func<Task>, string, Task> reject)
    {
        var dir = Path.Combine(root, "r92-expected-cash");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r92.db"));
        var cashMovements = new CashMovementRepository(db, new AuditLogRepository(db));

        long InsertSale(SqliteConnection c, long receipt, string transactionType, long? originalSaleId, long totalCents, string paymentMethod)
        {
            using var q = c.CreateCommand();
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
            return Convert.ToInt64(q.ExecuteScalar());
        }

        long originalSaleId;
        using (var c = db.OpenConnection())
        {
            // 5000 cents cash sale, fully storno'd (10000 cents card sale must
            // never affect the cash figure at all), plus a separate 3000-cent
            // cash sale partially retourned for 1000 cents.
            originalSaleId = InsertSale(c, 92001, "SALE", null, 5000, "CASH");
            InsertSale(c, 92002, "STORNO", originalSaleId, 5000, "CASH");
            InsertSale(c, 92003, "SALE", null, 10000, "CARD");
            var secondSaleId = InsertSale(c, 92004, "SALE", null, 3000, "CASH");
            InsertSale(c, 92005, "RETURN", secondSaleId, 1000, "CASH");
        }

        // Expected: opening 20000 + (5000 - 5000) fully-reversed cash sale +
        // (3000 - 1000) partially-returned cash sale + 0 (card sale ignored) = 22000.
        var expected = await cashMovements.GetExpectedCashCentsAsync(20000);
        assert(
            expected == 22000,
            "R92 expected cash correctly subtracts a cash STORNO/Teilretoure instead of adding it on top of the sale it reverses");

        var emptyDir = Path.Combine(root, "r92-expected-cash-empty");
        Directory.CreateDirectory(emptyDir);
        var emptyDb = await SafetyDatabase.CreateCurrentAsync(Path.Combine(emptyDir, "empty.db"));
        var emptyCashMovements = new CashMovementRepository(emptyDb, new AuditLogRepository(emptyDb));
        var emptyExpected = await emptyCashMovements.GetExpectedCashCentsAsync(5000);
        assert(
            emptyExpected == 5000,
            "R92 a day with no sales at all leaves the opening balance untouched");
    }
}
