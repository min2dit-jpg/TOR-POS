using TorPos.Infrastructure;

// O-1: the print journal kept every finished job forever, one damaged file
// blocked the whole journal, and an unclear kitchen-printer job also blocked
// receipts on the main printer.
public static class O1PrintJournalTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "o1-print-journal");
        var journal = new PrintJobJournal(dir);
        var laneJob = Guid.NewGuid().ToString("N");
        var mainJob = Guid.NewGuid().ToString("N");
        await journal.SaveAsync(new PrintJobRecord(laneJob, "UNKNOWN", "O1-KUECHE", null, null, "", IsolatedLane: true));
        await journal.SaveAsync(new PrintJobRecord(mainJob, "UNKNOWN", "O1-BON", null, null, ""));

        await using (var main = new StarMcPrint3PrinterService(journal, TimeSpan.FromSeconds(1), () => Task.CompletedTask))
        await using (var lane = new StarMcPrint3PrinterService(journal, TimeSpan.FromSeconds(1), () => Task.CompletedTask, "O1-KUECHE"))
        {
            var mainOpen = await main.GetUncertainJobsAsync();
            var laneOpen = await lane.GetUncertainJobsAsync();
            assert(
                mainOpen.Count == 1 && mainOpen[0].Id == mainJob &&
                laneOpen.Count == 1 && laneOpen[0].Id == laneJob,
                "O-1 an unclear kitchen-printer job is reviewed on its own lane and no longer blocks receipts on the main printer");
        }

        var broken = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(dir, broken + ".json"), "{ kaputt");
        IReadOnlyList<PrintJobRecord>? first = null;
        try { first = await journal.GetUncertainAsync(); } catch (Exception) { }
        var second = await journal.GetUncertainAsync();
        assert(
            first is not null &&
            first.Any(x => x.Id == broken && x.State == "UNKNOWN") &&
            second.All(x => x.Id != broken) &&
            File.Exists(Path.Combine(dir, PrintJobJournal.QuarantineFolder, broken + ".json")),
            "O-1 a damaged journal file is moved to quarantine and reported once for a paper check instead of making the whole journal unreadable");

        var oldDone = Guid.NewGuid().ToString("N");
        var oldReviewed = Guid.NewGuid().ToString("N");
        var freshDone = Guid.NewGuid().ToString("N");
        await journal.SaveAsync(new PrintJobRecord(oldDone, "SPOOL_ACCEPTED", "O1-BON", null, null, ""));
        await journal.SaveAsync(new PrintJobRecord(oldReviewed, "REVIEWED", "O1-BON", null, null, "Papier geprüft"));
        await journal.SaveAsync(new PrintJobRecord(freshDone, "SPOOL_ACCEPTED", "O1-BON", null, null, ""));
        var old = DateTime.UtcNow.AddDays(-40);
        foreach (var id in new[] { oldDone, oldReviewed, mainJob })
            File.SetLastWriteTimeUtc(Path.Combine(dir, id + ".json"), old);

        var removed = await journal.CleanupFinishedAsync(DateTimeOffset.Now);
        assert(
            removed == 2 &&
            await journal.GetAsync(oldDone) is null &&
            await journal.GetAsync(oldReviewed) is null &&
            await journal.GetAsync(freshDone) is not null &&
            await journal.GetAsync(mainJob) is not null,
            "O-1 finished print jobs older than 30 days are removed; recent ones and unclear jobs of any age stay");
    }
}
