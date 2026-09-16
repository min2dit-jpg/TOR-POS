using System.Linq;
using TorPos.Infrastructure;

// R93: found while auditing the last remaining sales-join site outside the
// R88-R92 sweep. OrderWorkflowService.ListAsync's SQL compared
// s.payment_method against the literal strings 'Cash'/'Card' (mixed case),
// but every real sale stores payment_method as 'CASH'/'CARD' (uppercase -
// CommitAsync writes paymentMethod.ToString().ToUpperInvariant()). SQLite's
// default TEXT comparison is case-sensitive, so that WHEN clause could
// never match - a cashed IMBISS order always fell through to the generic
// "Bezahlt · siehe Bon" label on the order board instead of showing "Bar
// bezahlt"/"Karte bezahlt". Fixed by matching the actual stored casing.
public static class R93ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert,
        Func<Func<Task>, string, Task> reject)
    {
        var dir = Path.Combine(root, "r93-order-payment-label");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r93.db"));
        var workflow = new OrderWorkflowService(db);

        long InsertSale(string paymentMethod, long receipt)
        {
            using var c = db.OpenConnection();
            using var q = c.CreateCommand();
            q.CommandText = """
                INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type)
                VALUES($r,$now,$pm,1000,1000,'TEST_FIXTURE','SALE');
                SELECT last_insert_rowid();
                """;
            q.Parameters.AddWithValue("$r", receipt);
            q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
            q.Parameters.AddWithValue("$pm", paymentMethod);
            return Convert.ToInt64(q.ExecuteScalar());
        }

        void InsertCashedOrder(long parkNumber, long cashedSaleId)
        {
            using var c = db.OpenConnection();
            using var q = c.CreateCommand();
            q.CommandText = """
                INSERT INTO parked_receipts(park_number,pickup_number,created_at,updated_at,subtotal_cents,total_cents,status,is_training,cashed_sale_id,cashed_at)
                VALUES($park,0,$now,$now,1000,1000,'CASHED',0,$sale,$now);
                """;
            q.Parameters.AddWithValue("$park", parkNumber);
            q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
            q.Parameters.AddWithValue("$sale", cashedSaleId);
            q.ExecuteNonQuery();
        }

        var cashSaleId = InsertSale("CASH", 93001);
        InsertCashedOrder(93101, cashSaleId);
        var cardSaleId = InsertSale("CARD", 93002);
        InsertCashedOrder(93102, cardSaleId);

        var overview = await workflow.ListAsync(training: false, includeDelivered: true);

        var cashOrder = overview.Single(x => x.ParkNumber == 93101);
        assert(
            cashOrder.Payment == "Bar bezahlt",
            "R93 a cash-paid order shows 'Bar bezahlt', not the generic fallback - payment_method comparison now matches the real stored 'CASH' casing");

        var cardOrder = overview.Single(x => x.ParkNumber == 93102);
        assert(
            cardOrder.Payment == "Karte bezahlt",
            "R93 a card-paid order shows 'Karte bezahlt', not the generic fallback");
    }
}
