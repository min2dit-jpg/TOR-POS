using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TorPos.Infrastructure;

public enum LegacySplitMigrationState
{
    NotSplitProduct,
    TargetAlreadyInitialized,
    NoLegacyData,
    LegacyProcessRunning,
    LegacyEditionUnproven,
    LegacyEditionMismatch,
    Migrated
}

public enum LegacySplitRollbackState
{
    NoMigrationMarker,
    LegacySourceMissing,
    SafeBeforeDedicatedWrites,
    DedicatedDataChanged
}

public sealed record LegacySplitMigrationResult(
    LegacySplitMigrationState State,
    string TargetEdition,
    string LegacyDirectory,
    string TargetDirectory,
    string? BackupPath = null);

public sealed record LegacySplitMigrationMarker(
    int Format,
    DateTimeOffset MigratedAtUtc,
    string SourceDirectory,
    string TargetEdition,
    string BackupPath,
    string? TargetDatabaseSha256 = null);

public sealed record LegacySplitRollbackResult(
    LegacySplitRollbackState State,
    string? LegacyDirectory = null,
    string? BackupPath = null);

/// <summary>
/// R182 split foundation. Copies an R181 shared installation into the matching
/// dedicated product data root without modifying or deleting the legacy source.
/// Automatic migration is deliberately conservative: only a permanent R181
/// edition lock is accepted as proof that the complete fiscal database belongs
/// to the target product. A temporary test selection is never enough.
/// </summary>
public static class LegacyEditionSplitMigration
{
    public const string MarkerFileName = "split-migration.json";

    public static Task<LegacySplitMigrationResult> TryMigrateDefaultAsync(
        string? targetEdition,
        CancellationToken ct = default)
    {
        var edition = Normalize(targetEdition);
        if (edition is null)
        {
            return Task.FromResult(new LegacySplitMigrationResult(
                LegacySplitMigrationState.NotSplitProduct,
                "", "", ""));
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var legacy = Path.Combine(appData, "TOR-POS-Pro");
        var target = Path.Combine(
            appData,
            edition == "KIOSK" ? "TOR-KIOSK" : "TOR-DOENER");
        var backupRoot = Path.Combine(appData, "TOR-POS-Migration-Backups");

        return TryMigrateAsync(legacy, target, backupRoot, edition, checkLegacyMutex: true, ct);
    }

    public static async Task<LegacySplitMigrationResult> TryMigrateAsync(
        string legacyDirectory,
        string targetDirectory,
        string backupDirectory,
        string targetEdition,
        bool checkLegacyMutex = false,
        CancellationToken ct = default)
    {
        var edition = Normalize(targetEdition)
            ?? throw new ArgumentException("Edition must be KIOSK or IMBISS.", nameof(targetEdition));

        legacyDirectory = Path.GetFullPath(legacyDirectory);
        targetDirectory = Path.GetFullPath(targetDirectory);
        backupDirectory = Path.GetFullPath(backupDirectory);

        if (IsInitializedTarget(targetDirectory))
        {
            return new LegacySplitMigrationResult(
                LegacySplitMigrationState.TargetAlreadyInitialized,
                edition, legacyDirectory, targetDirectory);
        }

        var legacyDb = Path.Combine(legacyDirectory, "torpos.db");
        if (!Directory.Exists(legacyDirectory) || !File.Exists(legacyDb))
        {
            return new LegacySplitMigrationResult(
                LegacySplitMigrationState.NoLegacyData,
                edition, legacyDirectory, targetDirectory);
        }

        if (checkLegacyMutex && IsLegacyProcessRunning())
        {
            return new LegacySplitMigrationResult(
                LegacySplitMigrationState.LegacyProcessRunning,
                edition, legacyDirectory, targetDirectory);
        }

        var permanentLock = ReadEditionLock(
            Path.Combine(legacyDirectory, "edition.permanent.lock"));
        if (permanentLock is null)
        {
            return new LegacySplitMigrationResult(
                LegacySplitMigrationState.LegacyEditionUnproven,
                edition, legacyDirectory, targetDirectory);
        }

        if (!string.Equals(permanentLock, edition, StringComparison.Ordinal))
        {
            return new LegacySplitMigrationResult(
                LegacySplitMigrationState.LegacyEditionMismatch,
                edition, legacyDirectory, targetDirectory);
        }

        Directory.CreateDirectory(backupDirectory);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var backupPath = Path.Combine(
            backupDirectory,
            $"TOR-POS-Pro-before-{edition}-split-{stamp}-{suffix}.zip");

        // Backup FIRST. The old R181 folder is never written by this migration.
        ZipFile.CreateFromDirectory(
            legacyDirectory, backupPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        VerifyBackupPackage(backupPath, edition);

        var targetParent = Path.GetDirectoryName(targetDirectory)
            ?? throw new InvalidOperationException("Target directory has no parent.");
        Directory.CreateDirectory(targetParent);

        var staging = targetDirectory + ".migrating-" + Guid.NewGuid().ToString("N");
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);

        try
        {
            Directory.CreateDirectory(staging);
            CopyDirectory(legacyDirectory, staging, ct);

            var stagedDb = Path.Combine(staging, "torpos.db");
            VerifyDatabaseReadable(stagedDb);
            var migratedDbHash = Sha256File(stagedDb);

            var marker = new LegacySplitMigrationMarker(
                2,
                DateTimeOffset.UtcNow,
                legacyDirectory,
                edition,
                backupPath,
                migratedDbHash);
            await File.WriteAllTextAsync(
                Path.Combine(staging, MarkerFileName),
                JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }),
                ct);

            if (Directory.Exists(targetDirectory))
            {
                if (Directory.EnumerateFileSystemEntries(targetDirectory).Any())
                    throw new InvalidOperationException("Split target became non-empty during migration.");
                Directory.Delete(targetDirectory);
            }

            Directory.Move(staging, targetDirectory);
        }
        catch
        {
            try
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
            catch
            {
                // Preserve the original exception. The source and backup remain intact.
            }
            throw;
        }

        return new LegacySplitMigrationResult(
            LegacySplitMigrationState.Migrated,
            edition, legacyDirectory, targetDirectory, backupPath);
    }

    /// <summary>
    /// Proves whether returning to the untouched shared R181 data is lossless.
    /// It never copies/deletes data. Once the dedicated database changed, an
    /// automatic rejoin is deliberately refused because fiscal histories must
    /// not be silently merged or discarded.
    /// </summary>
    public static LegacySplitRollbackResult EvaluateRollback(string targetDirectory)
    {
        var markerPath = Path.Combine(targetDirectory, MarkerFileName);
        if (!File.Exists(markerPath))
            return new LegacySplitRollbackResult(LegacySplitRollbackState.NoMigrationMarker);

        LegacySplitMigrationMarker? marker;
        try
        {
            marker = JsonSerializer.Deserialize<LegacySplitMigrationMarker>(File.ReadAllText(markerPath));
        }
        catch
        {
            return new LegacySplitRollbackResult(LegacySplitRollbackState.NoMigrationMarker);
        }

        if (marker is null || string.IsNullOrWhiteSpace(marker.SourceDirectory))
            return new LegacySplitRollbackResult(LegacySplitRollbackState.NoMigrationMarker);

        if (!File.Exists(Path.Combine(marker.SourceDirectory, "torpos.db")))
        {
            return new LegacySplitRollbackResult(
                LegacySplitRollbackState.LegacySourceMissing,
                marker.SourceDirectory,
                marker.BackupPath);
        }

        var targetDb = Path.Combine(targetDirectory, "torpos.db");
        if (string.IsNullOrWhiteSpace(marker.TargetDatabaseSha256) || !File.Exists(targetDb))
        {
            return new LegacySplitRollbackResult(
                LegacySplitRollbackState.DedicatedDataChanged,
                marker.SourceDirectory,
                marker.BackupPath);
        }

        var unchanged = string.Equals(
            Sha256File(targetDb),
            marker.TargetDatabaseSha256,
            StringComparison.OrdinalIgnoreCase);
        return new LegacySplitRollbackResult(
            unchanged
                ? LegacySplitRollbackState.SafeBeforeDedicatedWrites
                : LegacySplitRollbackState.DedicatedDataChanged,
            marker.SourceDirectory,
            marker.BackupPath);
    }

    private static bool IsInitializedTarget(string targetDirectory)
    {
        if (!Directory.Exists(targetDirectory)) return false;
        return File.Exists(Path.Combine(targetDirectory, "torpos.db")) ||
               File.Exists(Path.Combine(targetDirectory, MarkerFileName)) ||
               Directory.EnumerateFileSystemEntries(targetDirectory).Any();
    }

    private static string? ReadEditionLock(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return Normalize(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static void VerifyBackupPackage(string backupPath, string edition)
    {
        using var zip = ZipFile.OpenRead(backupPath);
        if (zip.GetEntry("torpos.db") is null)
            throw new InvalidDataException("Migration backup does not contain torpos.db.");

        var lockEntry = zip.GetEntry("edition.permanent.lock")
            ?? throw new InvalidDataException("Migration backup does not contain the permanent edition lock.");
        using var reader = new StreamReader(lockEntry.Open());
        var lockValue = Normalize(reader.ReadToEnd());
        if (!string.Equals(lockValue, edition, StringComparison.Ordinal))
            throw new InvalidDataException("Migration backup edition does not match the target product.");
    }

    private static void VerifyDatabaseReadable(string path)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            Cache = SqliteCacheMode.Private
        }.ToString();

        using var connection = new SqliteConnection(cs);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var value = Convert.ToString(command.ExecuteScalar());
        if (!string.Equals(value, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Migrated database failed SQLite quick_check.");
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void CopyDirectory(string source, string destination, CancellationToken ct)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Reparse points are not allowed in the migration source.");

            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Reparse points are not allowed in the migration source.");

            var relative = Path.GetRelativePath(source, file);
            var output = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Copy(file, output, overwrite: false);
        }
    }

    private static bool IsLegacyProcessRunning()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            if (!Mutex.TryOpenExisting("TOR-POS-Pro-Running", out var mutex))
                return false;
            mutex.Dispose();
            return true;
        }
        catch
        {
            // Failure to inspect a mutex must never authorize a destructive migration.
            return true;
        }
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToUpperInvariant();
        return normalized is "KIOSK" or "IMBISS" ? normalized : null;
    }
}
