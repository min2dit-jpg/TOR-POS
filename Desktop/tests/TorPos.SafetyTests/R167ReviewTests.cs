using TorPos.Core;
using TorPos.Infrastructure;

public static class R167ReviewTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var profiles = PaymentTerminalProfiles.All;

        string[] requested =
        [
            "PAYONE_ZVT",
            "CCV_ZVT",
            "SPARKASSE_ZVT",
            "SUMUP_CLOUD",
            "MYPOS_EPOS",
            "MYPOS_DOTNET",
            "READYPAY_API",
            "ZETTLE_SDK",
            "FLATPAY_PARTNER"
        ];

        assert(
            requested.All(id => profiles.Any(x => x.Id == id)),
            "R167 terminal catalogue contains PAYONE, CCV, Sparkasse, SumUp, myPOS, readyPay/readyMini, Zettle/iZettle and Flatpay profiles");

        var payone = PaymentTerminalProfiles.Find("PAYONE_ZVT");
        var ccv = PaymentTerminalProfiles.Find("CCV_ZVT");
        var sparkasse = PaymentTerminalProfiles.Find("SPARKASSE_ZVT");
        assert(
            new[] { payone, ccv, sparkasse }.All(x =>
                x.ProductionReady &&
                x.Protocol == "ZVT_TCP" &&
                x.RequiresNetworkEndpoint &&
                x.DefaultPort == 20007),
            "R167 verified PAYONE, CCV and Sparkasse/S-Haendlerservice profiles use the existing production ZVT TCP adapter");

        assert(
            sparkasse.Notes.Contains("S-POS", StringComparison.Ordinal) &&
            sparkasse.Notes.Contains("nicht umfasst", StringComparison.OrdinalIgnoreCase),
            "R167 Sparkasse profile does not falsely classify S-POS/S-POS Cube as a generic ZVT terminal");

        var proprietary = new[]
        {
            PaymentTerminalProfiles.Find("MYPOS_EPOS"),
            PaymentTerminalProfiles.Find("MYPOS_DOTNET"),
            PaymentTerminalProfiles.Find("READYPAY_API"),
            PaymentTerminalProfiles.Find("ZETTLE_SDK"),
            PaymentTerminalProfiles.Find("FLATPAY_PARTNER")
        };
        assert(
            proprietary.All(x => !x.ProductionReady && x.Protocol != "ZVT_TCP"),
            "R167 proprietary myPOS, readyPay, Zettle and Flatpay profiles stay fail-closed instead of being routed through ZVT");

        var sumup = PaymentTerminalProfiles.Find("SUMUP");
        assert(
            sumup.Id == "SUMUP_CLOUD" &&
            !sumup.ProductionReady &&
            sumup.Protocol == "SUMUP_CLOUD",
            "R167 legacy SUMUP selection resolves to the dedicated SumUp Cloud profile without claiming live ZVT support");

        assert(
            PaymentTerminalProfiles.Find("PAYONE").Id == "PAYONE_ZVT" &&
            PaymentTerminalProfiles.Find("CCV").Id == "CCV_ZVT" &&
            PaymentTerminalProfiles.Find("SPARKASSE").Id == "SPARKASSE_ZVT" &&
            PaymentTerminalProfiles.Find("READYMINI").Id == "READYPAY_API" &&
            PaymentTerminalProfiles.Find("IZETTLE").Id == "ZETTLE_SDK" &&
            PaymentTerminalProfiles.Find("FLATPAY").Id == "FLATPAY_PARTNER",
            "R167 common customer-facing provider names resolve to stable internal terminal profiles");

        var dir = Path.Combine(root, "r167-terminal-profile");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r167.db"));
        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        var journal = new CheckoutJournal(db);
        var service = new ZvtPaymentTerminalService(settings, audit, journal);

        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["payment.terminal.enabled"] = "true",
            ["payment.terminal.vendor"] = "MYPOS_EPOS",
            ["payment.terminal.protocol"] = "MYPOS_EPOS",
            ["payment.terminal.ip"] = "127.0.0.1",
            ["payment.terminal.port"] = "1"
        });

        var blocked = await service.ProbeAsync();
        assert(
            !blocked.Success &&
            blocked.State == "KONFIGURATION_FEHLER" &&
            blocked.Message.Contains("myPOS", StringComparison.OrdinalIgnoreCase),
            "R167 a proprietary provider cannot reach the live ZVT transport even when an old enabled flag and endpoint are present");

        var setupCode = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.App/PaymentTerminalSetupWindow.cs"));
        var settingsCode = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.App/SettingsWindow.axaml.cs"));
        assert(
            setupCode.Contains("_enabled.IsEnabled = p.ProductionReady", StringComparison.Ordinal) &&
            setupCode.Contains("if (!p.ProductionReady)", StringComparison.Ordinal) &&
            setupCode.Contains("PaymentTerminalProfiles.All", StringComparison.Ordinal) &&
            settingsCode.Contains("KARTENTERMINAL VERBINDEN · MARKE AUSWÄHLEN", StringComparison.Ordinal),
            "R167 normal device settings expose one brand assistant and its live-payment toggle follows reviewed production readiness");

        var firstRun = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.App/FirstRunSetupWindow.cs"));
        assert(
            firstRun.Contains("Field(\"Marke / Profil\", _terminalProfile)", StringComparison.Ordinal) &&
            firstRun.Contains("terminalProfile.ProductionReady && _terminalEnabled.IsChecked == true", StringComparison.Ordinal) &&
            firstRun.Contains("[\"payment.terminal.protocol\"] = terminalProfile.Protocol", StringComparison.Ordinal),
            "R167 first-run setup asks for the terminal brand and saves the profile's real protocol without enabling an unapproved adapter");

        assert(
            settingsCode.Contains("var profile = PaymentTerminalProfiles.Find(", StringComparison.Ordinal) &&
            settingsCode.Contains("values[\"payment.terminal.protocol\"] = profile.Protocol", StringComparison.Ordinal) &&
            settingsCode.Contains("if (!profile.ProductionReady)", StringComparison.Ordinal) &&
            settingsCode.Contains("values[\"payment.terminal.enabled\"] = \"false\"", StringComparison.Ordinal),
            "R167 technician settings derive protocol from the selected profile and forcibly disable unsupported proprietary profiles");

        var sumupCode = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Infrastructure/SumUpConnectionService.cs"));
        assert(
            sumupCode.Contains("R37 test-only SumUp integration", StringComparison.Ordinal) &&
            sumupCode.Contains("value = 100", StringComparison.Ordinal) &&
            setupCode.Contains("SUMUP GERÄT / PAIRING ÖFFNEN", StringComparison.Ordinal),
            "R167 keeps the existing SumUp 1-EUR device test clearly separate from production card checkout");
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

        throw new FileNotFoundException(
            $"R167 review could not locate repository file: {relativePath}");
    }
}
