using Microsoft.Data.Sqlite;
using TorPos.Infrastructure;

public static class R69ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool,string> assert,
        Func<Func<Task>,string,Task> reject)
    {
        var dir = Path.Combine(root, "r69");
        Directory.CreateDirectory(dir);

        // Fresh DB: initialize/version without unnecessary migration backup.
        var freshPath = Path.Combine(dir, "fresh.db");
        var freshDb = new SqliteDatabase(freshPath);
        var freshBackup = new DatabaseBackupService(freshDb);
        var freshMigrationDir = Path.Combine(dir, "fresh-migrations");
        var freshMigrator = new SchemaMigrationService(
            freshDb,
            freshBackup,
            freshMigrationDir);

        var freshResult = await freshMigrator.InitializeDatabaseAsync();
        assert(
            freshResult.FromVersion == 0 &&
            freshResult.ToVersion == SchemaMigrationService.TargetSchemaVersion &&
            freshResult.BackupPath is null,
            "R69 fresh database reaches current schema without unnecessary pre-migration backup");

        var freshStatus = await freshMigrator.GetStatusAsync();
        assert(
            freshStatus.IsCurrent &&
            freshStatus.HistoryCount == SchemaMigrationService.TargetSchemaVersion,
            "R69 schema_version and migration history are persisted");

        // Legacy customer DB: build old-compatible schema first, then adopt R69.
        var legacyPath = Path.Combine(dir, "legacy.db");
        var legacyDb = new SqliteDatabase(legacyPath);
        await legacyDb.InitializeAsync();

        using (var c = legacyDb.OpenConnection())
        {
            using var q = c.CreateCommand();
            q.CommandText = "INSERT OR REPLACE INTO app_settings(key,value) VALUES('r69.marker','KEEP-ME');";
            q.ExecuteNonQuery();
        }

        var legacyBackup = new DatabaseBackupService(legacyDb);
        var legacyMigrationDir = Path.Combine(dir, "legacy-migrations");
        var legacyMigrator = new SchemaMigrationService(
            legacyDb,
            legacyBackup,
            legacyMigrationDir);

        var legacyResult = await legacyMigrator.InitializeDatabaseAsync();

        assert(
            !string.IsNullOrWhiteSpace(legacyResult.BackupPath) &&
            File.Exists(legacyResult.BackupPath),
            "R69 existing unversioned customer database receives verified pre-migration backup");

        using (var backupConnection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = legacyResult.BackupPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString()))
        {
            backupConnection.Open();

            using var check = backupConnection.CreateCommand();
            check.CommandText = "PRAGMA quick_check;";
            assert(
                string.Equals(
                    Convert.ToString(check.ExecuteScalar()),
                    "ok",
                    StringComparison.OrdinalIgnoreCase),
                "R69 pre-migration backup passes SQLite quick_check");

            using var marker = backupConnection.CreateCommand();
            marker.CommandText = "SELECT value FROM app_settings WHERE key='r69.marker';";
            assert(
                Convert.ToString(marker.ExecuteScalar()) == "KEEP-ME",
                "R69 pre-migration backup preserves legacy customer data before schema adoption");
        }

        var backupCountBefore = Directory.GetFiles(
            legacyMigrationDir,
            "*.db").Length;

        var secondResult = await legacyMigrator.InitializeDatabaseAsync();

        var backupCountAfter = Directory.GetFiles(
            legacyMigrationDir,
            "*.db").Length;

        assert(
            secondResult.AppliedVersions.Count == 0 &&
            secondResult.BackupPath is null &&
            backupCountBefore == backupCountAfter,
            "R69 current schema restart is idempotent and does not create another migration backup");

        // Fail closed on DB created by a newer application/schema.
        var newerPath = Path.Combine(dir, "newer.db");
        var newerDb = new SqliteDatabase(newerPath);
        await newerDb.InitializeAsync();

        using (var c = newerDb.OpenConnection())
        {
            using var q = c.CreateCommand();
            q.CommandText = """
                CREATE TABLE schema_version(
                    singleton_id INTEGER PRIMARY KEY CHECK(singleton_id=1),
                    version INTEGER NOT NULL,
                    updated_at TEXT NOT NULL,
                    app_version TEXT NOT NULL
                );
                CREATE TABLE schema_migrations(
                    version INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    applied_at TEXT NOT NULL,
                    app_version TEXT NOT NULL,
                    pre_migration_backup TEXT NOT NULL DEFAULT ''
                );
                INSERT INTO schema_version(singleton_id,version,updated_at,app_version)
                VALUES(1,999,'2026-09-10T00:00:00Z','future');
                """;
            q.ExecuteNonQuery();
        }

        var newerMigrator = new SchemaMigrationService(
            newerDb,
            new DatabaseBackupService(newerDb),
            Path.Combine(dir, "newer-backups"));

        await reject(
            () => newerMigrator.InitializeDatabaseAsync(),
            "R69 refuses database downgrade when schema is newer than application");

        // Fail closed when safety backup destination is unusable.
        var blockedPath = Path.Combine(dir, "blocked.db");
        var blockedDb = new SqliteDatabase(blockedPath);
        await blockedDb.InitializeAsync();

        var invalidBackupTarget = Path.Combine(dir, "not-a-directory");
        File.WriteAllText(invalidBackupTarget, "blocked");

        var blockedMigrator = new SchemaMigrationService(
            blockedDb,
            new DatabaseBackupService(blockedDb),
            invalidBackupTarget);

        await reject(
            () => blockedMigrator.InitializeDatabaseAsync(),
            "R69 migration is blocked when pre-migration backup cannot be created");
    }
}
