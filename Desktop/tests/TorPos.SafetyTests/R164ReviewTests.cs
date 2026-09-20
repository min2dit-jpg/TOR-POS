public static class R164ReviewTests
{
    public static Task Run(Action<bool, string> assert)
    {
        var window = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.App/UserManagementWindow.cs"));
        var snapshot = File.ReadAllText(
            FindRepoFile("Desktop/tools/TorPos.UiSnapshot/Program.cs"));

        assert(
            window.Contains("private TextBox _adminCurrentPassword = null!;", StringComparison.Ordinal) &&
            window.Contains("private TextBox _adminNewPassword = null!;", StringComparison.Ordinal) &&
            window.Contains("private TextBox _adminNewPin = null!;", StringComparison.Ordinal),
            "R164 admin credential fields are no longer permanent controls reused across tab reloads");

        assert(
            window.Contains("_adminCurrentPassword = new TextBox", StringComparison.Ordinal) &&
            window.Contains("_adminNewPassword = new TextBox", StringComparison.Ordinal) &&
            window.Contains("_adminNewPin = new TextBox", StringComparison.Ordinal),
            "R164 every admin-tab rebuild creates fresh TextBox instances");

        assert(
            snapshot.Contains("SnapshotUserManagementAsync", StringComparison.Ordinal) &&
            snapshot.Contains("second load", StringComparison.Ordinal) &&
            snapshot.Contains("Laden fehlgeschlagen:", StringComparison.Ordinal) &&
            snapshot.Contains("9 dialogs", StringComparison.Ordinal),
            "R164 CI opens the employee window and reloads it twice to catch visual-parent regressions");

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
