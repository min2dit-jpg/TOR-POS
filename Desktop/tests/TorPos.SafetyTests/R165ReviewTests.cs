public static class R165ReviewTests
{
    public static Task Run(Action<bool, string> assert)
    {
        var main = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"));

        assert(
            main.Contains("InputElement.TextInputEvent", StringComparison.Ordinal) &&
            main.Contains("OnGlobalScannerTextInput", StringComparison.Ordinal),
            "R165 cashier window captures scanner data through TextInput, matching the path that works in article text fields");

        assert(
            main.Contains("e.Key is not (Key.Enter or Key.Tab)", StringComparison.Ordinal) &&
            main.Contains("SCAN ERKANNT", StringComparison.Ordinal),
            "R165 cashier scanner accepts both ENTER and TAB suffixes and visibly confirms a recognized scan");

        assert(
            main.Contains("foreach (var ch in text)", StringComparison.Ordinal) &&
            main.Contains("char.IsDigit(ch)", StringComparison.Ordinal) &&
            main.Contains("scanner.enter_suffix", StringComparison.Ordinal),
            "R165 TextInput capture accepts keyboard-wedge digit streams and still supports scanners without a suffix");

        assert(
            main.Contains("await _catalog.ReloadAsync();", StringComparison.Ordinal) &&
            main.Contains("_catalog.TryGetByBarcode(code, out p);", StringComparison.Ordinal),
            "R165 barcode miss refreshes the product catalog once so newly added articles scan without restarting TOR POS");

        assert(
            main.Contains("EAN NICHT GEFUNDEN", StringComparison.Ordinal) &&
            main.Contains("ScannerStatus.Foreground = AppTheme.WarningAmber", StringComparison.Ordinal) &&
            main.Contains("SCAN OK", StringComparison.Ordinal),
            "R165 every completed scan gives clear success or not-found feedback instead of failing silently");

        return Task.CompletedTask;
    }

    private static string FindRepoFile(string relativePath)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(
                    dir.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        throw new FileNotFoundException($"R165 review could not locate repository file: {relativePath}");
    }
}
