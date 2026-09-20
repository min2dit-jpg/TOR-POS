public static class R158ReviewTests
{
    public static Task Run(Action<bool, string> assert)
    {
        var axaml = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml"));

        assert(
            axaml.Contains("VerticalContentAlignment=\"Stretch\"", StringComparison.Ordinal),
            "R158 the menu workspace stretches its content to the available viewport height");

        assert(
            axaml.Contains("x:Name=\"GoodsHubPanel\" IsVisible=\"False\" RowDefinitions=\"Auto,*\"", StringComparison.Ordinal) &&
            axaml.Contains("x:Name=\"SettingsHubPanel\" IsVisible=\"False\" RowDefinitions=\"Auto,*\"", StringComparison.Ordinal) &&
            axaml.Contains("x:Name=\"CashHubPanel\" IsVisible=\"False\" RowDefinitions=\"Auto,*\"", StringComparison.Ordinal) &&
            axaml.Contains("x:Name=\"ReportsHubPanel\" IsVisible=\"False\" RowDefinitions=\"Auto,*\"", StringComparison.Ordinal),
            "R158 Waren, Einstellungen, Kasse and Berichte use header-plus-fill grids instead of content-height stack panels");

        assert(
            axaml.Contains("Grid.Row=\"1\" ColumnDefinitions=\"240,*\"", StringComparison.Ordinal) &&
            axaml.Split("Grid.Row=\"1\" ColumnDefinitions=\"230,*\"", StringSplitOptions.None).Length - 1 >= 3,
            "R158 the fill row still occupies the remaining vertical space after the R159 sidebar redesign");

        assert(
            axaml.Contains("Classes=\"hubdetail\"", StringComparison.Ordinal) &&
            axaml.Contains("x:Name=\"ReportsFiscalPanel\"", StringComparison.Ordinal),
            "R158 the Berichte workspace still stretches through a wide detail surface instead of shrinking to content height");

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

        throw new FileNotFoundException($"R158 review could not locate repository file: {relativePath}");
    }
}
