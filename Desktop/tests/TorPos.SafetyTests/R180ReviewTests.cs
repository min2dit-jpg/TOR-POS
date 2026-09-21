public static class R180ReviewTests
{
    public static Task Run(Action<bool,string> assert)
    {
        var mainXaml = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml"));
        var mainSource = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"));
        var settingsSource = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/SettingsWindow.axaml.cs"));

        var processStart = mainSource.IndexOf("private async Task ProcessBarcode(string code)", StringComparison.Ordinal);
        var processEnd = processStart < 0
            ? -1
            : mainSource.IndexOf("private async void OnEanSearchClick", processStart, StringComparison.Ordinal);
        var processBlock = processStart >= 0 && processEnd > processStart
            ? mainSource[processStart..processEnd]
            : "";

        assert(
            processBlock.Contains("EAN NICHT GEFUNDEN", StringComparison.Ordinal) &&
            processBlock.Contains("Artikel unter WAREN → Stammdaten prüfen.", StringComparison.Ordinal) &&
            !processBlock.Contains("new ProductEditorWindow", StringComparison.Ordinal),
            "R180 an unknown scan stays on the cashier screen and never opens Stammdaten automatically");

        assert(
            mainSource.Contains("if (ScannerCapture.IsFocused)", StringComparison.Ordinal) &&
            mainSource.Contains("TextInput is", StringComparison.Ordinal) &&
            mainSource.Contains("single authoritative barcode stream", StringComparison.Ordinal),
            "R180 focused scanner capture does not append the same HID digit through both KeyDown and TextInput");

        assert(
            mainXaml.Contains("x:Name="ScannerCapture"", StringComparison.Ordinal) &&
            mainXaml.Contains("CaretBrush="Transparent"", StringComparison.Ordinal),
            "R180 cashier scanner keeps HID focus without a permanently blinking visible caret");

        assert(
            settingsSource.Contains("Scanner bleibt immer auf dem Verkaufsbildschirm", StringComparison.Ordinal) &&
            !settingsSource.Contains("scanner.unknown_dialog", StringComparison.Ordinal),
            "R180 scanner settings no longer expose the obsolete automatic unknown-barcode dialog behavior");

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
