using TorPos.Core;
using TorPos.Infrastructure;

// CSV cells TOR writes for Excel/LibreOffice cannot start a formula, and a
// backup is never silently left unencrypted while encryption is switched on.
public static class CsvAndBackupHardeningTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "csv-backup-hardening");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "hardening.db"));
        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        var management = new BusinessManagementService(db, settings, audit);

        // ------------------------------------------------ CSV formula guard
        assert(
            CsvCells.Text("=HYPERLINK(\"http://x\";\"Klick\")") == "\"'=HYPERLINK(\"\"http://x\"\";\"\"Klick\"\")\"" &&
            CsvCells.Text("+49 30") == "\"'+49 30\"" && CsvCells.Text("-Rabatt") == "\"'-Rabatt\"" &&
            CsvCells.Text("@SUMME(A1)") == "\"'@SUMME(A1)\"" && CsvCells.Text("\tx") == "\"'\tx\"" &&
            CsvCells.Text("Döner = lecker") == "\"Döner = lecker\"" && CsvCells.Text(null) == "\"\"" &&
            CsvCells.Unguard("'=1+1") == "=1+1" && CsvCells.Unguard("'normal") == "'normal",
            "CSV: a text cell that Excel would read as a formula (= + - @ tab) gets a leading apostrophe; ordinary text is unchanged");

        var source = Path.Combine(dir, "quelle.csv");
        await File.WriteAllTextAsync(source,
            "GRUPPE;WARENGRUPPE;ARTIKEL;ARTIKELNUMMER;EAN;PREIS_CENT;UST\n" +
            "Speisen;Menüs;=1+1 Menü;;4000000000017;450;19\n");
        await management.ImportArticlesCsvAsync(source, "tester");
        var exported = await management.ExportArticlesCsvAsync(Path.Combine(dir, "artikel.csv"));
        var exportText = await File.ReadAllTextAsync(exported);
        var copy = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "import.db"));
        var imported = await new BusinessManagementService(copy, new SettingsRepository(copy), new AuditLogRepository(copy))
            .ImportArticlesCsvAsync(exported, "tester");
        var back = (await new ProductRepository(copy).GetActiveProductsAsync()).SingleOrDefault(x => x.Barcode == "4000000000017");
        assert(
            exportText.Contains("\"'=1+1 Menü\"", StringComparison.Ordinal) && !exportText.Contains(";\"=1+1", StringComparison.Ordinal) &&
            imported >= 1 && back?.Name == "=1+1 Menü",
            $"CSV: the article export guards a formula-like name and the import restores it exactly ({back?.Name})");

        await audit.WriteAsync("=cmd|' /C calc'!A0", "TEST", "SALE", "1", "-2+3+cmd|' /C calc'!A0");
        var auditCsv = await File.ReadAllTextAsync(await audit.ExportCsvAsync(Path.Combine(dir, "audit.csv")));
        assert(
            auditCsv.Contains("\"'=cmd|", StringComparison.Ordinal) && auditCsv.Contains("\"'-2+3+cmd|", StringComparison.Ordinal) &&
            !auditCsv.Contains(";\"=cmd", StringComparison.Ordinal),
            "CSV: the audit log export guards formula-like actor names and details");

        // ------------------------------------------------ backup encryption
        var encryption = new BackupEncryptionService(settings);
        await settings.SaveManyAsync(new Dictionary<string, string> { ["backup.encryption.enabled"] = "true" });
        var plain = Path.Combine(dir, "plain-backup.db");
        await File.WriteAllTextAsync(plain, "backup");
        BackupNotEncryptedException? refused = null;
        try { await encryption.EncryptIfEnabledAsync(plain); }
        catch (BackupNotEncryptedException ex) { refused = ex; }
        assert(
            refused is not null && refused.PlainPath == plain && File.Exists(plain) &&
            refused.Message.Contains("NICHT verschlüsselt", StringComparison.Ordinal),
            "Backup: encryption switched on without a recovery key is reported as NOT encrypted, and the plain backup is kept, not lost");

        var backupDir = Path.Combine(dir, "daily");
        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["backup.daily.enabled"] = "true",
            ["backup.daily.time"] = "00:00",
            ["backup.directory"] = backupDir
        });
        var now = new DateTimeOffset(2026, 9, 27, 0, 5, 0, TimeSpan.FromHours(2));
        await using (var scheduler = new DailyBackupScheduler(new DatabaseBackupService(db), settings, () => now))
        {
            await scheduler.CheckNowAsync();
            var first = await settings.LoadAllAsync();
            now = now.AddMinutes(10);
            await scheduler.CheckNowAsync();
            assert(
                first["backup.daily.last_error"].Contains("NICHT verschlüsselt", StringComparison.Ordinal) &&
                File.Exists(first["backup.daily.last_path"]) && !first["backup.daily.last_path"].EndsWith(".tpe", StringComparison.Ordinal) &&
                DateTimeOffset.TryParse(first["backup.daily.last_success"], out _) &&
                Directory.GetFiles(backupDir, "*.db").Length == 1,
                "Backup: the daily backup keeps the unencrypted copy but shows the NOT-encrypted warning in Einstellungen/Diagnose, and does not pile up plain copies every five minutes");
        }
    }
}
