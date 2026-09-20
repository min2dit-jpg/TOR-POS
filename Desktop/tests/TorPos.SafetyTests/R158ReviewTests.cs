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
            axaml.Contains("Grid.Row=\"1\" ColumnDefinitions=\"1.05*,1*,1.15*\"", StringComparison.Ordinal) &&
            axaml.Contains("Grid.Row=\"1\" ColumnDefinitions=\"1.18*,1*,0.86*\"", StringComparison.Ordinal),
            "R158 Einstellungen and Kasse card rows occupy the remaining vertical space");

        assert(
            axaml.Contains("Grid.Row=\"1\" ColumnDefinitions=\"*,*,*\" RowDefinitions=\"*,*\"", StringComparison.Ordinal),
            "R158 Berichte uses two equal-height rows so upper and lower cards align cleanly to the bottom");

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
