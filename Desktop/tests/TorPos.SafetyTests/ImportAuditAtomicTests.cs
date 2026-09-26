using Microsoft.Data.Sqlite;
using TorPos.Infrastructure;

// The article imports committed the articles first and wrote their audit rows
// afterwards: a failing audit write reported an error although the articles
// were already saved, and a retry of a row without EAN and Artikelnummer
// added the same article a second time. Articles and audit now commit together.
public static class ImportAuditAtomicTests
{
    private const string FailAudit = """
        CREATE TRIGGER safety_fail_import_audit BEFORE INSERT ON audit_log
        WHEN NEW.event_type LIKE 'ARTICLE_IMPORT%'
        BEGIN SELECT RAISE(ABORT,'audit write failed'); END;
        """;

    public static async Task Run(string root, Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "import-audit-atomic");
        Directory.CreateDirectory(dir);

        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "csv.db"));
        var management = new BusinessManagementService(db, new SettingsRepository(db), new AuditLogRepository(db));
        var csv = Path.Combine(dir, "import.csv");
        await File.WriteAllTextAsync(csv,
            "GRUPPE;WARENGRUPPE;ARTIKEL;ARTIKELNUMMER;EAN;PREIS_CENT;UST\n" +
            "Audit Laden;Audit Backwaren;Audit Brezel;;;150;7\n");
        await Execute(db, FailAudit);
        var failed = await Fails(() => management.ImportArticlesCsvAsync(csv, "tester"));
        var afterFailure = await Scalar(db, "SELECT COUNT(*) FROM products WHERE name='Audit Brezel'");
        await Execute(db, "DROP TRIGGER safety_fail_import_audit;");
        await management.ImportArticlesCsvAsync(csv, "tester");
        assert(
            failed && afterFailure == 0 &&
            await Scalar(db, "SELECT COUNT(*) FROM products WHERE name='Audit Brezel'") == 1 &&
            await Scalar(db, "SELECT COUNT(*) FROM audit_log WHERE event_type='ARTICLE_IMPORT_CSV'") == 1,
            "Import audit: a CSV import whose audit row fails changes no article, and the retry adds the row without EAN/Artikelnummer exactly once");

        var target = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "target.db"));
        var targetManagement = new BusinessManagementService(target, new SettingsRepository(target), new AuditLogRepository(target));
        var source = Path.Combine(dir, "csv.db");
        await Execute(target, FailAudit);
        var dbFailed = await Fails(() => targetManagement.ImportArticlesFromDatabaseAsync(source, "tester"));
        var dbAfterFailure = await Scalar(target, "SELECT COUNT(*) FROM products WHERE name='Audit Brezel'");
        await Execute(target, "DROP TRIGGER safety_fail_import_audit;");
        await targetManagement.ImportArticlesFromDatabaseAsync(source, "tester");
        assert(
            dbFailed && dbAfterFailure == 0 &&
            await Scalar(target, "SELECT COUNT(*) FROM products WHERE name='Audit Brezel'") == 1 &&
            await Scalar(target, "SELECT COUNT(*) FROM audit_log WHERE event_type='ARTICLE_IMPORT_DATABASE'") == 1,
            "Import audit: a database import whose audit row fails changes no article; the retry imports once, with its audit row");
    }

    private static async Task<bool> Fails(Func<Task> action)
    {
        try { await action(); return false; }
        catch (SqliteException) { return true; }
    }

    private static async Task Execute(SqliteDatabase db, string sql)
    {
        await using var c = db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = sql;
        await q.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(SqliteDatabase db, string sql)
    {
        await using var c = db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = sql;
        return Convert.ToInt64(await q.ExecuteScalarAsync());
    }
}
