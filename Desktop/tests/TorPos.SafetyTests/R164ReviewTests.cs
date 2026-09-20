using Avalonia;
using Avalonia.Controls;
using TorPos.Core;
using TorPos.Infrastructure;

public static class R164ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "r164-user-editor");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "users.db"));
        var audit = new AuditLogRepository(db);
        var auth = new AuthenticationService(db, audit);
        await auth.InitializeAsync();

        var login = await auth.LoginWithPasswordAsync("admin", "admin");
        if (!login.Success || login.User is null)
            throw new Exception("R164 fixture could not authenticate admin.");

        var window = new UserManagementWindow(auth, login.User);

        var load = typeof(UserManagementWindow).GetMethod(
            "LoadAsync",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)
            ?? throw new Exception("R164 could not find UserManagementWindow.LoadAsync.");

        async Task InvokeLoadAsync()
        {
            var task = (Task?)load.Invoke(window, null)
                ?? throw new Exception("R164 LoadAsync did not return Task.");
            await task;
        }

        await InvokeLoadAsync();
        await InvokeLoadAsync();

        var statusField = typeof(UserManagementWindow).GetField(
            "_status",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)
            ?? throw new Exception("R164 could not find status field.");
        var status = (TextBlock?)statusField.GetValue(window);

        assert(
            status?.Text?.Contains("geladen", StringComparison.OrdinalIgnoreCase) == true &&
            !status.Text.Contains("visual parent", StringComparison.OrdinalIgnoreCase),
            "R164 Mitarbeiter & Rechte can reload twice without reusing TextBox controls across visual parents");

        var source = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.App/UserManagementWindow.cs"));

        assert(
            source.Contains("_adminCurrentPassword = new TextBox", StringComparison.Ordinal) &&
            source.Contains("_adminNewPassword = new TextBox", StringComparison.Ordinal) &&
            source.Contains("_adminNewPin = new TextBox", StringComparison.Ordinal),
            "R164 admin credential inputs are recreated whenever the tab content is rebuilt");

        assert(
            !source.Contains("private readonly TextBox _adminCurrentPassword = new()", StringComparison.Ordinal),
            "R164 user editor no longer keeps visual TextBox instances as reusable readonly window fields");

        window.Close();
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
