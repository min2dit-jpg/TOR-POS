using Avalonia;
using TorPos.Infrastructure;

namespace TorPos.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // R182 foundation: fixed-product builds declare their identity before
        // any infrastructure path, crash-log, licence or worker code runs.
        ProductBuild.ConfigureEnvironment();

        // R172: the same executable doubles as the isolated Swissbit worker.
        // Run this before single-instance mutexes/Avalonia so a blocked vendor
        // call can be killed without touching the cashier process.
        if (SwissbitWorkerHost.IsWorkerCommand(args))
        {
            Environment.ExitCode =
                SwissbitWorkerHost.RunAsync().GetAwaiter().GetResult();
            return;
        }

        // R66: fixed process mutex lets the installer detect an open TOR POS
        // immediately instead of waiting on Restart Manager/file locks.
        using var installerGuard = new Mutex(true, ProductBuild.RunningMutexName, out var installerGuardOwner);
        if (!installerGuardOwner) return;

        using var instance = new Mutex(true, ProductBuild.RunningMutexName + "-" +
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "|" +
                    ProductBuild.DataDirectoryName)))[..20], out var firstInstance);
        if (!firstInstance) return;

        LegacySplitMigrationResult? splitMigration = null;
        if (ProductBuild.FixedEdition is { } fixedEdition)
        {
            try
            {
                // Run before CrashLog/AppPaths creates the new product folder.
                // The migration only COPIES a permanently edition-bound R181
                // installation and keeps the shared source untouched.
                splitMigration = LegacyEditionSplitMigration
                    .TryMigrateDefaultAsync(fixedEdition)
                    .GetAwaiter()
                    .GetResult();

                if (splitMigration.State == LegacySplitMigrationState.LegacyProcessRunning)
                {
                    throw new InvalidOperationException(
                        "Die gemeinsame R181-Installation läuft noch. " +
                        "Vor der ersten Datenübernahme TOR POS Pro vollständig schließen.");
                }
            }
            catch (Exception ex)
            {
                // Never continue with an accidentally empty till after a failed
                // migration. Keep a minimal diagnostic outside the product data
                // directory so the original R181 installation remains untouched.
                try
                {
                    File.WriteAllText(
                        Path.Combine(Path.GetTempPath(), "TOR-POS-split-migration-error.txt"),
                        DateTimeOffset.Now.ToString("O") + Environment.NewLine + ex);
                }
                catch
                {
                    // Preserve the migration failure as the authoritative reason.
                }

                Environment.ExitCode = 182;
                return;
            }
        }

        CrashLog.ResetForNewProcess();
        CrashLog.InitializeGlobalHandlers();
        CrashLog.Write("Avalonia bootstrap starting.");
        if (splitMigration is not null)
        {
            CrashLog.Write(
                $"R182 split migration: state={splitMigration.State}; " +
                $"edition={splitMigration.TargetEdition}; backup={splitMigration.BackupPath ?? "none"}");
        }

        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLog.WriteException("Fatal bootstrap failure.", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
