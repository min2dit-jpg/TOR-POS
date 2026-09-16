using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

// R79: BON STORNO - the fiscal counter-booking (Gegenbuchung) for an
// already-completed sale. RecordStornoAsync never updates or deletes the
// original sale (sales are DB-immutable, see R78); it only ever inserts a
// new sale with transaction_type='STORNO'. It goes through the exact same
// TorPos.Core.FiscalRelease.RequireProduction() build gate as a normal
// sale commit, which stays disabled in every SafetyTests fixture - so
// these tests can only exercise the validation that happens BEFORE that
// gate, plus prove a fully eligible request still reaches (and is stopped
// by) that same gate rather than a weaker one.
public static class R79ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool,string> assert,
        Func<Func<Task>,string,Task> reject)
    {
        var dir = Path.Combine(root, "r79-bon-storno");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r79.db"));
        var sales = new SaleRepository(db);

        var receiptCounter = 70000L;
        async Task<long> InsertSaleAsync(string paymentMethod, string transactionType = "SALE", long? originalSaleId = null, long totalCents = 1500)
        {
            var receipt = ++receiptCounter;
            await using var c = db.OpenConnection();
            await using var tx = await c.BeginTransactionAsync();
            long saleId;
            await using (var q = c.CreateCommand())
            {
                q.Transaction = (SqliteTransaction)tx;
                q.CommandText = """
                    INSERT INTO sales(
                        receipt_number,created_at,payment_method,
                        subtotal_cents,total_cents,fiscal_status,
                        transaction_type,original_sale_id)
                    VALUES($r,$now,$p,$t,$t,'TEST_FIXTURE',$type,$original);
                    SELECT last_insert_rowid();
                    """;
                q.Parameters.AddWithValue("$r", receipt);
                q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
                q.Parameters.AddWithValue("$p", paymentMethod);
                q.Parameters.AddWithValue("$t", totalCents);
                q.Parameters.AddWithValue("$type", transactionType);
                q.Parameters.AddWithValue("$original", (object?)originalSaleId ?? DBNull.Value);
                saleId = Convert.ToInt64(await q.ExecuteScalarAsync());
            }
            await using (var q = c.CreateCommand())
            {
                q.Transaction = (SqliteTransaction)tx;
                q.CommandText = """
                    INSERT INTO sale_items(
                      sale_id,product_id,product_name,quantity,
                      unit_price_cents,vat_rate,line_total_cents)
                    VALUES($sale,1,'R79 Testartikel',1,$t,19,$t);
                    """;
                q.Parameters.AddWithValue("$sale", saleId);
                q.Parameters.AddWithValue("$t", totalCents);
                await q.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            return saleId;
        }

        // A) A card-paid sale cannot be storno'd at all right now (no ZVT reversal exists yet).
        var cardSaleId = await InsertSaleAsync("CARD");
        await reject(
            () => sales.RecordStornoAsync(cardSaleId, "tester", "Testgrund"),
            "R79 a KARTE sale is refused for BON STORNO because no card-terminal reversal exists yet");

        // B) Storno of a non-existent sale is refused with a clear error, not a crash.
        await reject(
            () => sales.RecordStornoAsync(999_999_999L, "tester", "Testgrund"),
            "R79 storno of a non-existent sale is refused");

        // C) A STORNO row itself can never be storno'd again (transaction_type check).
        var baseSaleForChain = await InsertSaleAsync("CASH");
        var alreadyStornoRow = await InsertSaleAsync("CASH", transactionType: "STORNO", originalSaleId: baseSaleForChain);
        await reject(
            () => sales.RecordStornoAsync(alreadyStornoRow, "tester", "Testgrund"),
            "R79 a STORNO booking can never itself be the target of another BON STORNO");

        // D) A sale that already has an open STORNO booking cannot be storno'd twice.
        await reject(
            () => sales.RecordStornoAsync(baseSaleForChain, "tester", "Testgrund"),
            "R79 a sale that was already storno'd is refused a second BON STORNO");

        // E) A genuinely eligible CASH sale (never storno'd) passes every validation and
        // is stopped only by the same FiscalRelease.RequireProduction() gate every real
        // sale commit goes through - proving BON STORNO is not a weaker/separate bypass.
        var eligibleCashSaleId = await InsertSaleAsync("CASH");
        try
        {
            await sales.RecordStornoAsync(eligibleCashSaleId, "tester", "Testgrund");
            assert(false, "R79 an eligible BON STORNO must still be blocked by the production release gate");
        }
        catch (InvalidOperationException ex)
        {
            assert(ex.Message.Contains("Produktivbuchung gesperrt"),
                "R79 an eligible BON STORNO reaches exactly the same production-release gate as CommitAsync, not a different check");
        }

        // F) None of the above attempts touched the original sales they targeted -
        // the immutability guarantee holds even when RecordStornoAsync itself fails.
        var untouchedCard = await sales.GetByIdAsync(cardSaleId);
        var untouchedBase = await sales.GetByIdAsync(baseSaleForChain);
        assert(
            untouchedCard!.TransactionType == "SALE" && untouchedCard.TotalCents == 1500 &&
            untouchedBase!.TransactionType == "SALE" && untouchedBase.TotalCents == 1500,
            "R79 every refused or gate-blocked BON STORNO attempt leaves the targeted sale completely unchanged");

        // G) Z-report VAT fix: BusinessManagementService's per-rate breakdown used to only
        // ever sum transaction_type='SALE' rows, so a STORNO's own VAT was never subtracted
        // from it - the gross total already netted Storno/Retouren out, but the MwSt.
        // summary above it would have kept overstating collected VAT. Isolated fixture DB
        // so this is exact, independent of every sale inserted earlier in this file.
        var vatFixDir = Path.Combine(dir, "vat-fix");
        Directory.CreateDirectory(vatFixDir);
        var vatFixDb = await SafetyDatabase.CreateCurrentAsync(Path.Combine(vatFixDir, "vat-fix.db"));
        var vatFixSettings = new SettingsRepository(vatFixDb);
        var vatFixAudit = new AuditLogRepository(vatFixDb);
        var management = new BusinessManagementService(vatFixDb, vatFixSettings, vatFixAudit);

        // Subtotal 1200 (pre-discount line total) with a 100-cent manual discount
        // mirrors exactly how RecordStornoAsync now populates a Storno row: its
        // own discount_cents equals the original's, not 0 - otherwise the tax
        // breakdown (which nets subtotal-from-items minus sales.discount_cents)
        // would over-subtract VAT for any discounted sale that gets storno'd.
        async Task<long> InsertVatFixSaleAsync(long receiptNumber, string transactionType, long? originalSaleId)
        {
            await using var c = vatFixDb.OpenConnection();
            await using var tx = await c.BeginTransactionAsync();
            long saleId;
            await using (var q = c.CreateCommand())
            {
                q.Transaction = (SqliteTransaction)tx;
                q.CommandText = """
                    INSERT INTO sales(
                        receipt_number,created_at,payment_method,
                        subtotal_cents,discount_cents,total_cents,fiscal_status,
                        transaction_type,original_sale_id)
                    VALUES($r,$now,'CASH',1200,100,1100,'TEST_FIXTURE',$type,$original);
                    SELECT last_insert_rowid();
                    """;
                q.Parameters.AddWithValue("$r", receiptNumber);
                q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
                q.Parameters.AddWithValue("$type", transactionType);
                q.Parameters.AddWithValue("$original", (object?)originalSaleId ?? DBNull.Value);
                saleId = Convert.ToInt64(await q.ExecuteScalarAsync());
            }
            await using (var q = c.CreateCommand())
            {
                q.Transaction = (SqliteTransaction)tx;
                q.CommandText = """
                    INSERT INTO sale_items(sale_id,product_id,product_name,quantity,unit_price_cents,vat_rate,line_total_cents)
                    VALUES($sale,1,'VAT Fix Artikel',1,1200,19,1200);
                    """;
                q.Parameters.AddWithValue("$sale", saleId);
                await q.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            return saleId;
        }

        var vatSaleId = await InsertVatFixSaleAsync(60001, "SALE", null);
        await InsertVatFixSaleAsync(60002, "STORNO", vatSaleId);

        var xReport = await management.BuildXReportAsync();
        var taxLine = xReport.Lines.FirstOrDefault(l => l.Contains("19") && l.Contains("Brutto"));
        assert(
            taxLine is not null && taxLine.Contains("Brutto 0") && taxLine.Contains("Steuer 0"),
            "R79 a STORNO's VAT is subtracted from the same per-rate Z-report totals its original SALE contributed to, discount included");
    }
}
