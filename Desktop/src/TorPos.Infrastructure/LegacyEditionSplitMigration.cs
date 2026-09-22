using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
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

public sealed record LegacySplitMigrationResult(LegacySplitMigrationState State,string TargetEdition,string LegacyDirectory,string TargetDirectory,string? BackupPath = null);
public sealed record LegacySplitMigrationMarker(int Format,DateTimeOffset MigratedAtUtc,string SourceDirectory,string TargetEdition,string BackupPath,string? TargetDatabaseSha256 = null);
public sealed record LegacySplitRollbackResult(LegacySplitRollbackState State,string? LegacyDirectory = null,string? BackupPath = null);

public static class LegacyEditionSplitMigration
{
    public const string MarkerFileName = "split-migration.json";

    public static Task<LegacySplitMigrationResult> TryMigrateDefaultAsync(string? targetEdition,CancellationToken ct = default)
    {
        var edition = Normalize(targetEdition);
        if (edition is null) return Task.FromResult(new LegacySplitMigrationResult(LegacySplitMigrationState.NotSplitProduct,"", "", ""));
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var legacy = Path.Combine(appData,"TOR-POS-Pro");
        var target = Path.Combine(appData,edition == "KIOSK" ? "TOR-Einzelhandel" : "TOR-Gastro");
        var backupRoot = Path.Combine(appData,"TOR-POS-Migration-Backups");
        return TryMigrateAsync(legacy,target,backupRoot,edition,checkLegacyMutex:true,ct);
    }

    public static async Task<LegacySplitMigrationResult> TryMigrateAsync(string legacyDirectory,string targetDirectory,string backupDirectory,string targetEdition,bool checkLegacyMutex = false,CancellationToken ct = default)
    {
        var edition = Normalize(targetEdition) ?? throw new ArgumentException("Edition must be KIOSK or IMBISS.",nameof(targetEdition));
        legacyDirectory=Path.GetFullPath(legacyDirectory); targetDirectory=Path.GetFullPath(targetDirectory); backupDirectory=Path.GetFullPath(backupDirectory);
        if(IsInitializedTarget(targetDirectory)) return new(LegacySplitMigrationState.TargetAlreadyInitialized,edition,legacyDirectory,targetDirectory);
        var legacyDb=Path.Combine(legacyDirectory,"torpos.db");
        if(!Directory.Exists(legacyDirectory)||!File.Exists(legacyDb)) return new(LegacySplitMigrationState.NoLegacyData,edition,legacyDirectory,targetDirectory);
        if(checkLegacyMutex&&IsLegacyProcessRunning()) return new(LegacySplitMigrationState.LegacyProcessRunning,edition,legacyDirectory,targetDirectory);
        var permanentLock=ReadEditionLock(Path.Combine(legacyDirectory,"edition.permanent.lock"));
        if(permanentLock is null) return new(LegacySplitMigrationState.LegacyEditionUnproven,edition,legacyDirectory,targetDirectory);
        if(!string.Equals(permanentLock,edition,StringComparison.Ordinal)) return new(LegacySplitMigrationState.LegacyEditionMismatch,edition,legacyDirectory,targetDirectory);

        // Freeze fingerprints before backup/copy. The main database hash verifies the backup
        // package; the full persistent-state fingerprint (db + WAL) verifies the staging copy,
        // because a crash-interrupted R181 till keeps committed frames in torpos.db-wal.
        var sourceDbHash=Sha256File(legacyDb);
        var sourceStateFingerprint=DatabaseStateFingerprint(legacyDirectory);
        Directory.CreateDirectory(backupDirectory);
        var stamp=DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"); var suffix=Guid.NewGuid().ToString("N")[..10];
        var backupPath=Path.Combine(backupDirectory,$"TOR-POS-Pro-before-{edition}-split-{stamp}-{suffix}.zip");
        ZipFile.CreateFromDirectory(legacyDirectory,backupPath,CompressionLevel.Optimal,includeBaseDirectory:false);
        VerifyBackupPackage(backupPath,edition,sourceDbHash);

        var targetParent=Path.GetDirectoryName(targetDirectory)??throw new InvalidOperationException("Target directory has no parent.");
        Directory.CreateDirectory(targetParent);
        var staging=targetDirectory+".migrating-"+Guid.NewGuid().ToString("N");
        if(Directory.Exists(staging)) Directory.Delete(staging,true);
        try
        {
            Directory.CreateDirectory(staging); CopyDirectory(legacyDirectory,staging,ct);
            var stagedDb=Path.Combine(staging,"torpos.db");
            // R182: the copy is compared BEFORE anything opens it. Opening a database whose WAL
            // still carries committed frames recovers and checkpoints them into torpos.db, so a
            // byte-perfect copy would otherwise look like a concurrent source mutation and the
            // dedicated product would refuse to start. The comparison covers db + WAL.
            var migratedStateFingerprint=DatabaseStateFingerprint(staging);
            if(!string.Equals(sourceStateFingerprint,migratedStateFingerprint,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Legacy database changed while split migration was being copied.");
            VerifyDatabaseReadable(stagedDb);
            // Taken after the readability check so the marker records the state the dedicated
            // product really starts from, including a checkpoint performed by that check.
            var stateFingerprint=DatabaseStateFingerprint(staging);
            var marker=new LegacySplitMigrationMarker(3,DateTimeOffset.UtcNow,legacyDirectory,edition,backupPath,stateFingerprint);
            await File.WriteAllTextAsync(Path.Combine(staging,MarkerFileName),JsonSerializer.Serialize(marker,new JsonSerializerOptions{WriteIndented=true}),ct);
            if(Directory.Exists(targetDirectory)){if(Directory.EnumerateFileSystemEntries(targetDirectory).Any()) throw new InvalidOperationException("Split target became non-empty during migration."); Directory.Delete(targetDirectory);}
            Directory.Move(staging,targetDirectory);
        }
        catch
        {
            try{if(Directory.Exists(staging)) Directory.Delete(staging,true);}catch{}
            throw;
        }
        return new(LegacySplitMigrationState.Migrated,edition,legacyDirectory,targetDirectory,backupPath);
    }

    public static LegacySplitRollbackResult EvaluateRollback(string targetDirectory)
    {
        var markerPath=Path.Combine(targetDirectory,MarkerFileName); if(!File.Exists(markerPath)) return new(LegacySplitRollbackState.NoMigrationMarker);
        LegacySplitMigrationMarker? marker; try{marker=JsonSerializer.Deserialize<LegacySplitMigrationMarker>(File.ReadAllText(markerPath));}catch{return new(LegacySplitRollbackState.NoMigrationMarker);}
        if(marker is null||string.IsNullOrWhiteSpace(marker.SourceDirectory)) return new(LegacySplitRollbackState.NoMigrationMarker);
        if(!File.Exists(Path.Combine(marker.SourceDirectory,"torpos.db"))) return new(LegacySplitRollbackState.LegacySourceMissing,marker.SourceDirectory,marker.BackupPath);
        var targetDb=Path.Combine(targetDirectory,"torpos.db");
        if(string.IsNullOrWhiteSpace(marker.TargetDatabaseSha256)||!File.Exists(targetDb)) return new(LegacySplitRollbackState.DedicatedDataChanged,marker.SourceDirectory,marker.BackupPath);
        // Format 3 fingerprints the complete persistent SQLite state (main DB + WAL).
        // Older format-2 markers hashed only torpos.db; treat them conservatively if a WAL
        // exists because committed dedicated writes can live only in that file.
        bool unchanged;
        if(marker.Format >= 3)
            unchanged=string.Equals(DatabaseStateFingerprint(targetDirectory),marker.TargetDatabaseSha256,StringComparison.OrdinalIgnoreCase);
        else
            unchanged=!File.Exists(targetDb+"-wal") && string.Equals(Sha256File(targetDb),marker.TargetDatabaseSha256,StringComparison.OrdinalIgnoreCase);
        return new(unchanged?LegacySplitRollbackState.SafeBeforeDedicatedWrites:LegacySplitRollbackState.DedicatedDataChanged,marker.SourceDirectory,marker.BackupPath);
    }

    private static bool IsInitializedTarget(string targetDirectory){if(!Directory.Exists(targetDirectory))return false;return File.Exists(Path.Combine(targetDirectory,"torpos.db"))||File.Exists(Path.Combine(targetDirectory,MarkerFileName))||Directory.EnumerateFileSystemEntries(targetDirectory).Any();}
    private static string? ReadEditionLock(string path){try{return File.Exists(path)?Normalize(File.ReadAllText(path)):null;}catch{return null;}}

    private static void VerifyBackupPackage(string backupPath,string edition,string expectedDbSha256)
    {
        using var zip=ZipFile.OpenRead(backupPath);
        var dbEntry=zip.GetEntry("torpos.db")??throw new InvalidDataException("Migration backup does not contain torpos.db.");
        using(var dbStream=dbEntry.Open())
        {
            var backupHash=Convert.ToHexString(SHA256.HashData(dbStream));
            if(!string.Equals(backupHash,expectedDbSha256,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Migration backup database does not match the source fingerprint.");
        }
        var lockEntry=zip.GetEntry("edition.permanent.lock")??throw new InvalidDataException("Migration backup does not contain the permanent edition lock.");
        using var reader=new StreamReader(lockEntry.Open()); var lockValue=Normalize(reader.ReadToEnd());
        if(!string.Equals(lockValue,edition,StringComparison.Ordinal)) throw new InvalidDataException("Migration backup edition does not match the target product.");
    }

    private static void VerifyDatabaseReadable(string path)
    {
        var cs=new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadWrite,Pooling=false,Cache=SqliteCacheMode.Private}.ToString();
        using var connection=new SqliteConnection(cs); connection.Open(); using var command=connection.CreateCommand(); command.CommandText="PRAGMA quick_check;";
        var value=Convert.ToString(command.ExecuteScalar()); if(!string.Equals(value,"ok",StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Migrated database failed SQLite quick_check.");
    }

    private static string DatabaseStateFingerprint(string directory)
    {
        // -shm is intentionally excluded: it is transient shared-memory state. WAL is
        // persistent transaction state and MUST participate in rollback qualification.
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach(var name in new[]{"torpos.db","torpos.db-wal"})
        {
            var path=Path.Combine(directory,name);
            var nameBytes=Encoding.UTF8.GetBytes(name);
            hash.AppendData(BitConverter.GetBytes(nameBytes.Length)); hash.AppendData(nameBytes);
            if(!File.Exists(path)){hash.AppendData(new byte[]{0});continue;}
            hash.AppendData(new byte[]{1});
            using var stream=File.OpenRead(path); var buffer=new byte[81920]; int read;
            while((read=stream.Read(buffer,0,buffer.Length))>0) hash.AppendData(buffer,0,read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Sha256File(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream));}
    private static void CopyDirectory(string source,string destination,CancellationToken ct)
    {
        foreach(var directory in Directory.EnumerateDirectories(source,"*",SearchOption.AllDirectories)){ct.ThrowIfCancellationRequested();var info=new DirectoryInfo(directory);if((info.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Reparse points are not allowed in the migration source.");var relative=Path.GetRelativePath(source,directory);Directory.CreateDirectory(Path.Combine(destination,relative));}
        foreach(var file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories)){ct.ThrowIfCancellationRequested();var info=new FileInfo(file);if((info.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Reparse points are not allowed in the migration source.");var relative=Path.GetRelativePath(source,file);var output=Path.Combine(destination,relative);Directory.CreateDirectory(Path.GetDirectoryName(output)!);File.Copy(file,output,false);}
    }
    private static bool IsLegacyProcessRunning(){if(!OperatingSystem.IsWindows())return false;try{if(!Mutex.TryOpenExisting("TOR-POS-Pro-Running",out var mutex))return false;mutex.Dispose();return true;}catch{return true;}}
    private static string? Normalize(string? value){if(string.IsNullOrWhiteSpace(value))return null;var normalized=value.Trim().ToUpperInvariant();return normalized is "KIOSK" or "IMBISS"?normalized:null;}
}
