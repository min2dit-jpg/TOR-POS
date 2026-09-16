using System.Globalization;
using Microsoft.Data.Sqlite;
using TorPos.Infrastructure;

// R88: BuildStornoReportAsync ("STORNOBERICHT") only ever queried
// audit_log WHERE event_type LIKE '%STORNO%' - which silently missed BOTH
// real reversal kinds: SOFORT_STORNO lives in a different table entirely
// (pos_action_log), and R82's Teilretoure logs event_type 'SALE_RETURN',
// never matched by that LIKE pattern. The report has been silently
// incomplete since R82 shipped. Rebuilt to pull each kind from its actual
// source of truth (pos_action_log for SOFORT STORNO, sales.transaction_type
// for BON STORNO/TEILRETOURE) instead of pattern-matching free text.
public static class R88ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert,
        Func<Func<Task>, string, Task> reject)
    {
        var dir = Path.Combine(root, "r88-storno-journal");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r88.db"));
        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        var management = new BusinessManagementService(db, settings, audit);

        long InsertSale(SqliteConnection c, long receipt, string transactionType, long? originalSaleId, long totalCents, string operatorName)
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
            originalSaleId = InsertSale(c, 88001, "SALE", null, 2000, "kassierer-a");
            InsertSale(c, 88002, "STORNO", originalSaleId, 2000, "admin-a");
            InsertSale(c, 88003, "RETURN", originalSaleId, 500, "admin-b");

            using var q = c.CreateCommand();
            q.CommandText = """
                INSERT INTO pos_action_log(
                    created_at,action_id,phase,actor,register_id,operation_id,action_type,reason,
                    entity_type,entity_id,before_total_cents,after_total_cents,amount_cents,details,
                    prev_hash,entry_hash)
                VALUES(
                    $at,$actionId,'APPLIED','kassierer-a','R1','OP1','SOFORT_STORNO','Testgrund',
                    'CART_LINE','1',1500,1000,500,'R88 Testartikel entfernt',
                    '',$entryHash);
                """;
            q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
            q.Parameters.AddWithValue("$actionId", Guid.NewGuid().ToString("N"));
            q.Parameters.AddWithValue("$entryHash", Guid.NewGuid().ToString("N"));
            q.ExecuteNonQuery();
        }

        // Execute the report inside the I/O worker's culture scope: the queue
        // has its own ExecutionContext, so changing only the caller is insufficient.
        static Task<ReportDocument> ReportInCultureAsync(BusinessManagementService service, string culture) =>
            IoQueue.RunAsync(async () =>
            {
                var previous = CultureInfo.CurrentCulture;
                try
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                    return await service.BuildStornoReportAsync();
                }
                finally
                {
                    CultureInfo.CurrentCulture = previous;
                }
            });

        var report = await ReportInCultureAsync(management, "en-US");
        var text = string.Join("\n", report.Lines);

        assert(
            report.Title == "STORNO- UND RETOURENJOURNAL",
            "R88 the report title reflects that it covers both Storno and Retoure, not just Storno");

        assert(
            text.Contains("kassierer-a") && text.Contains("R88 Testartikel entfernt") && text.Contains("5,00 EUR"),
            "R88 a SOFORT STORNO entry from pos_action_log appears in the report with its amount; row: " +
            report.Lines.FirstOrDefault(line => line.Contains("R88 Testartikel entfernt")));

        assert(
            text.Contains("088002") && text.Contains("088001") && text.Contains("BON STORNO") && text.Contains("admin-a"),
            "R88 a BON STORNO row shows both its own receipt number and the original receipt it reverses");

        assert(
            text.Contains("088003") && text.Contains("TEILRETOURE") && text.Contains("admin-b"),
            "R88 a Teilretoure (transaction_type='RETURN') now actually appears - this is exactly what the old LIKE '%STORNO%' filter silently dropped");

        assert(
            text.Contains("Summe SOFORT STORNO: 1 · 5,00 EUR"),
            "R88 the SOFORT STORNO section totals count and amount correctly");

        assert(
            text.Contains("Summe BON STORNO/TEILRETOURE: 2 · 25,00 EUR"),
            "R88 the BON STORNO/TEILRETOURE section sums both reversal kinds together (20,00 + 5,00 EUR)");

        // An empty database must render clean placeholder sections, not throw or omit them.
        var emptyDir = Path.Combine(root, "r88-storno-journal-empty");
        Directory.CreateDirectory(emptyDir);
        var emptyDb = await SafetyDatabase.CreateCurrentAsync(Path.Combine(emptyDir, "empty.db"));
        var emptyManagement = new BusinessManagementService(emptyDb, new SettingsRepository(emptyDb), new AuditLogRepository(emptyDb));
        var emptyReport = await ReportInCultureAsync(emptyManagement, "en-US");
        var emptyText = string.Join("\n", emptyReport.Lines);
        assert(
            emptyText.Contains("Summe SOFORT STORNO: 0 · 0,00 EUR") &&
            emptyText.Contains("Summe BON STORNO/TEILRETOURE: 0 · 0,00 EUR"),
            "R88 an empty database still produces a well-formed report with zero totals instead of an empty/broken document");

        foreach (var culture in new[] { "de-DE", "tr-TR" })
        {
            var localized = await ReportInCultureAsync(management, culture);
            var localizedEmpty = await ReportInCultureAsync(emptyManagement, culture);
            assert(
                localized.Lines.SequenceEqual(report.Lines) &&
                localizedEmpty.Lines.SequenceEqual(emptyReport.Lines),
                $"R88 German report amounts and totals remain identical under {culture} and en-US");
        }
    }
}
