using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TorPos.Infrastructure;

public sealed record BackupManifest(
    int Format,
    DateTimeOffset CreatedAt,
    Dictionary<string,string> Files);

public sealed record BackupVerification(
    int Files,
    long Products,
    long Sales);

public sealed class FullBackupService(
    SqliteDatabase db,
    string dataDirectory)
{
    public Task<string> CreateAsync(
        string destination) =>
        Task.Run(async () =>
        {
            Directory.CreateDirectory(destination);

            var staging = Path.Combine(
                Path.GetTempPath(),
                "tor-backup-" +
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(staging);

            var target = Path.Combine(
                destination,
                "TOR-POS-Data-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss") +
                "-" +
                Guid.NewGuid().ToString("N") +
                ".zip");

            try
            {
                var snapshot =
                    await new DatabaseBackupService(db)
                        .CreateBackupAsync(staging);

                File.Move(
                    snapshot,
                    Path.Combine(
                        staging,
                        "torpos.db"));

                foreach (var folder in new[]
                         {
                             "ProductImages",
                             "ReceiptAssets",
                             "PrintJobs",
                             "CustomerDisplayAds"
                         })
                {
                    var source =
                        Path.Combine(
                            dataDirectory,
                            folder);

                    if (!Directory.Exists(source))
                        continue;

                    if ((File.GetAttributes(source) &
                         FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidDataException(
                            "Verknüpfter Datenordner muss separat gesichert werden: " +
                            folder);
                    }

                    foreach (var file in
                             Directory.EnumerateFiles(
                                 source,
                                 "*",
                                 new EnumerationOptions
                                 {
                                     RecurseSubdirectories = true,
                                     AttributesToSkip =
                                         FileAttributes.ReparsePoint,
                                     IgnoreInaccessible = false
                                 }))
                    {
                        if (file.EndsWith(
                            ".tmp",
                            StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var copy = Path.Combine(
                            staging,
                            Path.GetRelativePath(
                                dataDirectory,
                                file));

                        Directory.CreateDirectory(
                            Path.GetDirectoryName(copy)!);

                        File.Copy(
                            file,
                            copy);
                    }
                }

                var hashes =
                    new Dictionary<string,string>();

                foreach (var file in
                         Directory.EnumerateFiles(
                             staging,
                             "*",
                             SearchOption.AllDirectories))
                {
                    hashes[
                        Path.GetRelativePath(
                                staging,
                                file)
                            .Replace('\\','/')
                    ] = Hash(file);
                }

                await File.WriteAllTextAsync(
                    Path.Combine(
                        staging,
                        "backup-manifest.json"),
                    JsonSerializer.Serialize(
                        new BackupManifest(
                            1,
                            DateTimeOffset.UtcNow,
                            hashes),
                        new JsonSerializerOptions
                        {
                            WriteIndented = true
                        }));

                await File.WriteAllTextAsync(
                    Path.Combine(
                        staging,
                        "RESTORE-HINWEIS.txt"),
                    "Nur bei geschlossener Kasse und nach Prüfung wiederherstellen. " +
                    "Absolute Bildpfade ggf. anpassen. Windows-geschützte Zugangsdaten " +
                    "auf neuem Konto erneut einrichten. Enthalten: Datenbank, " +
                    "ProductImages, ReceiptAssets, PrintJobs, CustomerDisplayAds. Nicht enthalten: " +
                    "laufende Warenkorb-Recovery, externe Dateien, Updates. " +
                    "Offene Zahlungen/Druckaufträge nach Wiederherstellung prüfen.");

                var tempArchive =
                    target + ".tmp";

                ZipFile.CreateFromDirectory(
                    staging,
                    tempArchive,
                    CompressionLevel.Optimal,
                    includeBaseDirectory: false);

                using (var file = new FileStream(
                    tempArchive,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None))
                {
                    file.Flush(flushToDisk: true);
                }

                File.Move(
                    tempArchive,
                    target);

                return target;
            }
            finally
            {
                // The SQLite snapshot itself is non-pooled. Short transient
                // Windows Defender/indexer handles can still exist for a few
                // milliseconds, therefore cleanup is bounded and retried.
                await DeleteDirectoryWithRetryAsync(
                    staging);

                await DeleteFileWithRetryAsync(
                    target + ".tmp");
            }
        });

    public static Task<BackupVerification> VerifyRestoreAsync(
        string package,
        string newDirectory) =>
        Task.Run(async () =>
        {
            if (Directory.Exists(newDirectory) ||
                File.Exists(newDirectory))
            {
                throw new InvalidOperationException(
                    "Prüfziel muss neu sein. Bestehende Daten werden nicht überschrieben.");
            }

            Directory.CreateDirectory(
                newDirectory);

            var valid = false;

            try
            {
                using (var archive =
                       ZipFile.OpenRead(package))
                {
                    if (archive.Entries.Count > 50000 ||
                        archive.Entries.Sum(
                            x => x.Length) >
                        2L * 1024 * 1024 * 1024)
                    {
                        throw new InvalidDataException(
                            "Sicherung überschreitet Prüfgrenze (2 GB / 50000 Dateien).");
                    }

                    var entry =
                        archive.GetEntry(
                            "backup-manifest.json")
                        ?? throw new InvalidDataException(
                            "Sicherungsmanifest fehlt.");

                    if (entry.Length >
                        16 * 1024 * 1024)
                    {
                        throw new InvalidDataException(
                            "Sicherungsmanifest zu groß.");
                    }

                    BackupManifest manifest;

                    using (var stream =
                           entry.Open())
                    {
                        manifest =
                            await JsonSerializer
                                .DeserializeAsync<BackupManifest>(
                                    stream)
                            ?? throw new InvalidDataException(
                                "Manifest beschädigt.");
                    }

                    if (manifest.Format != 1 ||
                        !manifest.Files.ContainsKey(
                            "torpos.db"))
                    {
                        throw new InvalidDataException(
                            "Sicherungsformat ungültig.");
                    }

                    if (archive.Entries
                            .Select(x => x.FullName)
                            .Distinct(
                                StringComparer.OrdinalIgnoreCase)
                            .Count() !=
                        archive.Entries.Count)
                    {
                        throw new InvalidDataException(
                            "Doppelte Dateinamen.");
                    }

                    foreach (var (name, hash)
                             in manifest.Files)
                    {
                        if (name.Contains('\\') ||
                            name.Contains(':') ||
                            name.StartsWith('/') ||
                            name.Split('/')
                                .Any(x =>
                                    x is ".." or "." or "" ||
                                    x.EndsWith('.') ||
                                    x.EndsWith(' ')))
                        {
                            throw new InvalidDataException(
                                "Unsicherer Sicherungspfad.");
                        }

                        var file =
                            archive.GetEntry(name)
                            ?? throw new InvalidDataException(
                                "Datei fehlt: " + name);

                        var output =
                            Path.Combine(
                                newDirectory,
                                name);

                        Directory.CreateDirectory(
                            Path.GetDirectoryName(
                                output)!);

                        using (var input =
                               file.Open())
                        using (var dest =
                               new FileStream(
                                   output,
                                   FileMode.CreateNew,
                                   FileAccess.Write,
                                   FileShare.None))
                        {
                            await input.CopyToAsync(
                                dest);
                        }

                        if (!string.Equals(
                            Hash(output),
                            hash,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException(
                                "Prüfsumme falsch: " +
                                name);
                        }
                    }

                    var dbPath =
                        Path.Combine(
                            newDirectory,
                            "torpos.db");

                    long products;
                    long sales;

                    using (var connection =
                           new SqliteConnection(
                               new SqliteConnectionStringBuilder
                               {
                                   DataSource = dbPath,
                                   Mode = SqliteOpenMode.ReadOnly,
                                   Pooling = false,
                                   Cache = SqliteCacheMode.Private
                               }.ToString()))
                    {
                        await connection.OpenAsync();

                        using var command =
                            connection.CreateCommand();

                        command.CommandText =
                            "PRAGMA integrity_check;";

                        if ((string?)
                            await command.ExecuteScalarAsync()
                            != "ok")
                        {
                            throw new InvalidDataException(
                                "SQLite-Integritätsprüfung fehlgeschlagen.");
                        }

                        command.CommandText =
                            "PRAGMA foreign_key_check;";

                        using (var reader =
                               await command
                                   .ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                throw new InvalidDataException(
                                    "SQLite-Verknüpfungen beschädigt.");
                            }
                        }

                        command.CommandText =
                            "SELECT COUNT(*) FROM products;";

                        products =
                            Convert.ToInt64(
                                await command
                                    .ExecuteScalarAsync());

                        command.CommandText =
                            "SELECT COUNT(*) FROM sales;";

                        sales =
                            Convert.ToInt64(
                                await command
                                    .ExecuteScalarAsync());
                    }

                    valid = true;

                    return new BackupVerification(
                        manifest.Files.Count,
                        products,
                        sales);
                }
            }
            finally
            {
                if (!valid)
                {
                    await DeleteDirectoryWithRetryAsync(
                        newDirectory);
                }
            }
        });

    private static async Task DeleteDirectoryWithRetryAsync(
        string path)
    {
        Exception? last = null;

        for (var attempt = 0;
             attempt < 8;
             attempt++)
        {
            if (!Directory.Exists(path))
                return;

            try
            {
                Directory.Delete(
                    path,
                    recursive: true);

                return;
            }
            catch (IOException ex)
            {
                last = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                last = ex;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(
                    50 * (attempt + 1)));
        }

        if (Directory.Exists(path))
        {
            throw new IOException(
                "Temporäres Backup-Verzeichnis konnte nicht bereinigt werden: " +
                path,
                last);
        }
    }

    private static async Task DeleteFileWithRetryAsync(
        string path)
    {
        Exception? last = null;

        for (var attempt = 0;
             attempt < 8;
             attempt++)
        {
            if (!File.Exists(path))
                return;

            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException ex)
            {
                last = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                last = ex;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(
                    50 * (attempt + 1)));
        }

        if (File.Exists(path))
        {
            throw new IOException(
                "Temporäre Backup-Datei konnte nicht bereinigt werden: " +
                path,
                last);
        }
    }

    private static string Hash(
        string path)
    {
        using var file =
            File.OpenRead(path);

        return Convert.ToHexString(
            SHA256.HashData(file));
    }
}
