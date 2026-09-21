using TorPos.Core;

public static class R180ReviewTests
{
    public static Task Run(string root, Action<bool,string> assert)
    {
        var main = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"));
        var settings = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/SettingsWindow.axaml.cs"));
        var infra = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Infrastructure/Infrastructure.cs"));

        assert(
            main.Contains("EAN NICHT GEFUNDEN", StringComparison.Ordinal) &&
            main.Contains("ScannerCapture.Text = code", StringComparison.Ordinal),
            "R180 unknown barcode remains visible on the cashier screen");

        assert(
            !main.Contains("new ProductEditorWindow(", StringComparison.Ordinal) ||
            !main.Contains("scanner.unknown_dialog", StringComparison.Ordinal),
            "R180 scanner no longer opens Stammdaten/ProductEditor automatically for unknown EAN");

        assert(
            main.Contains("var typed = (ScannerCapture.Text ?? \"\").Trim();", StringComparison.Ordinal) &&
            main.Contains("await ProcessBarcodeSafely(typed);", StringComparison.Ordinal),
            "R180 EAN SUCHEN uses the visible cashier barcode field directly when it contains a numeric EAN");

        assert(
            !settings.Contains("scanner.unknown_dialog", StringComparison.Ordinal) &&
            !infra.Contains("('scanner.unknown_dialog','true')", StringComparison.Ordinal),
            "R180 removes the obsolete unknown-barcode auto-editor setting from UI/defaults");

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
