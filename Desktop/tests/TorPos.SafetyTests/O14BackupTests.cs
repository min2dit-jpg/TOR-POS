using System.IO.Compression;
using TorPos.Infrastructure;

// O-14: the full backup left out the TSE TAR exports (which must be kept and
// exist nowhere else), the reports and the licence file. With encryption on,
// the plain backup was first written to the backup medium and only then
// deleted - recoverable from a USB stick or share.
public static class O14BackupTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "o14-backup");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "o14.db"));
        var settings = new SettingsRepository(db);

        var data = Path.Combine(dir, "data");
        Directory.CreateDirectory(Path.Combine(data, "TseExports"));
        Directory.CreateDirectory(Path.Combine(data, "Reports", "Monatsberichte"));
        await File.WriteAllTextAsync(Path.Combine(data, "TseExports", "tse-2026-09.tar"), "TAR");
        await File.WriteAllTextAsync(Path.Combine(data, "Reports", "Monatsberichte", "2026-09.pdf"), "PDF");
        await File.WriteAllTextAsync(Path.Combine(data, "commercial-license.json"), "{}");

        var plainDestination = Path.Combine(dir, "plain-out");
        var encryption = new BackupEncryptionService(settings);
        var full = new FullBackupService(db, data);
        var package = await encryption.CreateProtectedAsync(full.CreateAsync, plainDestination);
        string[] entries;
        using (var zip = ZipFile.OpenRead(package))
            entries = zip.Entries.Select(x => x.FullName.Replace('\\', '/')).ToArray();
        assert(
            Path.GetDirectoryName(package) == plainDestination &&
            entries.Contains("TseExports/tse-2026-09.tar") &&
            entries.Contains("Reports/Monatsberichte/2026-09.pdf") &&
            entries.Contains("commercial-license.json"),
            "O-14 the full backup contains the TSE TAR exports, the reports and the licence file");

        await encryption.EnableAndGenerateRecoveryCodeAsync();
        var usb = Path.Combine(dir, "usb-stick");
        var written = new List<string>();
        var encrypted = await encryption.CreateProtectedAsync(async target =>
        {
            written.Add(target);
            var p = Path.Combine(target, "TOR-POS-test.db");
            await File.WriteAllTextAsync(p, "plain database");
            return p;
        }, usb);
        var onStick = Directory.GetFiles(usb);
        assert(
            written.Count == 1 && !written[0].StartsWith(usb, StringComparison.OrdinalIgnoreCase) &&
            !Directory.Exists(written[0]) &&
            onStick.Length == 1 && onStick[0] == encrypted &&
            BackupEncryptionService.LooksEncrypted(encrypted),
            "O-14 with encryption on, the plain backup is made in a local temporary folder and only the encrypted file reaches the backup medium");
        await encryption.DisableAsync();
    }
}
