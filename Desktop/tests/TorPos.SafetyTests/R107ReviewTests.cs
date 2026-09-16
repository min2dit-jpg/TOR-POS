using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

// R107: two Kritik findings from the user's own second source review,
// both confirmed against the actual code before fixing:
//
// 1. MainWindow.OnBonStornoClick/OnPartialReturnClick called the real card
//    terminal refund BEFORE any "already reversed" validation - the
//    validation lived only inside RecordStornoAsync/RecordReturnAsync,
//    reached AFTER the money already moved. Fixed with a new
//    ISaleRepository.CheckReversalAllowedAsync pre-check, called before
//    either handler ever touches the terminal.
//
// 2. RecordStornoAsync/RecordReturnAsync never checked for the OTHER
//    reversal type's existence against the same original sale: a 100 EUR
//    sale partially returned by 30 EUR, then fully storno'd, refunded the
//    FULL 100 EUR again on top of the 30 EUR already returned - 130 EUR
//    total for a 100 EUR sale. Fixed by widening RecordStornoAsync's
//    existing-check to include prior RETURN rows, and adding a new
//    STORNO-existence check to RecordReturnAsync.
//
// Both new checks sit BEFORE FiscalRelease.RequireProduction() in each
// method (unlike the method's own successful completion, which cannot be
// exercised in this harness at all). This suite uses RejectMessage, not
// plain Reject, specifically to prove that - directly answering the user's
// own test-methodology critique that a reject()-based test can pass by
// hitting an unrelated earlier gate instead of the one it claims to verify.
public static class R107ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert,
        Func<Func<Task>, string, Task> reject,
        Func<Func<Task>, string, string, Task> rejectMessage)
    {
        var dir = Path.Combine(root, "r107-double-refund-fixes");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r107.db"));
        var sales = new SaleRepository(db);
        var receiptCounter = 107000L;

        long InsertRawSale(long totalCents, out long itemId)
        {
            using var c = db.OpenConnection();
            using var tx = c.BeginTransaction();
            long saleId;
            using (var q = c.CreateCommand())
            {
                q.Transaction = (SqliteTransaction)tx;
                q.CommandText = """
                    INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,cash_portion_cents,card_portion_cents)
                    VALUES($r,$now,'CASH',$t,$t,'TEST_FIXTURE','SALE',$t,0);
                    SELECT last_insert_rowid();
                    """;
                q.Parameters.AddWithValue("$r", ++receiptCounter);
                q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
                q.Parameters.AddWithValue("$t", totalCents);
                saleId = Convert.ToInt64(q.ExecuteScalar());
            }
            long localItemId;
            using (var q = c.CreateCommand())
            {
                q.Transaction = (SqliteTransaction)tx;
                q.CommandText = "INSERT INTO sale_items(sale_id,product_id,product_name,quantity,unit_price_cents,vat_rate,line_total_cents) VALUES($sale,1,'R107 Artikel',1,$t,19,$t); SELECT last_insert_rowid();";
                q.Parameters.AddWithValue("$sale", saleId);
                q.Parameters.AddWithValue("$t", totalCents);
                localItemId = Convert.ToInt64(q.ExecuteScalar());
            }
            tx.Commit();
            itemId = localItemId;
            return saleId;
        }

        void InsertReversal(long originalSaleId, string transactionType, long totalCents)
        {
            using var c = db.OpenConnection();
            using var q = c.CreateCommand();
            q.CommandText = """
                INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,original_sale_id,cash_portion_cents,card_portion_cents)
                VALUES($r,$now,'CASH',$t,$t,'TEST_FIXTURE',$type,$orig,$t,0);
                """;
            q.Parameters.AddWithValue("$r", ++receiptCounter);
            q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
            q.Parameters.AddWithValue("$t", totalCents);
            q.Parameters.AddWithValue("$type", transactionType);
            q.Parameters.AddWithValue("$orig", originalSaleId);
            q.ExecuteNonQuery();
        }

        // 1) CheckReversalAllowedAsync: a fresh sale with no reversal at all
        // may proceed, for both a full BON STORNO and a Teilretoure.
        var freshSaleId = InsertRawSale(10000, out _);
        assert(
            await sales.CheckReversalAllowedAsync(freshSaleId, forFullStorno: true) is null,
            "R107 CheckReversalAllowedAsync allows BON STORNO against a sale with no prior reversal");
        assert(
            await sales.CheckReversalAllowedAsync(freshSaleId, forFullStorno: false) is null,
            "R107 CheckReversalAllowedAsync allows Teilretoure against a sale with no prior reversal");

        // 2) A prior STORNO always blocks further reversal of either kind -
        // the sale is already fully refunded, nothing is left to reverse.
        var stornodSaleId = InsertRawSale(10000, out _);
        InsertReversal(stornodSaleId, "STORNO", 10000);
        var stornoBlockFull = await sales.CheckReversalAllowedAsync(stornodSaleId, forFullStorno: true);
        var stornoBlockPartial = await sales.CheckReversalAllowedAsync(stornodSaleId, forFullStorno: false);
        assert(
            stornoBlockFull is not null && stornoBlockFull.Contains("bereits vollständig storniert") &&
            stornoBlockPartial is not null && stornoBlockPartial.Contains("bereits vollständig storniert"),
            "R107 CheckReversalAllowedAsync blocks BOTH BON STORNO and Teilretoure once a STORNO already exists");

        // 3) A prior partial RETURN blocks a further full BON STORNO (the
        // exact double-refund the user found: a 100 EUR sale 30 EUR
        // returned, then fully storno'd for the full 100 EUR on top) but
        // does NOT block a further Teilretoure - multiple partial returns
        // against the same not-yet-fully-storno'd original remain normal.
        var partiallyReturnedSaleId = InsertRawSale(10000, out _);
        InsertReversal(partiallyReturnedSaleId, "RETURN", 3000);
        var returnBlocksFullStorno = await sales.CheckReversalAllowedAsync(partiallyReturnedSaleId, forFullStorno: true);
        var returnAllowsMoreReturn = await sales.CheckReversalAllowedAsync(partiallyReturnedSaleId, forFullStorno: false);
        assert(
            returnBlocksFullStorno is not null && returnBlocksFullStorno.Contains("Teilretoure") &&
            returnAllowsMoreReturn is null,
            "R107 CheckReversalAllowedAsync blocks a full BON STORNO after a Teilretoure already exists, but still allows another Teilretoure");

        // 4) The same double-refund gap, but through RecordStornoAsync's own
        // internal (authoritative, transactional) gate rather than the
        // pre-check - this is what actually stops the write even if a
        // caller ever skipped CheckReversalAllowedAsync. Verified by
        // MESSAGE, not just "threw something", specifically because this
        // check and FiscalRelease.RequireProduction() are BOTH reachable
        // from RecordStornoAsync - a message-blind reject() here could
        // silently start passing for the wrong reason if the checks were
        // ever reordered.
        var returnedThenStornoTarget = InsertRawSale(10000, out _);
        InsertReversal(returnedThenStornoTarget, "RETURN", 3000);
        await rejectMessage(
            () => sales.RecordStornoAsync(returnedThenStornoTarget, "tester", "Testgrund"),
            "bereits storniert oder teilweise retourniert",
            "R107 RecordStornoAsync refuses a full BON STORNO against an already-Teilretoure'd original (the 130 EUR-on-a-100-EUR-sale gap), and fails via ITS OWN check message, not FiscalRelease");

        // 5) Mirror check on RecordReturnAsync's side: a Teilretoure against
        // an already fully storno'd original must be refused too, again
        // verified by its own distinct message.
        var stornodThenReturnTarget = InsertRawSale(10000, out var stornodThenReturnItemId);
        InsertReversal(stornodThenReturnTarget, "STORNO", 10000);
        await rejectMessage(
            () => sales.RecordReturnAsync(stornodThenReturnTarget, new[] { new ReturnLineRequest(stornodThenReturnItemId, 1m) }, "tester", "Testgrund"),
            "bereits vollständig storniert",
            "R107 RecordReturnAsync refuses a Teilretoure against an already fully-storno'd original, and fails via ITS OWN check message, not FiscalRelease");

        // 6) Contrast case: a plain sale with NO prior reversal at all still
        // only ever reaches FiscalRelease.RequireProduction() (the one gate
        // every real booking hits, unrelated to this fix) - proves checks 4
        // and 5 are not just matching on a generic substring that ANY
        // exception in this codebase happens to contain.
        var plainSaleId = InsertRawSale(10000, out var plainItemId);
        await rejectMessage(
            () => sales.RecordStornoAsync(plainSaleId, "tester", "Testgrund"),
            "Produktivbuchung gesperrt",
            "R107 a plain sale's BON STORNO (no prior reversal) hits only the FiscalRelease gate, never the new double-refund check");
        await rejectMessage(
            () => sales.RecordReturnAsync(plainSaleId, new[] { new ReturnLineRequest(plainItemId, 1m) }, "tester", "Testgrund"),
            "Produktivbuchung gesperrt",
            "R107 a plain sale's Teilretoure (no prior reversal) hits only the FiscalRelease gate, never the new double-refund check");
    }
}
