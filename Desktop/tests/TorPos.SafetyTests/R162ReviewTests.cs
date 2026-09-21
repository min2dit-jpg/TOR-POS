using TorPos.Core;
using TorPos.Infrastructure;

public static class R162ReviewTests
{
    public static async Task Run(string root, Action<bool, string> assert, Func<Func<Task>, string, Task> reject)
    {
        var dir = Path.Combine(root, "r162-staff");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "staff.db"));
        var audit = new AuditLogRepository(db);
        var auth = new AuthenticationService(db, audit);
        await auth.InitializeAsync();
        var staff = await auth.GetStaffUsersAsync();
        if (staff.Count < 2) throw new Exception("R162 fixture expected at least two staff slots.");
        var first = staff[0];
        await auth.SaveStaffUserAsync(new StaffUserUpdate(first.Id, "mitarbeiter1", true, UserPermissions.Sale | UserPermissions.ViewReceiptHistory, "MitarbeiterPasswort1", ""), "admin");
        var passwordLogin = await auth.LoginWithPasswordAsync("mitarbeiter1", "MitarbeiterPasswort1");
        assert(passwordLogin.Success && passwordLogin.User is { MustChangePassword: false } && passwordLogin.User.Can(UserPermissions.Sale), "R162 a fresh staff account can be activated and used with a password only");
        var oldDefaultPin = await auth.LoginWithPinAsync("mitarbeiter1", "1234");
        assert(!oldDefaultPin.Success, "R162 password-only activation replaces the untouched factory PIN so 1234 never remains a back door");
        await auth.SaveStaffUserAsync(new StaffUserUpdate(first.Id, "mitarbeiter1", true, UserPermissions.Sale | UserPermissions.ViewReceiptHistory, "", "4826"), "admin");
        var pinLogin = await auth.LoginWithPinAsync("mitarbeiter1", "4826");
        var passwordStillWorks = await auth.LoginWithPasswordAsync("mitarbeiter1", "MitarbeiterPasswort1");
        assert(pinLogin.Success && passwordStillWorks.Success, "R162 an optional PIN can be added later without replacing the existing password");
        var second = staff[1];
        await auth.SaveStaffUserAsync(new StaffUserUpdate(second.Id, "mitarbeiter2", true, UserPermissions.Sale, "", "5931"), "admin");
        var pinOnly = await auth.LoginWithPinAsync("mitarbeiter2", "5931");
        var oldDefaultPassword = await auth.LoginWithPasswordAsync("mitarbeiter2", "1234");
        assert(pinOnly.Success && !oldDefaultPassword.Success, "R162 PIN-only activation also disables the untouched factory password");
        await reject(() => auth.SaveStaffUserAsync(new StaffUserUpdate(second.Id, "mitarbeiter1", true, UserPermissions.Sale, "", ""), "admin"), "R162 duplicate staff usernames are rejected before the database UNIQUE constraint");
        var userWindow = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/UserManagementWindow.cs"));
        assert(userWindow.Contains("Zum Aktivieren reicht Passwort oder PIN.", StringComparison.Ordinal) && userWindow.Contains("Optional: 4 Ziffern. Leer lassen = Anmeldung nur per Passwort.", StringComparison.Ordinal), "R162 staff editor explains that PIN is optional when a password is used");
        var workflow = File.ReadAllText(FindRepoFile(".github/workflows/tor-pos-ci.yml"));
        var installer = File.ReadAllText(FindRepoFile("Desktop/TOR-POS-Pro-Setup.iss"));
        assert(workflow.Contains("Kunden Setup EXE olustur", StringComparison.Ordinal) && workflow.Contains("TOR-POS-Setup.exe", StringComparison.Ordinal) && installer.Contains("{commondesktop}\\{#MyAppName}", StringComparison.Ordinal) && installer.Contains("{app}\\{#MyAppExeName}", StringComparison.Ordinal), "R162 CI produces a real customer Setup EXE whose installer creates the configured TOR POS desktop shortcut");
    }

    private static string FindRepoFile(string relativePath)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate)) return candidate;
            }
        throw new FileNotFoundException($"R162 review could not locate repository file: {relativePath}");
    }
}
