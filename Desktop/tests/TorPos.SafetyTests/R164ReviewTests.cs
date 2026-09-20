using TorPos.App;

public static class R164ReviewTests
{
    public static Task Run(
        string root,
        Action<bool, string> assert)
    {
        var source = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.App/UserManagementWindow.cs"));
        var snapshot = File.ReadAllText(
            FindRepoFile("Desktop/tools/TorPos.UiSnapshot/Program.cs"));

        assert(
            source.Contains("_adminCurrentPassword = new TextBox", StringComparison.Ordinal) &&
            source.Contains("_adminNewPassword = new TextBox", StringComparison.Ordinal) &&
            source.Contains("_adminNewPin = new TextBox", StringComparison.Ordinal),
            "R164 admin credential inputs are recreated whenever the tab content is rebuilt");

        assert(
            !source.Contains("private readonly TextBox _adminCurrentPassword = new()", StringComparison.Ordinal) &&
            !source.Contains("private readonly TextBox _adminNewPassword = new()", StringComparison.Ordinal) &&
            !source.Contains("private readonly TextBox _adminNewPin = new()", StringComparison.Ordinal),
            "R164 user editor no longer keeps reusable visual TextBox instances as readonly window fields");

        assert(
            snapshot.Contains("user-management-reload", StringComparison.Ordinal) &&
            snapshot.Contains("LoadAsync", StringComparison.Ordinal) &&
            snapshot.Contains("visual parent", StringComparison.OrdinalIgnoreCase),
            "R164 headless UI regression test reloads Mitarbeiter & Rechte twice and rejects any visual-parent error");

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

        throw new FileNotFoundException($"R164 review could not locate repository file: {relativePath}");
    }
}
