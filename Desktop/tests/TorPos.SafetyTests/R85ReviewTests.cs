using TorPos.App;

// R85: cashier-facing error text was split from raw technical exception detail.
// ScannerStatus now shows only a category plus a Fehler-ID; the full exception
// goes exclusively to CrashLog, retrievable later by a technician through
// CrashLog.FindErrorId. Since R182 CrashLog.LogDirectory follows the product
// identity of the running build (%LocalAppData%\<Produkt>\Logs); the suite runs
// the shared build, so it still resolves to the real TOR POS Pro folder. Unlike
// every other fixture in
// this suite these checks write a uniquely-named session log directly into
// that real, shared directory and remove it again in a finally block - the
// same directory production sessions and other test runs also write to, so
// the file name and search needle must both be collision-proof.
public static class R85ReviewTests
{
    public static Task Run(Action<bool, string> assert, Func<Func<Task>, string, Task> reject)
    {
        Directory.CreateDirectory(CrashLog.LogDirectory);
        var logPath = Path.Combine(
            CrashLog.LogDirectory,
            $"TOR-POS-R85TEST-{Guid.NewGuid():N}.log");

        try
        {
            var knownId = "R85TEST-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var missingId = "R85TEST-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

            File.WriteAllText(
                logPath,
                $"[2026-09-14T12:00:00.0000000+02:00] ERROR-ID={knownId} CATEGORY=TEST MESSAGE=Testfehler{Environment.NewLine}" +
                $"System.InvalidOperationException: boom{Environment.NewLine}" +
                $"   at TorPos.App.Somewhere(){Environment.NewLine}" +
                $"[2026-09-14T12:00:01.0000000+02:00] Unrelated later log line that must not be captured.{Environment.NewLine}");

            var found = CrashLog.FindErrorId(knownId);
            assert(
                found != null &&
                found.Contains("ERROR-ID=" + knownId) &&
                found.Contains("System.InvalidOperationException: boom") &&
                found.Contains("at TorPos.App.Somewhere()") &&
                !found.Contains("Unrelated later log line"),
                "R85 a technician lookup by Fehler-ID returns the full exception block and stops before the next log entry");

            assert(
                CrashLog.FindErrorId(missingId) == null,
                "R85 a Fehler-ID with no matching log entry returns no result instead of a false match");

            assert(
                CrashLog.FindErrorId("   ") == null,
                "R85 a blank Fehler-ID search is refused instead of scanning every log file");

            var lowerFound = CrashLog.FindErrorId(knownId.ToLowerInvariant());
            assert(
                lowerFound != null && lowerFound.Contains("ERROR-ID=" + knownId),
                "R85 a Fehler-ID lookup is not case-sensitive, matching how it is read aloud or retyped");
        }
        finally
        {
            try { File.Delete(logPath); } catch { }
        }

        return Task.CompletedTask;
    }
}
