using System.Security.Cryptography;

public static class R161ReviewTests
{
    private const string ExpectedIconSha256 =
        "DEF9E50913D39CD2F3D019F95337F127A513F10B72AD8915827D7D212A073278";

    public static Task Run(Action<bool, string> assert)
    {
        var login = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/LoginWindow.axaml"));
        var startup = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/StartupDiagnostics.cs"));
        var snapshot = File.ReadAllText(FindRepoFile("Desktop/tools/TorPos.UiSnapshot/Program.cs"));
        var project = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/TorPos.App.csproj"));
        var iconPath = FindRepoFile("Desktop/src/TorPos.App/Assets/TorPos.ico");

        assert(
            login.Contains("Source=\"/Assets/TorPos-Brand.jpg\"", StringComparison.Ordinal) &&
            login.Contains("Text=\"TOR-POS\"", StringComparison.Ordinal) &&
            !login.Contains("<TextBlock Text=\"TOR\"", StringComparison.Ordinal),
            "R161 login screen uses the new TOR POS brand image and no longer renders the old green TOR placeholder");

        assert(
            startup.Contains("avares://{assetAssembly}/Assets/TorPos-Brand.jpg", StringComparison.Ordinal) &&
            startup.Contains("AssetLoader.Open", StringComparison.Ordinal) &&
            !startup.Contains("TorPos-Magnifier.png", StringComparison.Ordinal) &&
            startup.Contains("Text = \"TOR POS\"", StringComparison.Ordinal),
            "R161 startup splash loads the bundled TOR POS brand image instead of the legacy magnifier");

        var iconHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(iconPath)));
        assert(
            project.Contains("<ApplicationIcon>Assets\\TorPos.ico</ApplicationIcon>", StringComparison.Ordinal) &&
            iconHash == ExpectedIconSha256,
            "R161 Windows executable/title-bar icon is the newly generated TOR POS icon");

        assert(
            snapshot.Contains("new StartupLoadingWindow()", StringComparison.Ordinal) &&
            snapshot.Contains("new LoginWindow(auth, settings)", StringComparison.Ordinal) &&
            snapshot.Contains("LAYOUT CHECK PASSED ({sizes.Count} sizes, 10 dialogs, language {language})", StringComparison.Ordinal),
            "R161 CI captures splash and login as visual-regression dialogs, in the language it measured");

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

        throw new FileNotFoundException($"R161 review could not locate repository file: {relativePath}");
    }
}
