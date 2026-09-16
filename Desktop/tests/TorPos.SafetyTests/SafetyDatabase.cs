using TorPos.Infrastructure;

internal static class SafetyDatabase
{
    public static async Task<SqliteDatabase> CreateCurrentAsync(
        string databasePath,
        CancellationToken ct = default)
    {
        var db = new SqliteDatabase(databasePath);
        await EnsureCurrentAsync(db, ct);
        return db;
    }

    public static async Task EnsureCurrentAsync(
        SqliteDatabase db,
        CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(db.DatabasePath);

        if (string.IsNullOrWhiteSpace(directory))
            directory = Path.GetTempPath();

        var backupFolder = Path.Combine(
            directory,
            ".safety-migration-backups");

        var backup = new DatabaseBackupService(db);
        var migrations = new SchemaMigrationService(
            db,
            backup,
            backupFolder);

        await migrations.InitializeDatabaseAsync(ct);
    }
}
