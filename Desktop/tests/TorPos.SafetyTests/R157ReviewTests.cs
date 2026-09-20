using TorPos.App;

public static class R157ReviewTests
{
    public static Task Run(Action<bool, string> assert)
    {
        var mainAxaml = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml"));
        var mainCode = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"));
        var project = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/TorPos.App.csproj"));
        var payment = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/PaymentChoiceWindow.cs"));
        var logo = FindRepoFile("Desktop/src/TorPos.App/Assets/TorPos-Brand.jpg");

        assert(
            File.Exists(logo) &&
            new FileInfo(logo).Length > 1000 &&
            project.Contains("AvaloniaResource Include=\"Assets\\TorPos-Brand.jpg\"", StringComparison.Ordinal),
            "R157 the supplied TOR POS logo is a real bundled Avalonia resource, not a placeholder");

        assert(
            mainAxaml.Contains("avares://TorPos.App/Assets/TorPos-Brand.jpg", StringComparison.Ordinal) &&
            !mainAxaml.Contains("Text=\"TOR\" Foreground=\"#07140F\"", StringComparison.Ordinal),
            "R157 the cashier header uses the new TOR POS image instead of the old green TOR text badge");

        assert(
            mainCode.Contains("CompanyNameText.Text=\"TOR-POS\";", StringComparison.Ordinal),
            "R157 the brand beside the logo is TOR-POS for both Einzelhandel and Gastronomie instead of a mode/company label");

        assert(
            payment.Contains("SectionTitle(\"VERKAUFSART\")", StringComparison.Ordinal) &&
            payment.Contains("Columns = 2", StringComparison.Ordinal) &&
            payment.Contains("SectionTitle(\"ZAHLART\")", StringComparison.Ordinal) &&
            payment.Contains("Columns = 3", StringComparison.Ordinal) &&
            payment.Contains("HorizontalAlignment = HorizontalAlignment.Stretch", StringComparison.Ordinal),
            "R157 payment UI groups two equal service-type choices above three equal payment choices and keeps actions aligned");

        assert(
            payment.Contains("AUSSER HAUS\\nSTANDARD", StringComparison.Ordinal) &&
            payment.Contains("ChoiceButton(\"IM HAUS\")", StringComparison.Ordinal) &&
            payment.Contains("GEMISCHT\\nBAR + KARTE", StringComparison.Ordinal) &&
            payment.Contains("IsEnabled = enabled", StringComparison.Ordinal) &&
            payment.Contains("Content = \"ABBRECHEN\"", StringComparison.Ordinal),
            "R157 payment cleanup preserves AUSSER HAUS/IM HAUS, BAR/KARTE/GEMISCHT enablement and cancel behavior");

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

        throw new FileNotFoundException($"R157 review could not locate repository file: {relativePath}");
    }
}
