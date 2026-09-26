namespace TorPos.Infrastructure;

/// <summary>
/// O-15: the fiscal database lives in the Windows user's roaming profile, so a
/// second Windows user on the same PC starts with an empty till and the fiscal
/// records are split. Moving the data to ProgramData needs an installer/ACL
/// change and a verified migration on real customer PCs; until that has passed
/// hardware acceptance, the split is at least made visible: at start the till
/// looks for a TOR database of the same product in the other user profiles of
/// this PC and warns in German. Profiles it may not read are skipped silently.
/// </summary>
public static class DataLocationCheck
{
    public static IReadOnlyList<string> Warnings(string currentDataDirectory, string? usersRoot, string productDirectoryName)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(usersRoot) || !Directory.Exists(usersRoot))
            return warnings;

        var current = Path.GetFullPath(currentDataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var others = new List<string>();
        IEnumerable<string> profiles;
        try
        {
            profiles = Directory.EnumerateDirectories(usersRoot).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return warnings;
        }

        foreach (var profile in profiles)
        {
            try
            {
                var candidate = Path.Combine(profile, "AppData", "Roaming", productDirectoryName);
                var full = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(full, current, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (File.Exists(Path.Combine(candidate, "torpos.db")))
                    others.Add(Path.GetFileName(profile));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // A profile this user may not read is skipped.
            }
        }

        if (others.Count > 0)
        {
            warnings.Add(
                $"Kassendaten dieses Produkts liegen auch im Windows-Benutzer {string.Join(", ", others.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}. " +
                "Fiskaldaten dürfen nicht auf mehrere Windows-Benutzer verteilt werden - die Kasse immer mit demselben Windows-Benutzer betreiben und TOR Service informieren.");
        }

        return warnings;
    }

    /// <summary>The folder holding the Windows user profiles (e.g. C:\Users), or null.</summary>
    public static string? UsersRoot()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile) ? null : Path.GetDirectoryName(profile);
    }
}
