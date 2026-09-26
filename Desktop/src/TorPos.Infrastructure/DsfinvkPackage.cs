using System.IO.Compression;
using System.Security.Cryptography;

namespace TorPos.Infrastructure;

public sealed record DsfinvkPackageResult(string ZipPath, string Sha256, int FileCount, long Bytes, string ChecksumPath);

/// <summary>
/// Packs a finished DSFinV-K export folder into a ZIP for delivery (e-mail,
/// Steuerberater) and proves the package is lossless: every entry is read back
/// and compared byte for byte (SHA-256) with the export file, and the ZIP's own
/// SHA-256 is written next to it. The export files are never re-encoded or
/// reformatted; a package that does not match is deleted and refused.
/// </summary>
public static class DsfinvkPackage
{
    public static DsfinvkPackageResult Create(string exportFolder, string zipPath)
    {
        if (!Directory.Exists(exportFolder))
            throw new DirectoryNotFoundException($"Exportordner fehlt: {exportFolder}");
        if (exportFolder.EndsWith(".unvollstaendig", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Ein unvollständiger Export wird nicht verpackt.");
        if (File.Exists(zipPath))
            throw new IOException($"ZIP existiert bereits: {zipPath}");

        ZipFile.CreateFromDirectory(exportFolder, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
        try
        {
            var files = Verify(exportFolder, zipPath);
            var sha = FileSha256(zipPath);
            var checksumPath = zipPath + ".sha256";
            File.WriteAllText(checksumPath, $"{sha}  {Path.GetFileName(zipPath)}\n");
            return new DsfinvkPackageResult(zipPath, sha, files, new FileInfo(zipPath).Length, checksumPath);
        }
        catch
        {
            File.Delete(zipPath);
            throw;
        }
    }

    /// <summary>Returns the number of files; throws when the ZIP differs from the folder in any way.</summary>
    public static int Verify(string exportFolder, string zipPath)
    {
        var baseName = new DirectoryInfo(exportFolder).Name;
        var expected = Directory.GetFiles(exportFolder, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => baseName + "/" + Path.GetRelativePath(exportFolder, path).Replace('\\', '/'),
                FileSha256,
                StringComparer.Ordinal);

        using var archive = ZipFile.OpenRead(zipPath);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/'))
                continue;
            if (!expected.TryGetValue(name, out var hash))
                throw new InvalidOperationException($"ZIP enthält eine unerwartete Datei: {name}");
            using var stream = entry.Open();
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), hash, StringComparison.Ordinal))
                throw new InvalidOperationException($"ZIP-Inhalt weicht vom Export ab: {name}");
            seen.Add(name);
        }

        var missing = expected.Keys.Where(x => !seen.Contains(x)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"ZIP ist unvollständig, es fehlt: {string.Join(", ", missing)}");
        return seen.Count;
    }

    public static string FileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
