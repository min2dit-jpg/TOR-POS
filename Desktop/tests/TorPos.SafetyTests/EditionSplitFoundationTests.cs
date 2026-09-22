using TorPos.App;
using TorPos.Infrastructure;

public static class EditionSplitFoundationTests
{
    public static async Task Run(Action<bool,string> assert)
    {
        var original = Environment.GetEnvironmentVariable("TOR_POS_PRODUCT_EDITION");
        try
        {
            assert(ProductBuild.FixedEdition is null, "split foundation keeps the default safety build on the legacy shared product identity");
            Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", "KIOSK");
            assert(AppPaths.ProductEdition == "KIOSK", "split foundation recognizes the fixed KIOSK product edition");
            assert(AppPaths.ProductDataDirectoryName() == "TOR-Einzelhandel", "TOR Einzelhandel receives its own AppData root");
            assert(AppPaths.TrialIdentityDirectory.EndsWith("TOR-Einzelhandel", StringComparison.Ordinal), "TOR Einzelhandel receives its own machine-wide licence/trial identity root");
            Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", "IMBISS");
            assert(AppPaths.ProductEdition == "IMBISS", "split foundation recognizes the fixed IMBISS product edition");
            assert(AppPaths.ProductDataDirectoryName() == "TOR-Gastro", "TOR Gastro receives its own AppData root");
            assert(AppPaths.TrialIdentityDirectory.EndsWith("TOR-Gastro", StringComparison.Ordinal), "TOR Gastro receives its own machine-wide licence/trial identity root");
            Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", "invalid");
            assert(AppPaths.ProductEdition is null && AppPaths.ProductDataDirectoryName() == "TOR-POS-Pro", "unknown product identity fails back to the legacy shared R181 data root");

            Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", "KIOSK");
            var kioskDemoRoot = AppPaths.TrialIdentityDirectory;
            Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", "IMBISS");
            var doenerDemoRoot = AppPaths.TrialIdentityDirectory;
            Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", null);
            var sharedDemoRoot = AppPaths.TrialIdentityDirectory;
            var machineRoot = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            assert(kioskDemoRoot != doenerDemoRoot && kioskDemoRoot != sharedDemoRoot && doenerDemoRoot != sharedDemoRoot && kioskDemoRoot.StartsWith(machineRoot, StringComparison.Ordinal) && doenerDemoRoot.StartsWith(machineRoot, StringComparison.Ordinal), "each product owns a separate machine-wide demo identity, so the 7-day demo is granted once per PC and per product");

            // The shared build must not inherit a product identity from its environment:
            // that would move its data root and machine-wide demo identity into a split product.
            Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", "IMBISS");
            ProductBuild.ConfigureEnvironment();
            assert(AppPaths.ProductEdition is null && AppPaths.ProductDataDirectoryName() == "TOR-POS-Pro", "the shared build pins its own product identity instead of inheriting one from the environment");

            var project = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/TorPos.App.csproj"));
            assert(project.Contains("<AssemblyName Condition=\"'$(TorProductEdition)' == 'KIOSK'\">TOR-Einzelhandel</AssemblyName>", StringComparison.Ordinal) && project.Contains("<AssemblyName Condition=\"'$(TorProductEdition)' == 'IMBISS'\">TOR-Gastro</AssemblyName>", StringComparison.Ordinal) && project.Contains("TOR_EINZELHANDEL_PRODUCT", StringComparison.Ordinal) && project.Contains("TOR_GASTRO_PRODUCT", StringComparison.Ordinal), "one audited App project emits distinct TOR Einzelhandel and TOR Gastro binaries");
            var program = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/Program.cs"));
            var app = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/App.axaml.cs"));
            // R182: avares URIs are keyed by assembly name, and a dedicated build renames
            // the assembly to TOR-Einzelhandel / TOR-Gastro. A hardcoded avares://TorPos.App/ URI
            // therefore resolves in the shared build and throws FileNotFoundException while
            // the dedicated product loads its login window - it cannot start at all.
            var appDirectory = Path.GetDirectoryName(FindRepoFile("Desktop/src/TorPos.App/Program.cs"))!;
            var sharedAssemblyAssetUris = Directory
                .EnumerateFiles(appDirectory, "*", SearchOption.AllDirectories)
                .Where(f => (f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
                    && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .Where(f => File.ReadAllText(f).Contains("avares://TorPos.App/", StringComparison.Ordinal))
                .ToArray();
            assert(sharedAssemblyAssetUris.Length == 0, "no asset URI hardcodes the shared assembly name, which a renamed TOR Einzelhandel / TOR Gastro build cannot resolve");

            // The customer-facing names may change (a TOR Restaurant product is planned);
            // the stored edition codes may not, because databases, licences, Cloud payloads
            // and edition.permanent.lock files on customer machines carry KIOSK / IMBISS.
            var productBuild = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/ProductBuild.cs"));
            assert(productBuild.Contains("ProductName = \"TOR Einzelhandel\"", StringComparison.Ordinal) && productBuild.Contains("ProductName = \"TOR Gastro\"", StringComparison.Ordinal) && productBuild.Contains("FixedEdition = \"KIOSK\"", StringComparison.Ordinal) && productBuild.Contains("FixedEdition = \"IMBISS\"", StringComparison.Ordinal), "the products are named TOR Einzelhandel and TOR Gastro while their stored edition codes stay KIOSK and IMBISS");

            var editionGuard = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/InstallationEdition.cs"));
            assert(editionGuard.Contains("var productEditionValue = ProductBuild.FixedEdition;", StringComparison.Ordinal) && editionGuard.Contains("productEditionValue is { } productEdition", StringComparison.Ordinal) && editionGuard.Contains("?? Environment.GetEnvironmentVariable(\"TOR_POS_EDITION\")", StringComparison.Ordinal), "a dedicated build refuses any edition that does not match its compiled product identity");
            assert(program.Contains("ProductBuild.ConfigureEnvironment()", StringComparison.Ordinal) && program.Contains("ProductBuild.RunningMutexName", StringComparison.Ordinal) && app.Contains("var builtEdition = ProductBuild.FixedEdition;", StringComparison.Ordinal) && app.Contains("return builtEdition;", StringComparison.Ordinal), "split builds fix both process identity and login edition before normal application flow");
        }
        finally { Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", original); }

        var installer = File.ReadAllText(FindRepoFile("Desktop/TOR-POS-Pro-Setup.iss"));
        var einzelhandelInstaller = File.ReadAllText(FindRepoFile("Desktop/TOR-Einzelhandel-Setup.iss"));
        var gastroInstaller = File.ReadAllText(FindRepoFile("Desktop/TOR-Gastro-Setup.iss"));
        var splitPublish = File.ReadAllText(FindRepoFile("Desktop/BUILD-SPLIT-EDITIONS.ps1"));
        var workflow = File.ReadAllText(FindRepoFile(".github/workflows/tor-pos-ci.yml"));
        assert(installer.Contains("AppId={#MyAppId}", StringComparison.Ordinal) && installer.Contains("DefaultDirName={autopf}\\{#MyDefaultDirName}", StringComparison.Ordinal) && installer.Contains("AppMutex={#MyAppMutex}", StringComparison.Ordinal) && installer.Contains("{userappdata}\\{#MyDataDirName}", StringComparison.Ordinal), "base installer is parameterized so split products do not share install identity, mutex or user data paths");
        assert(einzelhandelInstaller.Contains("TOR-Einzelhandel.exe", StringComparison.Ordinal) && einzelhandelInstaller.Contains("TOR-Einzelhandel-Running", StringComparison.Ordinal) && einzelhandelInstaller.Contains("TOR-Einzelhandel-Setup", StringComparison.Ordinal) && gastroInstaller.Contains("TOR-Gastro.exe", StringComparison.Ordinal) && gastroInstaller.Contains("TOR-Gastro-Running", StringComparison.Ordinal) && gastroInstaller.Contains("TOR-Gastro-Setup", StringComparison.Ordinal), "TOR Einzelhandel and TOR Gastro have distinct installer, executable and process identities");
        assert(einzelhandelInstaller.Contains("A4E3F6A1-4B7A-4F51-8D7E-2C4A8B9F1D21", StringComparison.Ordinal) && gastroInstaller.Contains("B7D2C9E4-6A35-4C88-9F12-5E71A3D8C642", StringComparison.Ordinal), "split installers use distinct stable Windows AppIds and can be installed side by side");
        assert(installer.Contains("Name: \"{commonappdata}\\{#MyDataDirName}\"; Permissions: users-modify; Flags: uninsneveruninstall", StringComparison.Ordinal) && installer.Contains("{commondesktop}\\{#MyAppName}", StringComparison.Ordinal) && installer.Contains("{commonprograms}\\{#MyDefaultGroupName}\\{#MyAppName}", StringComparison.Ordinal) && !einzelhandelInstaller.Contains("B7D2C9E4-6A35-4C88-9F12-5E71A3D8C642", StringComparison.Ordinal) && !gastroInstaller.Contains("A4E3F6A1-4B7A-4F51-8D7E-2C4A8B9F1D21", StringComparison.Ordinal), "split uninstall preserves product data and each installer owns only its own Windows identity and shortcuts");
        // R182: building and packaging a dedicated product never proved it starts.
        // A hardcoded avares://TorPos.App/... URI shipped a product that could not
        // load its login window while every source-level check passed, so CI now
        // renders the real windows of each dedicated build.
        var snapshotTool = File.ReadAllText(FindRepoFile("Desktop/tools/TorPos.UiSnapshot/Program.cs"));
        assert(workflow.Contains("-p:TorProductEdition=KIOSK -- ci-results/ui-einzelhandel", StringComparison.Ordinal) && workflow.Contains("-p:TorProductEdition=IMBISS -- ci-results/ui-gastro", StringComparison.Ordinal) && snapshotTool.Contains("new LoginWindow(auth, settings, \"IMBISS\")", StringComparison.Ordinal), "CI renders the real windows of each dedicated product, including a fixed Kassenart, so a build that cannot come up fails before it reaches a till");

        assert(splitPublish.Contains("-p:TorProductEdition=$Edition", StringComparison.Ordinal) && workflow.Contains("TOR-POS-Split-Setups-", StringComparison.Ordinal) && workflow.Contains("TOR-Einzelhandel-Setup.exe", StringComparison.Ordinal) && workflow.Contains("TOR-Gastro-Setup.exe", StringComparison.Ordinal), "CI publishes and packages both dedicated product variants rather than only compiling the shared app");

        var migrationRoot = Path.Combine(Path.GetTempPath(), "tor-split-migration-" + Guid.NewGuid().ToString("N"));
        var legacy = Path.Combine(migrationRoot, "legacy");
        var target = Path.Combine(migrationRoot, "target");
        var backups = Path.Combine(migrationRoot, "backups");
        Directory.CreateDirectory(legacy);
        try
        {
            var legacyDb = Path.Combine(legacy, "torpos.db");
            await using (var c = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = legacyDb, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
            {
                await c.OpenAsync();
                await using var q = c.CreateCommand();
                q.CommandText = "CREATE TABLE probe(id INTEGER PRIMARY KEY, value TEXT NOT NULL); INSERT INTO probe(value) VALUES ('R181');";
                await q.ExecuteNonQueryAsync();
            }
            File.WriteAllText(Path.Combine(legacy, "edition.permanent.lock"), "KIOSK");
            Directory.CreateDirectory(Path.Combine(legacy, "ReceiptAssets"));
            File.WriteAllText(Path.Combine(legacy, "ReceiptAssets", "logo.txt"), "legacy-logo");

            var migrated = await LegacyEditionSplitMigration.TryMigrateAsync(legacy, target, backups, "KIOSK");
            assert(migrated.State == LegacySplitMigrationState.Migrated && migrated.BackupPath is not null && File.Exists(migrated.BackupPath), "split migration creates a verified backup before copying a permanently-bound R181 installation");
            assert(File.Exists(Path.Combine(target, "torpos.db")) && File.ReadAllText(Path.Combine(target, "ReceiptAssets", "logo.txt")) == "legacy-logo" && File.Exists(Path.Combine(target, LegacyEditionSplitMigration.MarkerFileName)), "split migration copies database/assets and records migration provenance in the dedicated product root");

            var safeRollback = LegacyEditionSplitMigration.EvaluateRollback(target);
            assert(File.Exists(Path.Combine(legacy, "torpos.db")) && File.ReadAllText(Path.Combine(legacy, "ReceiptAssets", "logo.txt")) == "legacy-logo" && safeRollback.State == LegacySplitRollbackState.SafeBeforeDedicatedWrites && safeRollback.LegacyDirectory == Path.GetFullPath(legacy), "split migration leaves shared R181 intact and proves lossless rollback before dedicated writes");

            var repeated = await LegacyEditionSplitMigration.TryMigrateAsync(legacy, target, backups, "KIOSK");
            assert(repeated.State == LegacySplitMigrationState.TargetAlreadyInitialized, "split migration is idempotent and never overwrites an initialized dedicated product root");

            var targetDb = Path.Combine(target, "torpos.db");
            await using (var c = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = targetDb, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
            {
                await c.OpenAsync();
                await using var q = c.CreateCommand();
                q.CommandText = "INSERT INTO probe(value) VALUES ('R182-dedicated-write');";
                await q.ExecuteNonQueryAsync();
            }
            var changedRollback = LegacyEditionSplitMigration.EvaluateRollback(target);

            // A committed SQLite write may live only in the WAL while the main DB file
            // still has the exact migration hash. Close the writer before fingerprinting:
            // the WAL remains because auto-checkpoint is disabled, while the DB file is
            // no longer locked by our own test connection.
            var walTarget = Path.Combine(migrationRoot, "wal-target");
            var walMigration = await LegacyEditionSplitMigration.TryMigrateAsync(legacy, walTarget, backups, "KIOSK");
            var walDb = Path.Combine(walTarget, "torpos.db");
            await using (var c = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = walDb, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
            {
                await c.OpenAsync();
                await using (var pragma = c.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;";
                    await pragma.ExecuteNonQueryAsync();
                }
                await using var q = c.CreateCommand();
                q.CommandText = "INSERT INTO probe(value) VALUES ('R182-wal-only-write');";
                await q.ExecuteNonQueryAsync();
            }
            var walOnlyRollback = LegacyEditionSplitMigration.EvaluateRollback(walTarget);

            var wrongTarget = Path.Combine(migrationRoot, "wrong-target");
            var mismatch = await LegacyEditionSplitMigration.TryMigrateAsync(legacy, wrongTarget, backups, "IMBISS");
            assert(mismatch.State == LegacySplitMigrationState.LegacyEditionMismatch && !Directory.Exists(wrongTarget) && changedRollback.State == LegacySplitRollbackState.DedicatedDataChanged && walMigration.State == LegacySplitMigrationState.Migrated && walOnlyRollback.State == LegacySplitRollbackState.DedicatedDataChanged, "wrong-edition migration is refused and automatic rollback closes after dedicated fiscal data changes, including committed WAL-only writes");

            // R182 regression: a till that lost power keeps committed transactions in
            // torpos.db-wal. Opening the copied database recovers and checkpoints those
            // frames into torpos.db, so the copy has to be compared BEFORE that happens
            // and the comparison has to cover the WAL. Otherwise an intact R181 backup
            // looks like a concurrent source mutation and the product refuses to start.
            var crashLegacy = Path.Combine(migrationRoot, "legacy-wal-source");
            var crashSeed = Path.Combine(migrationRoot, "wal-seed");
            Directory.CreateDirectory(crashLegacy);
            Directory.CreateDirectory(crashSeed);
            var crashSeedDb = Path.Combine(crashSeed, "torpos.db");
            await using (var crashWriter = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = crashSeedDb, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
            {
                await crashWriter.OpenAsync();
                await using (var pragma = crashWriter.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;";
                    await pragma.ExecuteNonQueryAsync();
                }
                await using (var seed = crashWriter.CreateCommand())
                {
                    seed.CommandText = "CREATE TABLE probe(id INTEGER PRIMARY KEY, value TEXT NOT NULL); INSERT INTO probe(value) VALUES ('R181-uncheckpointed');";
                    await seed.ExecuteNonQueryAsync();
                }

                // Copied while the connection is still open: SQLite checkpoints the WAL into
                // torpos.db and deletes it as soon as the last connection closes. Copying now
                // reproduces exactly what a till looks like after a power cut.
                // File.Copy would raise a Windows sharing violation here because the seeding
                // connection still holds both files with write access, so the source is opened
                // explicitly with FileShare.ReadWrite.
                foreach (var name in new[] { "torpos.db", "torpos.db-wal" })
                {
                    using var seedStream = new FileStream(Path.Combine(crashSeed, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var crashStream = new FileStream(Path.Combine(crashLegacy, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    seedStream.CopyTo(crashStream);
                }
            }
            File.WriteAllText(Path.Combine(crashLegacy, "edition.permanent.lock"), "KIOSK");

            var crashTarget = Path.Combine(migrationRoot, "wal-source-target");
            var crashMigration = await LegacyEditionSplitMigration.TryMigrateAsync(crashLegacy, crashTarget, backups, "KIOSK");
            var crashRollback = LegacyEditionSplitMigration.EvaluateRollback(crashTarget);
            assert(crashMigration.State == LegacySplitMigrationState.Migrated && File.Exists(Path.Combine(crashTarget, "torpos.db")) && File.Exists(Path.Combine(crashLegacy, "torpos.db-wal")) && crashRollback.State == LegacySplitRollbackState.SafeBeforeDedicatedWrites, "an R181 installation whose committed writes are still in the WAL migrates, keeps its source intact and stays rollback-safe");

            File.Delete(Path.Combine(legacy, "edition.permanent.lock"));
            var unprovenTarget = Path.Combine(migrationRoot, "unproven-target");
            var unproven = await LegacyEditionSplitMigration.TryMigrateAsync(legacy, unprovenTarget, backups, "KIOSK");
            assert(unproven.State == LegacySplitMigrationState.LegacyEditionUnproven && !Directory.Exists(unprovenTarget), "temporary/test R181 edition state is not enough to auto-migrate fiscal history");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(migrationRoot)) Directory.Delete(migrationRoot, recursive: true);
        }


        // R182: the products ship ready to work. Setup no longer asks for
        // credentials, the documented access (admin / admin, staff PIN 1234,
        // training code 0000) is usable at the first start instead of being
        // blocked by a forced credential dialog, and an operator who does
        // replace it is not pushed into a 10-character password. The audit
        // trace for a session on shipped credentials is asserted in R122.
        var accessRoot = Path.Combine(Path.GetTempPath(), "tor-split-access-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(accessRoot);
        try
        {
            var accessDb = await SafetyDatabase.CreateCurrentAsync(Path.Combine(accessRoot, "access.db"));
            var accessAuth = new AuthenticationService(accessDb, new AuditLogRepository(accessDb));
            await accessAuth.InitializeAsync();

            var factory = await accessAuth.LoginWithPasswordAsync("admin", "admin");
            assert(factory.Success && factory.User is { IsAdmin: true, MustChangePassword: false } && factory.User.Can(TorPos.Core.UserPermissions.Sale) && factory.User.Can(TorPos.Core.UserPermissions.ZReport), "the shipped admin access works at the first start instead of being blocked by a forced credential change");

            await accessAuth.ChangeAdminCredentialsAsync("admin", "1234", "1234");
            var replaced = await accessAuth.LoginWithPasswordAsync("admin", "1234");
            assert(replaced.Success && replaced.User is { IsAdmin: true, MustChangePassword: false }, "an operator may choose a short password and a 1234 PIN instead of a forced 10-character password");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(accessRoot)) Directory.Delete(accessRoot, recursive: true);
        }

        var setup = File.ReadAllText(FindRepoFile("Desktop/TOR-POS-Pro-Setup.iss"));
        assert(!setup.Contains("AdminPage", StringComparison.Ordinal) && !setup.Contains("first-run-admin.cfg", StringComparison.Ordinal), "setup no longer asks for admin credentials and writes no bootstrap credential file");

        var loginCode = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/LoginWindow.axaml.cs"));
        assert(loginCode.Contains("Grid.SetColumnSpan(visible, 2)", StringComparison.Ordinal) && loginCode.Contains("HorizontalAlignment.Center", StringComparison.Ordinal) && loginCode.Contains("FontSize = 22", StringComparison.Ordinal), "a fixed Kassenart spans the whole row, sits centred and is shown large enough to read at the till");
    }

    private static string FindRepoFile(string relative)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException(relative);
    }
}
