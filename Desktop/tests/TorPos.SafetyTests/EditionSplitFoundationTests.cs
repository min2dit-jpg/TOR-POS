using TorPos.App;
using TorPos.Infrastructure;

public static class EditionSplitFoundationTests
{
    public static async Task Run(Action<bool,string> assert)
    {
        var original = Environment.GetEnvironmentVariable("TOR_POS_PRODUCT_EDITION");
        try
        {
            assert(
                ProductBuild.FixedEdition is null,
                "split foundation keeps the default safety build on the legacy shared product identity");

            Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", "KIOSK");
            assert(
                AppPaths.ProductEdition == "KIOSK",
                "split foundation recognizes the fixed KIOSK product edition");
            assert(
                AppPaths.ProductDataDirectoryName() == "TOR-KIOSK",
                "TOR KIOSK receives its own AppData root");
            assert(
                AppPaths.TrialIdentityDirectory.EndsWith("TOR-KIOSK", StringComparison.Ordinal),
                "TOR KIOSK receives its own machine-wide licence/trial identity root");

            Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", "IMBISS");
            assert(
                AppPaths.ProductEdition == "IMBISS",
                "split foundation recognizes the fixed IMBISS product edition");
            assert(
                AppPaths.ProductDataDirectoryName() == "TOR-DOENER",
                "TOR DÖNER receives its own AppData root");
            assert(
                AppPaths.TrialIdentityDirectory.EndsWith("TOR-DOENER", StringComparison.Ordinal),
                "TOR DÖNER receives its own machine-wide licence/trial identity root");

            Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", "invalid");
            assert(
                AppPaths.ProductEdition is null &&
                AppPaths.ProductDataDirectoryName() == "TOR-POS-Pro",
                "unknown product identity fails back to the legacy shared R181 data root");

            var project = File.ReadAllText(
                FindRepoFile("Desktop/src/TorPos.App/TorPos.App.csproj"));
            assert(
                project.Contains("<AssemblyName Condition=\"'$(TorProductEdition)' == 'KIOSK'\">TOR-KIOSK</AssemblyName>", StringComparison.Ordinal) &&
                project.Contains("<AssemblyName Condition=\"'$(TorProductEdition)' == 'IMBISS'\">TOR-DOENER</AssemblyName>", StringComparison.Ordinal) &&
                project.Contains("TOR_KIOSK_PRODUCT", StringComparison.Ordinal) &&
                project.Contains("TOR_DOENER_PRODUCT", StringComparison.Ordinal),
                "one audited App project emits distinct TOR KIOSK and TOR DÖNER binaries");

            var program = File.ReadAllText(
                FindRepoFile("Desktop/src/TorPos.App/Program.cs"));
            var app = File.ReadAllText(
                FindRepoFile("Desktop/src/TorPos.App/App.axaml.cs"));
            assert(
                program.Contains("ProductBuild.ConfigureEnvironment()", StringComparison.Ordinal) &&
                program.Contains("ProductBuild.RunningMutexName", StringComparison.Ordinal) &&
                app.Contains("var builtEdition = ProductBuild.FixedEdition;", StringComparison.Ordinal) &&
                app.Contains("return builtEdition;", StringComparison.Ordinal),
                "split builds fix both process identity and login edition before normal application flow");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", original);
        }

        var migrationRoot = Path.Combine(
            Path.GetTempPath(),
            "tor-split-migration-" + Guid.NewGuid().ToString("N"));
        var legacy = Path.Combine(migrationRoot, "legacy");
        var target = Path.Combine(migrationRoot, "target");
        var backups = Path.Combine(migrationRoot, "backups");
        Directory.CreateDirectory(legacy);

        try
        {
            var legacyDb = Path.Combine(legacy, "torpos.db");
            await using (var c = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = legacyDb,
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
                    Pooling = false
                }.ToString()))
            {
                await c.OpenAsync();
                await using var q = c.CreateCommand();
                q.CommandText = "CREATE TABLE probe(id INTEGER PRIMARY KEY, value TEXT NOT NULL); INSERT INTO probe(value) VALUES ('R181');";
                await q.ExecuteNonQueryAsync();
            }

            File.WriteAllText(Path.Combine(legacy, "edition.permanent.lock"), "KIOSK");
            Directory.CreateDirectory(Path.Combine(legacy, "ReceiptAssets"));
            File.WriteAllText(Path.Combine(legacy, "ReceiptAssets", "logo.txt"), "legacy-logo");

            var migrated = await LegacyEditionSplitMigration.TryMigrateAsync(
                legacy, target, backups, "KIOSK");

            assert(
                migrated.State == LegacySplitMigrationState.Migrated &&
                migrated.BackupPath is not null &&
                File.Exists(migrated.BackupPath),
                "split migration creates a verified backup before copying a permanently-bound R181 installation");

            assert(
                File.Exists(Path.Combine(target, "torpos.db")) &&
                File.ReadAllText(Path.Combine(target, "ReceiptAssets", "logo.txt")) == "legacy-logo" &&
                File.Exists(Path.Combine(target, LegacyEditionSplitMigration.MarkerFileName)),
                "split migration copies database/assets and records migration provenance in the dedicated product root");

            assert(
                File.Exists(Path.Combine(legacy, "torpos.db")) &&
                File.ReadAllText(Path.Combine(legacy, "ReceiptAssets", "logo.txt")) == "legacy-logo",
                "split migration leaves the complete shared R181 source untouched for rollback");

            var repeated = await LegacyEditionSplitMigration.TryMigrateAsync(
                legacy, target, backups, "KIOSK");
            assert(
                repeated.State == LegacySplitMigrationState.TargetAlreadyInitialized,
                "split migration is idempotent and never overwrites an initialized dedicated product root");

            var wrongTarget = Path.Combine(migrationRoot, "wrong-target");
            var mismatch = await LegacyEditionSplitMigration.TryMigrateAsync(
                legacy, wrongTarget, backups, "IMBISS");
            assert(
                mismatch.State == LegacySplitMigrationState.LegacyEditionMismatch &&
                !Directory.Exists(wrongTarget),
                "a permanently KIOSK-bound shared database is never copied into TOR DÖNER");

            File.Delete(Path.Combine(legacy, "edition.permanent.lock"));
            var unprovenTarget = Path.Combine(migrationRoot, "unproven-target");
            var unproven = await LegacyEditionSplitMigration.TryMigrateAsync(
                legacy, unprovenTarget, backups, "KIOSK");
            assert(
                unproven.State == LegacySplitMigrationState.LegacyEditionUnproven &&
                !Directory.Exists(unprovenTarget),
                "temporary/test R181 edition state is not enough to auto-migrate fiscal history");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(migrationRoot))
                Directory.Delete(migrationRoot, recursive: true);
        }
    }

    private static string FindRepoFile(string relative)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName,
                relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException(relative);
    }
}
