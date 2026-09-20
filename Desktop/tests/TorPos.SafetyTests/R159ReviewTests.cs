public static class R159ReviewTests
{
    public static Task Run(Action<bool, string> assert)
    {
        var axaml = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml"));
        var mainCode = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"));
        var workflow = File.ReadAllText(FindRepoFile(".github/workflows/tor-pos-ci.yml"));

        assert(
            axaml.Contains("Grid.Row=\"1\" ColumnDefinitions=\"270,*\"", StringComparison.Ordinal) &&
            axaml.Contains("SettingsNavCash", StringComparison.Ordinal) &&
            axaml.Contains("SettingsDetailTitle", StringComparison.Ordinal) &&
            axaml.Contains("<Setter Property=\"FontSize\" Value=\"14\"/>", StringComparison.Ordinal) &&
            axaml.Contains("<Setter Property=\"FontSize\" Value=\"27\"/>", StringComparison.Ordinal),
            "R159 Einstellungen uses a wider site-style sidebar, one wide detail panel and till-readable typography");

        assert(
            axaml.Contains("GoodsNavArticles", StringComparison.Ordinal) &&
            axaml.Contains("CashNavOperation", StringComparison.Ordinal) &&
            axaml.Contains("ReportsNavDaily", StringComparison.Ordinal) &&
            axaml.Contains("<Setter Property=\"MinHeight\" Value=\"62\"/>", StringComparison.Ordinal) &&
            axaml.Contains("<Setter Property=\"HorizontalContentAlignment\" Value=\"Stretch\"/>", StringComparison.Ordinal),
            "R159 Waren, Kasse and Berichte use the same sidebar/detail pattern with full-width action cards");

        assert(
            mainCode.Contains("ShowSettingsHubSection", StringComparison.Ordinal) &&
            mainCode.Contains("ShowGoodsHubSection", StringComparison.Ordinal) &&
            mainCode.Contains("ShowCashHubSection", StringComparison.Ordinal) &&
            mainCode.Contains("ShowReportsHubSection", StringComparison.Ordinal),
            "R159 sidebar buttons switch wide content panels instead of opening a wall of small cards");

        assert(
            workflow.Contains("-p:PublishSingleFile=true", StringComparison.Ordinal) &&
            workflow.Contains("-p:IncludeNativeLibrariesForSelfExtract=true", StringComparison.Ordinal) &&
            workflow.Contains("Copy-Item $sourceExe ci-results/package/TOR-POS.exe", StringComparison.Ordinal),
            "R159 CI builds a self-contained single-file Windows package with TOR-POS.exe at the root");

        assert(
            workflow.Contains("Package root must contain only TOR-POS.exe.", StringComparison.Ordinal) &&
            workflow.Contains("ci-results/package/**", StringComparison.Ordinal),
            "R159 download artifact hides runtime clutter and exposes only TOR-POS.exe plus documentation");

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

        throw new FileNotFoundException($"R159 review could not locate repository file: {relativePath}");
    }
}
