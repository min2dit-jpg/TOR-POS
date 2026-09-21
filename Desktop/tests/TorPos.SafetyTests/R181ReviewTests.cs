public static class R181ReviewTests
{
    public static Task Run(Action<bool,string> assert)
    {
        var main = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"));
        var infra = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Infrastructure/Infrastructure.cs"));
        var settings = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/SettingsWindow.axaml.cs"));

        assert(
            main.Contains("private readonly Queue<string> _barcodeQueue = new();", StringComparison.Ordinal) &&
            main.Contains("DrainBarcodeQueueAsync", StringComparison.Ordinal) &&
            main.Contains("_barcodeQueue.Enqueue(code.Trim())", StringComparison.Ordinal),
            "R181 rapid scans are buffered in a FIFO queue instead of being dropped while one barcode is processing");

        assert(
            !main.Contains("_scan.Length < 6 || _scanProcessing", StringComparison.Ordinal) &&
            !main.Contains("_scan.Length >= 6 && gap < maxSuffixGap && !_scanProcessing", StringComparison.Ordinal) &&
            !main.Contains("hasSuffix && _scan.Length >= 6 && !_scanProcessing", StringComparison.Ordinal),
            "R181 capture completion no longer rejects a second scan merely because the previous barcode is still processing");

        assert(
            infra.Contains("('scanner.wait_ms','140')", StringComparison.Ordinal) &&
            main.Contains("[\"scanner.wait_ms\"] = \"140\"", StringComparison.Ordinal) &&
            main.Contains("scanner.r181_latency_migrated", StringComparison.Ordinal) &&
            main.Contains("Math.Max(140, configured)", StringComparison.Ordinal),
            "R181 suffix-less scanner latency defaults/migrates from the legacy 1000 ms delay to 140 ms");

        assert(
            main.Contains("_barcodeQueue.Count >= 64", StringComparison.Ordinal) &&
            main.Contains("SCANNER-WARTESCHLANGE VOLL", StringComparison.Ordinal) &&
            settings.Contains("RAM-Index", StringComparison.Ordinal),
            "R181 scanner queue is bounded and keeps the existing in-memory barcode lookup contract");

        return Task.CompletedTask;
    }

    private static string FindRepoFile(string relative)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException(relative);
    }
}
