// A second click on Z-Bericht (hidden ZReportButton or the menu tile) while
// the first Z is still archiving queued behind it and archived a second, empty
// Z for the new period. Both entry points now share one run.
public static class ZReportSingleRunTests
{
    public static void Run(Action<bool, string> assert)
    {
        var main = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"));
        var xaml = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml"));
        var start = main.IndexOf("private async void OnZReportClick(", StringComparison.Ordinal);
        var end = start < 0 ? -1 : main.IndexOf("private async Task RunZReportAsync()", start, StringComparison.Ordinal);
        var click = start < 0 || end < 0 ? "" : main[start..end];
        var guard = click.IndexOf("if (_zReportRunning)", StringComparison.Ordinal);
        var set = click.IndexOf("_zReportRunning = true;", StringComparison.Ordinal);
        var run = click.IndexOf("await RunZReportAsync();", StringComparison.Ordinal);
        var reset = click.IndexOf("_zReportRunning = false;", StringComparison.Ordinal);
        var entries = System.Text.RegularExpressions.Regex.Matches(xaml, "Click=\"OnZReportClick\"").Count;
        assert(
            guard > 0 && set > guard && run > set && reset > run &&
            click.IndexOf("finally", run, StringComparison.Ordinal) is var fin && fin > run && fin < reset &&
            click.Contains("new[] { ZReportButton, MenuZReportButton }", StringComparison.Ordinal) &&
            entries == 2 && xaml.Contains("x:Name=\"MenuZReportButton\"", StringComparison.Ordinal) &&
            main.Split("CreateZArchiveAsync(").Length == 2 &&
            main.IndexOf("CreateZArchiveAsync(", StringComparison.Ordinal) > main.IndexOf("private async Task RunZReportAsync()", StringComparison.Ordinal),
            "Z-Bericht: both Z buttons share one run - a second click while the first Z is archiving is refused, the buttons are disabled meanwhile, and the lock is released in finally");
    }

    private static string FindRepoFile(string relativePath)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relativePath);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        throw new FileNotFoundException(relativePath);
    }
}
