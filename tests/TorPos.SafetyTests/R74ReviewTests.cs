using System.Diagnostics;
using Microsoft.Data.Sqlite;
using TorPos.Infrastructure;

public static class R74ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool,string> assert,
        Func<Func<Task>,string,Task> reject)
    {
        var dir =
            Path.Combine(
                root,
                "r74");

        Directory.CreateDirectory(dir);

        var db =
            await SafetyDatabase.CreateCurrentAsync(
                Path.Combine(
                    dir,
                    "r74.db"));

        assert(
            SchemaMigrationService.TargetSchemaVersion >= 5,
            "R74 keeps current business schema at V5; DB health needs no customer-data migration");

        using (var c=db.OpenConnection())
        using (var q=c.CreateCommand())
        {
            q.CommandText="""
                CREATE TABLE IF NOT EXISTS r74_health_probe(
                    id INTEGER PRIMARY KEY,
                    value TEXT NOT NULL);
                INSERT INTO r74_health_probe(id,value)
                VALUES(1,'before-health')
                ON CONFLICT(id) DO UPDATE SET value=excluded.value;
                """;

            q.ExecuteNonQuery();
        }

        var health =
            new DatabaseHealthService(db);

        var before =
            await health.GetSnapshotAsync();

        assert(
            string.Equals(
                before.JournalMode,
                "wal",
                StringComparison.OrdinalIgnoreCase),
            "R74 database health confirms WAL journal mode");

        assert(
            before.Synchronous == 2 &&
            before.SynchronousName == "FULL",
            "R74 database health confirms synchronous FULL");

        assert(
            before.BusyTimeoutMs >= 3000 &&
            before.ForeignKeysEnabled,
            "R74 database health confirms busy timeout and foreign keys");

        assert(
            before.PageCount > 0 &&
            before.PageSizeBytes >= 512 &&
            before.FreeListPages >= 0,
            "R74 database health exposes valid page and freelist metrics");

        assert(
            before.DatabaseFileBytes >= 0 &&
            before.WalFileBytes >= 0 &&
            before.ShmFileBytes >= 0,
            "R74 database health exposes non-negative DB WAL and SHM file sizes");

        assert(
            before.CheckpointBusy >= 0 &&
            before.WalLogFrames >= 0 &&
            before.WalCheckpointedFrames >= 0,
            "R74 PASSIVE WAL checkpoint returns valid busy/log/checkpointed metrics");

        assert(
            before.IntegrityHealthy &&
            string.Equals(
                before.QuickCheck,
                "ok",
                StringComparison.OrdinalIgnoreCase),
            "R74 non-destructive database health quick_check passes");

        using (var c=db.OpenConnection())
        using (var q=c.CreateCommand())
        {
            q.CommandText="""
                SELECT value
                FROM r74_health_probe
                WHERE id=1;
                """;

            assert(
                Convert.ToString(
                    q.ExecuteScalar())=="before-health",
                "R74 health probe does not modify application data");
        }

        // Deterministic SQLite busy-timeout test on the disposable safety DB.
        // Writer A keeps a write lock briefly; writer B must wait and then
        // succeed well before the configured 3000 ms timeout.
        using var first =
            db.OpenConnection();

        using (var create=first.CreateCommand())
        {
            create.CommandText="""
                CREATE TABLE IF NOT EXISTS r74_lock_probe(
                    id INTEGER PRIMARY KEY,
                    value TEXT NOT NULL);
                """;

            create.ExecuteNonQuery();
        }

        using var firstTx =
            first.BeginTransaction();

        using (var hold=first.CreateCommand())
        {
            hold.Transaction =
                (SqliteTransaction)firstTx;

            hold.CommandText="""
                INSERT INTO r74_lock_probe(id,value)
                VALUES(1,'writer-a');
                """;

            hold.ExecuteNonQuery();
        }

        var writerReady =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        var secondWriter =
            Task.Run(() =>
            {
                try
                {
                    using var second =
                        db.OpenConnection();

                    using var q =
                        second.CreateCommand();

                    q.CommandText="""
                        INSERT INTO r74_lock_probe(id,value)
                        VALUES(2,'writer-b');
                        """;

                    writerReady.TrySetResult(true);

                    var sw =
                        Stopwatch.StartNew();

                    q.ExecuteNonQuery();

                    sw.Stop();

                    return (
                        Success:true,
                        Milliseconds:
                            sw.Elapsed.TotalMilliseconds);
                }
                catch
                {
                    return (
                        Success:false,
                        Milliseconds:0d);
                }
            });

        await writerReady.Task;

        await Task.Delay(300);

        firstTx.Commit();

        var writerResult =
            await secondWriter;

        assert(
            writerResult.Success &&
            writerResult.Milliseconds >= 150 &&
            writerResult.Milliseconds < 3000,
            "R74 competing writer waits for a short lock and succeeds within busy_timeout");

        using (var c=db.OpenConnection())
        using (var q=c.CreateCommand())
        {
            q.CommandText="""
                SELECT COUNT(*)
                FROM r74_lock_probe;
                """;

            assert(
                Convert.ToInt32(
                    q.ExecuteScalar())==2,
                "R74 WAL concurrency test preserves both committed writers");
        }

        var after =
            await health.GetSnapshotAsync();

        assert(
            after.CorePragmasHealthy &&
            after.IntegrityHealthy,
            "R74 database remains healthy after contention and PASSIVE checkpoint");
    }
}
