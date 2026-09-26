using TorPos.App;
using TorPos.Infrastructure;

public static class R181ReviewTests
{
    public static async Task Run(string root, Action<bool,string> assert)
    {
        var main = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"));
        var infra = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Infrastructure/Infrastructure.cs"));
        var settings = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/SettingsWindow.axaml.cs"));

        assert(
            main.Contains("private readonly Queue<string> _barcodeQueue = new();", StringComparison.Ordinal) &&
            main.Contains("DrainBarcodeQueueAsync", StringComparison.Ordinal) &&
            main.Contains("_barcodeQueue.Enqueue(code.Trim())", StringComparison.Ordinal),
            "R181 rapid scans are buffered in a FIFO queue instead of being dropped while one barcode is processing");

        assert(
            !main.Contains("_scan.Length < 6 || _scanProcessing", StringComparison.Ordinal) &&
            !main.Contains("_scan.Length >= 6 && gap < maxSuffixGap && !_scanProcessing", StringComparison.Ordinal) &&
            !main.Contains("hasSuffix && _scan.Length >= 6 && !_scanProcessing", StringComparison.Ordinal),
            "R181 capture completion no longer rejects a second scan merely because the previous barcode is still processing");

        assert(
            infra.Contains("('scanner.wait_ms','140')", StringComparison.Ordinal) &&
            main.Contains("[\"scanner.wait_ms\"] = \"140\"", StringComparison.Ordinal) &&
            main.Contains("scanner.r181_latency_migrated", StringComparison.Ordinal) &&
            main.Contains("Math.Max(140, configured)", StringComparison.Ordinal),
            "R181 suffix-less scanner latency defaults/migrates from the legacy 1000 ms delay to 140 ms");

        assert(
            main.Contains("_barcodeQueue.Count >= 64", StringComparison.Ordinal) &&
            main.Contains("SCANNER-WARTESCHLANGE VOLL", StringComparison.Ordinal) &&
            settings.Contains("RAM-Index", StringComparison.Ordinal),
            "R181 scanner queue is bounded and keeps the existing in-memory barcode lookup contract");

        var login = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/LoginWindow.axaml.cs"));
        var app = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/App.axaml.cs"));
        // R182 routed both lock directions through one helper that hides the other
        // radio, so the contract is pinned on that helper and on both call sites
        // instead of on two separate IsVisible assignments.
        assert(
            login.Contains("hidden.IsVisible = false", StringComparison.Ordinal) &&
            login.Contains("ShowFixedEdition(KioskEditionRadio, ImbissEditionRadio)", StringComparison.Ordinal) &&
            login.Contains("ShowFixedEdition(ImbissEditionRadio, KioskEditionRadio)", StringComparison.Ordinal) &&
            login.Contains("_lockedEdition ??", StringComparison.Ordinal),
            "R181 licensed edition hides the other sector completely on the login screen");
        assert(
            app.Contains("commercialLicense.Check(\"KIOSK\")", StringComparison.Ordinal) &&
            app.Contains("commercialLicense.Check(\"IMBISS\")", StringComparison.Ordinal) &&
            app.Contains("permanentLock: lockedEdition is not null", StringComparison.Ordinal),
            "R181 active signed licence determines and permanently binds the installation edition");

        var editionDir = Path.Combine(root, "r181-edition");
        Directory.CreateDirectory(editionDir);
        var previousOverride = AppPaths.DataDirectoryOverride;
        AppPaths.DataDirectoryOverride = editionDir;
        try
        {
            var editionDb = await SafetyDatabase.CreateCurrentAsync(
                Path.Combine(editionDir, "edition.db"));
            var editionSettings = new SettingsRepository(editionDb);

            await editionSettings.SaveManyAsync(new Dictionary<string,string>
            {
                ["company.name"] = "Einzelhandel Test GmbH",
                ["company.street"] = "Retailweg 1",
                ["installation.first_run_completed"] = "true"
            });

            await InstallationEdition.EnforceAsync(editionSettings, "KIOSK");
            await editionSettings.SaveManyAsync(new Dictionary<string,string>
            {
                ["company.name"] = "Einzelhandel Separat"
            });

            await InstallationEdition.EnforceAsync(editionSettings, "IMBISS");
            var afterGastroSwitch = await editionSettings.LoadAllAsync();
            assert(
                string.IsNullOrEmpty(afterGastroSwitch.GetValueOrDefault("company.name", "")) &&
                afterGastroSwitch.GetValueOrDefault(
                    InstallationEdition.ProfileKey("KIOSK", "company.name"), "") == "Einzelhandel Separat",
                "R181 switching to Gastronomie never leaks Einzelhandel company identity");

            await editionSettings.SaveManyAsync(new Dictionary<string,string>
            {
                ["company.name"] = "Gastronomie Separat",
                ["company.city"] = "Berlin"
            });

            await InstallationEdition.EnforceAsync(editionSettings, "KIOSK");
            var backToRetail = await editionSettings.LoadAllAsync();
            assert(
                backToRetail.GetValueOrDefault("company.name", "") == "Einzelhandel Separat" &&
                backToRetail.GetValueOrDefault(
                    InstallationEdition.ProfileKey("IMBISS", "company.name"), "") == "Gastronomie Separat",
                "R181 test-mode company identities round-trip independently between Einzelhandel and Gastronomie");

            await InstallationEdition.EnforceAsync(
                editionSettings,
                "KIOSK",
                permanentLock: true);
            assert(
                InstallationEdition.ReadPermanent() == "KIOSK",
                "R181 permanent edition lock is stored separately from the temporary test selection");

            var refusedOtherEdition = false;
            try
            {
                await InstallationEdition.EnforceAsync(editionSettings, "IMBISS");
            }
            catch (InvalidOperationException ex)
            {
                refusedOtherEdition = ex.Message.Contains("dauerhaft", StringComparison.OrdinalIgnoreCase);
            }
            assert(
                refusedOtherEdition,
                "R181 permanently licensed Einzelhandel installation refuses Gastronomie activation");
        }
        finally
        {
            AppPaths.DataDirectoryOverride = previousOverride;
        }
    }

    private static string FindRepoFile(string relative)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException(relative);
    }
}
