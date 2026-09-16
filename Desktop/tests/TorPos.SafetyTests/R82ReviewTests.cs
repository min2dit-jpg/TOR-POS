using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

// R82: Teilretoure (partial Retoure) - returns specific quantities of
// specific lines from an already-completed sale, as its own new immutable
// sale (transaction_type='RETURN'). Same family of rules as R79's BON
// STORNO (BAR only, never touches the original sale, FiscalRelease-gated),
// but per-line instead of per-Bon, so this also has to track "how much of
// this exact original line has already been returned" across possibly
// several partial returns without ever over-returning it.
public static class R82ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool,string> assert,
        Func<Func<Task>,string,Task> reject)
    {
        var dir = Path.Combine(root, "r82-partial-return");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r82.db"));
        var sales = new SaleRepository(db);
        var journal = new CheckoutJournal(db);

        using (var c = db.OpenConnection())
        {
            using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM pragma_table_info('sale_items') WHERE name='original_sale_item_id';";
            assert(Convert.ToInt64(q.ExecuteScalar()) == 1,
                "R82 schema migration V10 adds original_sale_item_id to sale_items");
        }

        // A RETURN is a Beleg with negative amounts (DSFinV-K 4.2.5); R130 moved
        // the processData to the official Anhang I form.
        var returnSale = new Sale
        {
            Id = 99,
            ReceiptNumber = 601,
            TransactionType = "RETURN",
            OriginalSaleId = 1,
            OriginalReceiptNumber = 555,
            CreatedAt = DateTimeOffset.Now,
            PaymentMethod = PaymentMethod.Cash,
            TotalCents = 500,
            Lines = new[] { new CartLine { ProductName = "A", Quantity = 1, UnitPriceCents = 500, VatRate = 19m } }
        };
        var returnProcessData = System.Text.Encoding.UTF8.GetString(FiscalProcessData.BuildKassenbeleg(returnSale));
        // R121 CORRECTION: this originally required "AVBelegabbruch^", which
        // means an ABORTED process - a receipt broken off before completion,
        // with no money moved. A Retoure is the opposite: a completed
        // transaction handing money back, modelled as a normal Beleg with
        // negative positions. The reference to the original receipt is what
        // makes it traceable, and that part was always right.
        assert(
            returnProcessData == "Beleg^-5.00_0.00_0.00_0.00_0.00^-5.00:Bar",
            $"R82/R121/R130 a partial RETURN is signed as a normal Beleg with negative amounts, not as an aborted receipt (actual: {returnProcessData})");
        assert(
            !returnProcessData.Contains("AVBelegabbruch"),
            "R121 a Retoure is never signed as AVBelegabbruch - that marker means the process was aborted, not reversed");

        var receiptCounter = 90000L;
        long twoLineSaleId = 0;
        long firstItemId = 0, secondItemId = 0;

        async Task<long> CommitTwoLineSaleAsync()
        {
            var snapshot = new CheckoutSnapshot(
                Guid.NewGuid().ToString("N"),
                new[]
                {
                    new CartLine { ProductName = "R82 Artikel A", Quantity = 3, UnitPriceCents = 500, VatRate = 19m },
                    new CartLine { ProductName = "R82 Artikel B", Quantity = 2, UnitPriceCents = 300, VatRate = 7m },
                },
                0, PaymentMethod.Cash, "tester", null);
            await journal.BeginAsync(snapshot);

            var receipt = ++receiptCounter;
            long saleId;
            await using (var c = db.OpenConnection())
            {
                await using var tx = await c.BeginTransactionAsync();
                await using (var q = c.CreateCommand())
                {
                    q.Transaction = (SqliteTransaction)tx;
                    q.CommandText = "INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type) VALUES($r,$now,'CASH',2100,2100,'TEST_FIXTURE','SALE'); SELECT last_insert_rowid();";
                    q.Parameters.AddWithValue("$r", receipt);
                    q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
                    saleId = Convert.ToInt64(await q.ExecuteScalarAsync());
                }
                await using (var q = c.CreateCommand())
                {
                    q.Transaction = (SqliteTransaction)tx;
                    q.CommandText = "INSERT INTO sale_items(sale_id,product_id,product_name,quantity,unit_price_cents,vat_rate,line_total_cents) VALUES($sale,1,'R82 Artikel A',3,500,19,1500); SELECT last_insert_rowid();";
                    q.Parameters.AddWithValue("$sale", saleId);
                    firstItemId = Convert.ToInt64(await q.ExecuteScalarAsync());
                }
                await using (var q = c.CreateCommand())
                {
                    q.Transaction = (SqliteTransaction)tx;
                    q.CommandText = "INSERT INTO sale_items(sale_id,product_id,product_name,quantity,unit_price_cents,vat_rate,line_total_cents) VALUES($sale,2,'R82 Artikel B',2,300,7,600); SELECT last_insert_rowid();";
                    q.Parameters.AddWithValue("$sale", saleId);
                    secondItemId = Convert.ToInt64(await q.ExecuteScalarAsync());
                }
                await using (var q = c.CreateCommand())
                {
                    q.Transaction = (SqliteTransaction)tx;
                    q.CommandText = "UPDATE checkout_operations SET state='COMMITTED',sale_id=$sale WHERE id=$id;";
                    q.Parameters.AddWithValue("$sale", saleId);
                    q.Parameters.AddWithValue("$id", snapshot.OperationId);
                    await q.ExecuteNonQueryAsync();
                }
                await tx.CommitAsync();
            }

            await sales.CommitAsync(snapshot); // retry path; just confirms the fixture is consistent
            return saleId;
        }

        twoLineSaleId = await CommitTwoLineSaleAsync();

        await reject(
            () => sales.RecordReturnAsync(twoLineSaleId, Array.Empty<ReturnLineRequest>(), "tester", "Testgrund"),
            "R82 an empty line selection is refused before any database work happens");

        await reject(
            () => sales.RecordReturnAsync(twoLineSaleId, new[] { new ReturnLineRequest(firstItemId, 0m) }, "tester", "Testgrund"),
            "R82 a zero return quantity is refused");

        await reject(
            () => sales.RecordReturnAsync(twoLineSaleId, new[] { new ReturnLineRequest(firstItemId, -1m) }, "tester", "Testgrund"),
            "R82 a negative return quantity is refused");

        await reject(
            () => sales.RecordReturnAsync(twoLineSaleId, new[] { new ReturnLineRequest(firstItemId, 1m), new ReturnLineRequest(firstItemId, 1m) }, "tester", "Testgrund"),
            "R82 the same line cannot appear twice in one Retoure request");

        await reject(
            () => sales.RecordReturnAsync(twoLineSaleId, new[] { new ReturnLineRequest(999_999_999L, 1m) }, "tester", "Testgrund"),
            "R82 a sale_item id that isn't on this Bon is refused");

        await reject(
            () => sales.RecordReturnAsync(999_999_999L, new[] { new ReturnLineRequest(firstItemId, 1m) }, "tester", "Testgrund"),
            "R82 a non-existent original sale is refused");

        // A card-paid sale: same restriction as full BON STORNO.
        long cardSaleId;
        await using (var c = db.OpenConnection())
        {
            await using var tx = await c.BeginTransactionAsync();
            long id;
            await using (var q = c.CreateCommand())
            {
                q.Transaction = (SqliteTransaction)tx;
                q.CommandText = "INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type) VALUES($r,$now,'CARD',500,500,'TEST_FIXTURE','SALE'); SELECT last_insert_rowid();";
                q.Parameters.AddWithValue("$r", ++receiptCounter);
                q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
                id = Convert.ToInt64(await q.ExecuteScalarAsync());
            }
            long itemId;
            await using (var q = c.CreateCommand())
            {
                q.Transaction = (SqliteTransaction)tx;
                q.CommandText = "INSERT INTO sale_items(sale_id,product_id,product_name,quantity,unit_price_cents,vat_rate,line_total_cents) VALUES($sale,1,'X',1,500,19,500); SELECT last_insert_rowid();";
                q.Parameters.AddWithValue("$sale", id);
                itemId = Convert.ToInt64(await q.ExecuteScalarAsync());
            }
            await tx.CommitAsync();
            cardSaleId = id;

            await reject(
                () => sales.RecordReturnAsync(cardSaleId, new[] { new ReturnLineRequest(itemId, 1m) }, "tester", "Testgrund"),
                "R82 a KARTE sale is refused for Teilretoure, same as BON STORNO");
        }

        // A RETURN row can never itself be the target of another Retoure.
        long returnRowId, returnRowItemId;
        await using (var c = db.OpenConnection())
        {
            await using var tx = await c.BeginTransactionAsync();
            await using (var q = c.CreateCommand())
            {
                q.Transaction = (SqliteTransaction)tx;
                q.CommandText = "INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,original_sale_id) VALUES($r,$now,'CASH',500,500,'TEST_FIXTURE','RETURN',$original); SELECT last_insert_rowid();";
                q.Parameters.AddWithValue("$r", ++receiptCounter);
                q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
                q.Parameters.AddWithValue("$original", twoLineSaleId);
                returnRowId = Convert.ToInt64(await q.ExecuteScalarAsync());
            }
            await using (var q = c.CreateCommand())
            {
                q.Transaction = (SqliteTransaction)tx;
                q.CommandText = "INSERT INTO sale_items(sale_id,product_id,product_name,quantity,unit_price_cents,vat_rate,line_total_cents,original_sale_item_id) VALUES($sale,1,'R82 Artikel A',1,500,19,500,$original); SELECT last_insert_rowid();";
                q.Parameters.AddWithValue("$sale", returnRowId);
                q.Parameters.AddWithValue("$original", firstItemId);
                returnRowItemId = Convert.ToInt64(await q.ExecuteScalarAsync());
            }
            await tx.CommitAsync();
        }

        await reject(
            () => sales.RecordReturnAsync(returnRowId, new[] { new ReturnLineRequest(returnRowItemId, 1m) }, "tester", "Testgrund"),
            "R82 a RETURN booking can never itself be the target of another Teilretoure");

        // The cumulative "already returned per original line" query the real
        // RecordReturnAsync uses (verified directly here, since a genuinely
        // eligible request only reaches the same production gate every other
        // fiscal booking reaches - see the R79/R80 pattern for why).
        using (var c = db.OpenConnection())
        {
            using var q = c.CreateCommand();
            q.CommandText = """
                SELECT COALESCE(SUM(i.quantity),0)
                FROM sale_items i
                JOIN sales s ON s.id=i.sale_id
                WHERE i.original_sale_item_id=$item AND s.transaction_type='RETURN';
                """;
            q.Parameters.AddWithValue("$item", firstItemId);
            var alreadyReturned = Convert.ToDecimal(q.ExecuteScalar());
            assert(alreadyReturned == 1m,
                "R82 the already-returned-per-line query correctly sums only RETURN rows referencing that exact original line");
        }

        // A genuinely eligible request (two-line sale, valid line, valid quantity,
        // never returned before) still reaches - and is stopped only by - the same
        // FiscalRelease.RequireProduction() gate as every other real fiscal booking.
        try
        {
            await sales.RecordReturnAsync(twoLineSaleId, new[] { new ReturnLineRequest(secondItemId, 1m) }, "tester", "Testgrund");
            assert(false, "R82 an eligible Teilretoure must still be blocked by the production release gate");
        }
        catch (InvalidOperationException ex)
        {
            assert(ex.Message.Contains("Produktivbuchung gesperrt"),
                "R82 an eligible Teilretoure reaches exactly the same production-release gate as CommitAsync/RecordStornoAsync, not a different check");
        }

        var untouched = await sales.GetByIdAsync(twoLineSaleId);
        assert(
            untouched!.TransactionType == "SALE" && untouched.TotalCents == 2100 && untouched.Lines.Count == 2,
            "R82 every refused or gate-blocked Teilretoure attempt leaves the targeted sale completely unchanged");
    }
}
