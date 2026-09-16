using Microsoft.Data.Sqlite;

namespace TorPos.Infrastructure;

public sealed record DatabaseHealthSnapshot(
    string JournalMode,
    int Synchronous,
    string SynchronousName,
    int BusyTimeoutMs,
    bool ForeignKeysEnabled,
    int WalAutoCheckpointPages,
    long PageCount,
    long PageSizeBytes,
    long FreeListPages,
    long DatabaseFileBytes,
    long WalFileBytes,
    long ShmFileBytes,
    int CheckpointBusy,
    int WalLogFrames,
    int WalCheckpointedFrames,
    string QuickCheck)
{
    public double FreeListPercent =>
        PageCount <= 0
            ? 0d
            : 100d * FreeListPages / PageCount;

    public bool CorePragmasHealthy =>
        string.Equals(
            JournalMode,
            "wal",
            StringComparison.OrdinalIgnoreCase) &&
        Synchronous == 2 &&
        BusyTimeoutMs >= 3000 &&
        ForeignKeysEnabled;

    public bool IntegrityHealthy =>
        string.Equals(
            QuickCheck,
            "ok",
            StringComparison.OrdinalIgnoreCase);

    public bool IsHealthy =>
        CorePragmasHealthy &&
        IntegrityHealthy &&
        CheckpointBusy == 0;
}

/// <summary>
/// Non-destructive SQLite runtime health probe.
///
/// The production database is never VACUUMed or TRUNCATE-checkpointed here.
/// PASSIVE checkpoint may copy eligible WAL frames into the DB but does not
/// wait for readers and does not truncate the WAL file.
/// </summary>
public sealed class DatabaseHealthService
{
    private readonly SqliteDatabase _db;

    public DatabaseHealthService(
        SqliteDatabase db) =>
        _db = db;

    // Deliberately NOT routed through IoQueue: every step here is read-only
    // (PRAGMA reads, quick_check, a PASSIVE checkpoint that never blocks
    // writers/readers) on its own connection. SQLite's WAL mode already
    // allows this safely alongside concurrent checkout writes; funneling it
    // through the single shared write-serialization queue instead made a
    // full quick_check scan able to stall every register's checkout for as
    // long as the scan takes.
    public async Task<DatabaseHealthSnapshot> GetSnapshotAsync(
        CancellationToken ct = default)
    {
        {
            ct.ThrowIfCancellationRequested();

            using var c =
                _db.OpenConnection();

            var journalMode =
                await ScalarTextAsync(
                    c,
                    "PRAGMA journal_mode;",
                    ct);

            var synchronous =
                await ScalarIntAsync(
                    c,
                    "PRAGMA synchronous;",
                    ct);

            var busyTimeout =
                await ScalarIntAsync(
                    c,
                    "PRAGMA busy_timeout;",
                    ct);

            var foreignKeys =
                await ScalarIntAsync(
                    c,
                    "PRAGMA foreign_keys;",
                    ct) == 1;

            var walAutoCheckpoint =
                await ScalarIntAsync(
                    c,
                    "PRAGMA wal_autocheckpoint;",
                    ct);

            var pageCount =
                await ScalarLongAsync(
                    c,
                    "PRAGMA page_count;",
                    ct);

            var pageSize =
                await ScalarLongAsync(
                    c,
                    "PRAGMA page_size;",
                    ct);

            var freeList =
                await ScalarLongAsync(
                    c,
                    "PRAGMA freelist_count;",
                    ct);

            var quickCheck =
                await ScalarTextAsync(
                    c,
                    "PRAGMA quick_check;",
                    ct);

            var checkpointBusy = 0;
            var walLogFrames = 0;
            var walCheckpointedFrames = 0;

            using (var checkpoint =
                   c.CreateCommand())
            {
                checkpoint.CommandText =
                    "PRAGMA wal_checkpoint(PASSIVE);";

                using var reader =
                    await checkpoint
                        .ExecuteReaderAsync(ct);

                if (!await reader.ReadAsync(ct))
                {
                    throw new InvalidOperationException(
                        "SQLite PASSIVE-Checkpoint lieferte kein Ergebnis.");
                }

                checkpointBusy =
                    reader.GetInt32(0);

                walLogFrames =
                    reader.GetInt32(1);

                walCheckpointedFrames =
                    reader.GetInt32(2);
            }

            var dbBytes =
                FileSize(
                    _db.DatabasePath);

            var walBytes =
                FileSize(
                    _db.DatabasePath + "-wal");

            var shmBytes =
                FileSize(
                    _db.DatabasePath + "-shm");

            return new DatabaseHealthSnapshot(
                JournalMode: journalMode,
                Synchronous: synchronous,
                SynchronousName:
                    SynchronousName(synchronous),
                BusyTimeoutMs: busyTimeout,
                ForeignKeysEnabled: foreignKeys,
                WalAutoCheckpointPages: walAutoCheckpoint,
                PageCount: pageCount,
                PageSizeBytes: pageSize,
                FreeListPages: freeList,
                DatabaseFileBytes: dbBytes,
                WalFileBytes: walBytes,
                ShmFileBytes: shmBytes,
                CheckpointBusy: checkpointBusy,
                WalLogFrames: walLogFrames,
                WalCheckpointedFrames:
                    walCheckpointedFrames,
                QuickCheck: quickCheck);
        }
    }

    private static async Task<string> ScalarTextAsync(
        SqliteConnection c,
        string sql,
        CancellationToken ct)
    {
        using var q =
            c.CreateCommand();

        q.CommandText = sql;

        return Convert.ToString(
                   await q.ExecuteScalarAsync(ct))
               ?? "";
    }

    private static async Task<int> ScalarIntAsync(
        SqliteConnection c,
        string sql,
        CancellationToken ct) =>
        checked((int)
            await ScalarLongAsync(
                c,
                sql,
                ct));

    private static async Task<long> ScalarLongAsync(
        SqliteConnection c,
        string sql,
        CancellationToken ct)
    {
        using var q =
            c.CreateCommand();

        q.CommandText = sql;

        return Convert.ToInt64(
            await q.ExecuteScalarAsync(ct));
    }

    private static long FileSize(
        string path)
    {
        try
        {
            return File.Exists(path)
                ? new FileInfo(path).Length
                : 0L;
        }
        catch
        {
            return 0L;
        }
    }

    private static string SynchronousName(
        int value) =>
        value switch
        {
            0 => "OFF",
            1 => "NORMAL",
            2 => "FULL",
            3 => "EXTRA",
            _ => value.ToString()
        };
}
