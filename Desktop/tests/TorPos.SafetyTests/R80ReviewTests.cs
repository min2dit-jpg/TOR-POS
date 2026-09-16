using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

// R80: fixes found on a dedicated review pass over R77-R79 (kitchen routing,
// TSE sale signing, BON STORNO) after they were first built and tested.
// Each check here targets one specific bug found during that review:
// - FiscalProcessData didn't distinguish a STORNO from an ordinary sale of
//   the same items and never referenced the original Beleg.
// - A STORNO could theoretically race past its own "already storno'd?"
//   check under concurrent access; a DB-level unique index now makes a
//   double storno impossible regardless of timing.
public static class R80ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool,string> assert,
        Func<Func<Task>,string,Task> reject)
    {
        var dir = Path.Combine(root, "r80-review-fixes");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r80.db"));

        using (var c = db.OpenConnection())
        {
            using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ux_sales_one_storno_per_original';";
            assert(Convert.ToInt64(q.ExecuteScalar()) == 1,
                "R80 schema migration V9 creates the one-storno-per-original unique index");
        }

        var ordinarySale = new Sale
        {
            Id = 1,
            ReceiptNumber = 501,
            TransactionType = "SALE",
            CreatedAt = new DateTimeOffset(2026, 2, 1, 10, 0, 0, TimeSpan.FromHours(1)),
            PaymentMethod = PaymentMethod.Cash,
            TotalCents = 1000,
            Lines = new[] { new CartLine { ProductName = "A", Quantity = 1, UnitPriceCents = 1000, VatRate = 19m } }
        };
        var ordinaryProcessData = System.Text.Encoding.UTF8.GetString(FiscalProcessData.BuildKassenbeleg(ordinarySale));
        assert(
            ordinaryProcessData == "Beleg^10.00_0.00_0.00_0.00_0.00^10.00:Bar",
            $"R80/R130 an ordinary SALE is a Beleg with positive amounts (actual: {ordinaryProcessData})");

        var stornoSale = new Sale
        {
            Id = 2,
            ReceiptNumber = 502,
            TransactionType = "STORNO",
            OriginalSaleId = 1,
            OriginalReceiptNumber = 501,
            CreatedAt = new DateTimeOffset(2026, 2, 1, 10, 5, 0, TimeSpan.FromHours(1)),
            PaymentMethod = PaymentMethod.Cash,
            TotalCents = 1000,
            Lines = ordinarySale.Lines
        };
        var stornoProcessData = System.Text.Encoding.UTF8.GetString(FiscalProcessData.BuildKassenbeleg(stornoSale));
        // R130 CORRECTION: this used to require "AVBelegstorno" plus a
        // "Referenz-Beleg-Nr" tag. DSFinV-K Anhang B and I rule AVBelegstorno out
        // for a TSE-secured till, and Anhang I has no reference field: a Storno
        // is a Beleg with reversed signs, and its link to the original receipt
        // is DSFinV-K Bon_Referenzen (built from OriginalSaleId, checked below).
        // The reversed signs are what keep it from looking like a duplicate sale.
        assert(
            stornoProcessData == "Beleg^-10.00_0.00_0.00_0.00_0.00^-10.00:Bar",
            $"R80/R130 a STORNO is a Beleg with reversed signs, never AVBelegstorno, and cannot be mistaken for a duplicate sale (actual: {stornoProcessData})");

        // LoadSaleAsync (exercised via ISaleRepository.GetByIdAsync) must populate
        // OriginalReceiptNumber from the self-join, not just OriginalSaleId.
        var sales = new SaleRepository(db);
        long originalId, stornoId;
        await using (var c = db.OpenConnection())
        {
            await using var tx = await c.BeginTransactionAsync();
            await using (var q = c.CreateCommand())
            {
                q.Transaction = (SqliteTransaction)tx;
                q.CommandText = "INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type) VALUES(80001,$now,'CASH',1000,1000,'TEST_FIXTURE','SALE'); SELECT last_insert_rowid();";
                q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
                originalId = Convert.ToInt64(await q.ExecuteScalarAsync());
            }
            await using (var q = c.CreateCommand())
            {
                q.Transaction = (SqliteTransaction)tx;
                q.CommandText = "INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,original_sale_id) VALUES(80002,$now,'CASH',1000,1000,'TEST_FIXTURE','STORNO',$original); SELECT last_insert_rowid();";
                q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
                q.Parameters.AddWithValue("$original", originalId);
                stornoId = Convert.ToInt64(await q.ExecuteScalarAsync());
            }
            await tx.CommitAsync();
        }

        var reloadedStorno = await sales.GetByIdAsync(stornoId);
        assert(
            reloadedStorno!.OriginalSaleId == originalId && reloadedStorno.OriginalReceiptNumber == 80001,
            "R80 a reloaded STORNO sale carries its original sale's receipt number, not just its internal id");

        var reloadedOriginal = await sales.GetByIdAsync(originalId);
        assert(
            reloadedOriginal!.OriginalReceiptNumber is null,
            "R80 an ordinary sale never has an OriginalReceiptNumber");

        // The unique index is the authoritative guard: a second raw STORNO row for
        // the same original must be refused by SQLite itself, independent of
        // RecordStornoAsync's own (non-atomic-by-itself) pre-check.
        await reject(
            async () =>
            {
                await using var c = db.OpenConnection();
                await using var q = c.CreateCommand();
                q.CommandText = "INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,original_sale_id) VALUES(80003,$now,'CASH',1000,1000,'TEST_FIXTURE','STORNO',$original);";
                q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
                q.Parameters.AddWithValue("$original", originalId);
                await q.ExecuteNonQueryAsync();
            },
            "R80 the database itself refuses a second STORNO row for the same original sale, closing the race window a plain COUNT-then-INSERT check cannot");
    }
}
