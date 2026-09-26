using TorPos.Core;
using TorPos.Infrastructure;

// R126: small screens.
//
// On the HP till (and at 1366px on a laptop) the header pushed AUSSER HAUS,
// GEMISCHT and ABMELDEN off the screen and the numpad digits were cut in half.
// The layout itself can only be looked at by tools/TorPos.UiSnapshot (rendering
// Avalonia inside this console host is not reliable - see R87), which CI runs
// with --check. What is testable here is the rule that decides how far the
// header texts shorten, and the sandbox that keeps the snapshot tool away from
// a real till's data.
public static class R126ReviewTests
{
    public static void Run(Action<bool, string> assert)
    {
        // ---------- header density ----------
        double Needs(HeaderDensity d) => d switch
        {
            HeaderDensity.Full => 1000,
            HeaderDensity.Compact => 700,
            _ => 450
        };

        assert(
            HeaderDensityPolicy.Choose(1200, Needs) == HeaderDensity.Full,
            "R126 a wide header keeps the full status texts");

        assert(
            HeaderDensityPolicy.Choose(1100, Needs) == HeaderDensity.Compact,
            "R126 full texts are dropped once they would leave the company name less than readable room, even though they technically fit");

        assert(
            HeaderDensityPolicy.Choose(700, Needs) == HeaderDensity.Compact,
            "R126 compact texts are used as long as they fit exactly");

        assert(
            HeaderDensityPolicy.Choose(600, Needs) == HeaderDensity.Minimal,
            "R126 a narrow header falls back to the minimal texts instead of overflowing");

        var outage = new HeaderLabel("TSE-AUSFALL · seit 16.09. 10:16", "TSE-AUSFALL", "TSE-AUSFALL");
        assert(
            outage.For(HeaderDensity.Minimal).Contains("TSE-AUSFALL") &&
            outage.For(HeaderDensity.Compact).Contains("TSE-AUSFALL"),
            "R126 the TSE outage stays named at every density - it is a legal state, only its start time moves to the tooltip");

        assert(
            HeaderDensityPolicy.ShowCompanyName(500, 400) &&
            !HeaderDensityPolicy.ShowCompanyName(500, 450),
            "R126 the company name is hidden rather than shown as a few letters and an ellipsis");

        // ---------- the snapshot sandbox ----------
        var previousOverride = AppPaths.DataDirectoryOverride;
        var callerDataDirectory = AppPaths.DataDirectory;
        var sandbox = Path.Combine(Path.GetTempPath(), "tor-r126-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppPaths.DataDirectoryOverride = sandbox;
            assert(
                AppPaths.DatabasePath.StartsWith(sandbox, StringComparison.OrdinalIgnoreCase) &&
                AppPaths.BackupsPath.StartsWith(sandbox, StringComparison.OrdinalIgnoreCase) &&
                new SqliteDatabase().DatabasePath.StartsWith(sandbox, StringComparison.OrdinalIgnoreCase),
                "R126 with the override set, the database and every data folder resolve inside the throwaway folder");
        }
        finally
        {
            AppPaths.DataDirectoryOverride = previousOverride;
            try { Directory.Delete(sandbox, recursive: true); } catch { /* best effort */ }
        }

        assert(
            AppPaths.DataDirectoryOverride == previousOverride &&
            AppPaths.DataDirectory == callerDataDirectory &&
            (previousOverride is not null || callerDataDirectory.EndsWith("TOR-POS-Pro", StringComparison.OrdinalIgnoreCase)),
            "R126 snapshot sandbox restores exactly the caller data-directory context");
    }
}
