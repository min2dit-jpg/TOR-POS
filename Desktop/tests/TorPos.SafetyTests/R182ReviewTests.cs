using TorPos.Core;
using TorPos.Infrastructure;

// R182: the split products ship ready to work.
//
// Setup no longer asks for admin credentials, the documented access
// (admin / admin, staff PIN 1234, training code 0000) is usable at the first
// start instead of being blocked by a forced credential dialog, and an operator
// who does replace it is not pushed into a 10-character password or away from a
// 4-digit PIN. A fixed Kassenart is presented centred across the whole row.
//
// The audit trace for a session on shipped credentials is asserted in
// R122ReviewTests, which also covers the record-level permission contract that
// staff slots still depend on.
public static class R182ReviewTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "r182-access");
        Directory.CreateDirectory(dir);

        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r182.db"));
        var audit = new AuditLogRepository(db);
        var auth = new AuthenticationService(db, audit);
        await auth.InitializeAsync();

        var factory = await auth.LoginWithPasswordAsync("admin", "admin");
        assert(
            factory.Success &&
            factory.User is { IsAdmin: true, MustChangePassword: false } &&
            factory.User.Can(UserPermissions.Sale) &&
            factory.User.Can(UserPermissions.ZReport),
            "R182 the shipped admin access works at the first start instead of being blocked by a forced credential change");

        await auth.ChangeAdminCredentialsAsync("admin", "1234", "1234");
        var changed = await auth.LoginWithPasswordAsync("admin", "1234");
        assert(
            changed.Success && changed.User is { IsAdmin: true, MustChangePassword: false },
            "R182 an operator may choose a short password and a 1234 PIN instead of a forced 10-character password");

        var installer = File.ReadAllText(FindRepoFile("Desktop/TOR-POS-Pro-Setup.iss"));
        assert(
            !installer.Contains("AdminPage", StringComparison.Ordinal) &&
            !installer.Contains("first-run-admin.cfg", StringComparison.Ordinal),
            "R182 setup no longer asks for admin credentials and writes no bootstrap credential file");

        var login = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/LoginWindow.axaml.cs"));
        assert(
            login.Contains("Grid.SetColumnSpan(visible, 2)", StringComparison.Ordinal) &&
            login.Contains("HorizontalAlignment.Center", StringComparison.Ordinal) &&
            login.Contains("FontSize = 22", StringComparison.Ordinal),
            "R182 a fixed Kassenart spans the whole row, sits centred and is shown large enough to read at the till");
    }

    private static string FindRepoFile(string relative)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException(relative);
    }
}
