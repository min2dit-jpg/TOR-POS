using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.App;

public static class InstallationEdition
{
    public static readonly string[] Allowed = ["KIOSK", "IMBISS"];

    private static readonly string[] ProfileKeys =
    [
        "company.name",
        "company.owner",
        "company.street",
        "company.zip",
        "company.city",
        "company.email",
        "company.phone",
        "company.tax_no",
        "company.vat_id",
        "cash.register.name"
    ];

    public static string ProfileKey(string edition, string key) =>
        $"profile.{Normalize(edition) ?? throw new InvalidOperationException("Ungültige Edition.")}.{key}";


    // Technical edition codes remain stable for licenses, settings, migrations,
    // Cloud payloads and existing customer data. Only the customer-facing names
    // are generalized.
    public static string DisplayName(string? edition) =>
        string.Equals(edition?.Trim(), "KIOSK", StringComparison.OrdinalIgnoreCase)
            ? "Einzelhandel"
            : string.Equals(edition?.Trim(), "IMBISS", StringComparison.OrdinalIgnoreCase)
                ? "Gastronomie"
                : "Nicht festgelegt";

    public static string DisplayNameWithCode(string? edition)
    {
        var normalized = Normalize(edition);
        return normalized is null
            ? "Nicht festgelegt"
            : $"{DisplayName(normalized)} ({normalized})";
    }

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

    public static string? ReadPermanent()
    {
        try
        {
            if (!File.Exists(AppPaths.PermanentEditionLockPath))
                return null;

            var value = File.ReadAllText(AppPaths.PermanentEditionLockPath).Trim().ToUpperInvariant();
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
        bool permanentLock = false,
        CancellationToken ct = default)
    {
        var requested = firstRunSelection;

        // R182: in a dedicated TOR Einzelhandel / TOR Gastro build the compiled product identity
        // outranks every runtime source. The TOR_POS_EDITION fallback stays available for
        // the shared build only, and a mismatching request is refused instead of applied.
        var productEditionValue = ProductBuild.FixedEdition;

        if (string.IsNullOrWhiteSpace(requested))
            requested = productEditionValue
                ?? Environment.GetEnvironmentVariable("TOR_POS_EDITION");

        var edition = Normalize(requested);
        if (edition is null)
            throw new InvalidOperationException(
                "Vor der Anmeldung muss Einzelhandel oder Gastronomie gewählt werden.");

        if (productEditionValue is { } productEdition &&
            !string.Equals(edition, productEdition, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Diese Installation gehört zu {ProductBuild.ProductName} " +
                $"({DisplayName(productEdition)}).");
        }

        var permanent = ReadPermanent();
        if (permanent is not null &&
            !string.Equals(permanent, edition, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Diese Installation ist dauerhaft für {DisplayName(permanent)} gebunden.");
        }

        var previous = ReadLocked();
        var all = await settings.LoadAllAsync(ct);

        // R181: during licence-free testing KIOSK/IMBISS may still be switched.
        // Preserve the business identity of the edition we are leaving before
        // loading the other profile into the legacy/common keys used by receipts,
        // DSFinV-K and reports.
        if (previous is not null)
        {
            var snapshot = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in ProfileKeys)
                snapshot[ProfileKey(previous, key)] = all.GetValueOrDefault(key, "");

            // Preserve the legacy first-run completion for the edition that
            // actually owned the currently loaded company data.
            if (!all.ContainsKey($"installation.first_run_completed.{previous}") &&
                all.TryGetValue("installation.first_run_completed", out var legacyFirstRun))
            {
                snapshot[$"installation.first_run_completed.{previous}"] = legacyFirstRun;
            }

            if (snapshot.Count > 0)
                await settings.SaveManyAsync(snapshot, ct);

            all = await settings.LoadAllAsync(ct);
        }

        if (permanentLock && permanent is null)
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(AppPaths.PermanentEditionLockPath, edition);
            permanent = edition;
        }

        var active = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
        {
            ["business.mode"] = edition,
            ["installation.edition"] = edition,
            ["installation.edition_locked"] = permanent is null ? "false" : "true"
        };

        var hasTargetProfile = ProfileKeys.Any(
            key => all.ContainsKey(ProfileKey(edition, key)));

        foreach (var key in ProfileKeys)
        {
            var profileKey = ProfileKey(edition, key);

            if (all.TryGetValue(profileKey, out var profileValue))
            {
                active[key] = profileValue;
                continue;
            }

            // First migration of an already existing installation: if this is
            // the edition that was already active, adopt its existing identity.
            // When switching to the other test edition for the first time, start
            // with a clean profile so names/addresses never bleed across.
            if (!hasTargetProfile &&
                (previous is null || string.Equals(previous, edition, StringComparison.Ordinal)))
            {
                var current = all.GetValueOrDefault(key, "");
                active[key] = current;
                active[profileKey] = current;
            }
            else
            {
                active[key] = "";
            }
        }

        Directory.CreateDirectory(AppPaths.DataDirectory);
        File.WriteAllText(AppPaths.EditionLockPath, edition);
        await settings.SaveManyAsync(active, ct);

        return edition;
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToUpperInvariant();
        return Allowed.Contains(normalized, StringComparer.Ordinal) ? normalized : null;
    }
}
