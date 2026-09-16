using TorPos.Infrastructure;

public static class R57ReviewTests
{
    public static async Task Run(string root, Action<bool,string> check)
    {
        var dbPath = Path.Combine(root, "r57.db");
        var db = await SafetyDatabase.CreateCurrentAsync(dbPath);
        var settings = new SettingsRepository(db);
        var values = await settings.LoadAllAsync();

        check(values.TryGetValue("backup.daily.enabled", out var enabled) && enabled == "true", "R57 daily backup is enabled by default");
        check(values.TryGetValue("backup.daily.time", out var time) && time == "00:00", "R57 daily backup defaults to 00:00");
        check(values.TryGetValue("backup.daily.last_success", out var last) && last == "", "R57 daily backup starts without a false success marker");
        check(values.TryGetValue("backup.daily.last_error", out var error) && error == "", "R57 daily backup starts without a false error marker");
        check(values.TryGetValue("order_display.enabled", out var display) && display == "false", "R57 order display is opt-in and separate from customer display");
        check(values.TryGetValue("order_display.screen_index", out var screen) && screen == "0", "R57 order display defaults to automatic second screen");
        check(values.TryGetValue("order_display.refresh_seconds", out var refresh) && refresh == "2", "R57 order display refresh defaults to two seconds");

        var backupDir = Path.Combine(root, "r57-backups");
        await settings.SaveManyAsync(new Dictionary<string,string>
        {
            ["backup.daily.enabled"] = "true",
            ["backup.daily.time"] = "00:00",
            ["backup.directory"] = backupDir
        });

        var now = new DateTimeOffset(2026, 9, 10, 0, 1, 0, TimeSpan.FromHours(2));
        await using var scheduler = new DailyBackupScheduler(new DatabaseBackupService(db), settings, () => now);
        await scheduler.CheckNowAsync();
        values = await settings.LoadAllAsync();
        check(DateTimeOffset.TryParse(values["backup.daily.last_success"], out _), "R57 scheduled backup writes last-success timestamp only after success");
        check(File.Exists(values["backup.daily.last_path"]), "R57 scheduled backup stores the successful backup path");
        check(values["backup.daily.last_error"] == "", "R57 successful scheduled backup clears previous error state");
        check(Directory.GetFiles(backupDir, "*.db").Length == 1, "R57 scheduled backup creates one database copy");

        await scheduler.CheckNowAsync();
        check(Directory.GetFiles(backupDir, "*.db").Length == 1, "R57 does not repeat a successful scheduled backup on the same day");

        now = now.AddDays(1);
        await scheduler.CheckNowAsync();
        check(Directory.GetFiles(backupDir, "*.db").Length == 2, "R57 creates the next daily backup on the next due day");

        var successBeforeFailure = (await settings.LoadAllAsync())["backup.daily.last_success"];
        var impossibleDirectory = Path.Combine(root, "not-a-directory");
        await File.WriteAllTextAsync(impossibleDirectory, "file blocks directory creation");
        await settings.SaveManyAsync(new Dictionary<string,string> { ["backup.directory"] = impossibleDirectory });
        now = now.AddDays(1);
        await scheduler.CheckNowAsync();
        values = await settings.LoadAllAsync();
        check(!string.IsNullOrWhiteSpace(values["backup.daily.last_error"]), "R57 records inaccessible backup destination as an error");
        check(values["backup.daily.last_success"] == successBeforeFailure, "R57 failure never writes a false success marker");

        await settings.SaveManyAsync(new Dictionary<string,string> { ["backup.directory"] = backupDir });
        await scheduler.CheckNowAsync();
        check(Directory.GetFiles(backupDir, "*.db").Length == 2, "R57 waits instead of immediate retry after a backup failure");

        now = now.AddMinutes(4).AddSeconds(59);
        await scheduler.CheckNowAsync();
        check(Directory.GetFiles(backupDir, "*.db").Length == 2, "R57 still waits before the five-minute retry point");

        now = now.AddSeconds(2);
        await scheduler.CheckNowAsync();
        values = await settings.LoadAllAsync();
        check(Directory.GetFiles(backupDir, "*.db").Length == 3 && values["backup.daily.last_error"] == "", "R57 retries after five minutes and clears the error on success");

        var due = DailyBackupScheduler.MostRecentDue(
            new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.FromHours(2)),
            new TimeOnly(22, 0));
        check(due.Date == new DateTime(2026, 9, 11), "R57 catch-up logic targets the last missed scheduled occurrence");
    }
}
