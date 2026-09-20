public static class R160ReviewTests
{
    public static Task Run(Action<bool, string> assert)
    {
        var axaml = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml"));

        assert(
            axaml.Contains("<UniformGrid Rows=\"3\" Columns=\"1\"", StringComparison.Ordinal) &&
            axaml.Split("<UniformGrid Rows=\"3\" Columns=\"1\"", StringSplitOptions.None).Length - 1 >= 2,
            "R160 Waren and Kasse sidebars divide the full panel height into three equal navigation cards");

        assert(
            axaml.Contains("<UniformGrid Rows=\"5\" Columns=\"1\"", StringComparison.Ordinal),
            "R160 Berichte divides the full sidebar height into five equal navigation cards");

        assert(
            axaml.Contains("<UniformGrid Rows=\"11\" Columns=\"1\"", StringComparison.Ordinal) &&
            !axaml.Contains("<ScrollViewer VerticalScrollBarVisibility=\"Auto\">\n                    <StackPanel>\n                      <Button x:Name=\"SettingsNavCash\"", StringComparison.Ordinal),
            "R160 Einstellungen distributes all eleven sections proportionally across the sidebar instead of leaving unused space");

        assert(
            axaml.Contains("<Setter Property=\"MinHeight\" Value=\"0\"/>", StringComparison.Ordinal) &&
            axaml.Contains("<Setter Property=\"VerticalAlignment\" Value=\"Stretch\"/>", StringComparison.Ordinal) &&
            axaml.Contains("<Setter Property=\"FontSize\" Value=\"15\"/>", StringComparison.Ordinal),
            "R160 sidebar cards stretch vertically and use the larger readable 15px label size");

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

        throw new FileNotFoundException($"R160 review could not locate repository file: {relativePath}");
    }
}
