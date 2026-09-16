using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.App;

public static class InstallationEdition
{
    public static readonly string[] Allowed = ["KIOSK", "IMBISS"];

    public static string? ReadLocked()
    {
        try
        {
            if (!File.Exists(AppPaths.EditionLockPath))
                return null;

            var value = File.ReadAllText(AppPaths.EditionLockPath).Trim().ToUpperInvariant();
            return Allowed.Contains(value, StringComparer.Ordinal) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<string> EnforceAsync(
        ISettingsRepository settings,
        string? firstRunSelection = null,
        CancellationToken ct = default)
    {
        // The edition selected on the login panel is authoritative for the
        // current session. A previous edition.lock must never preselect or block
        // the other option. The selected edition is written back after login so
        // all existing KIOSK/IMBISS feature checks read the current choice.
        var requested = firstRunSelection;

        if (string.IsNullOrWhiteSpace(requested))
            requested = Environment.GetEnvironmentVariable("TOR_POS_EDITION");

        var edition = Normalize(requested);

        if (edition is null)
            throw new InvalidOperationException(
                "Vor der Anmeldung muss KIOSK oder IMBISS gewählt werden.");

        Directory.CreateDirectory(AppPaths.DataDirectory);
        File.WriteAllText(AppPaths.EditionLockPath, edition);

        await settings.SaveManyAsync(new Dictionary<string,string>
        {
            ["business.mode"] = edition,
            ["installation.edition"] = edition,
            ["installation.edition_locked"] = "true"
        }, ct);

        return edition;
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToUpperInvariant();
        return Allowed.Contains(normalized, StringComparer.Ordinal) ? normalized : null;
    }
}
