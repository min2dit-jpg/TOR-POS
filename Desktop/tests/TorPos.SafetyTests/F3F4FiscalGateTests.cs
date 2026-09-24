using TorPos.Core;

// F-3: one production rule. Sale commit, Storno, Retoure, card terminal,
// compliance overview and DSFinV-K used the strict global flag while the
// checkout screen used the probed TSE; now all follow the probed TSE.
// F-4: an unqualified cloud TSE (refuses every signature) cannot be saved as
// the till's TSE, and TSE status texts no longer assume Swissbit.
public static class F3F4FiscalGateTests
{
    public static Task Run(Action<bool, string> assert)
    {
        FiscalRelease.ClearActiveTse();
        var beforeProbe = FiscalRelease.ProductionAllowed == FiscalRelease.Enabled;

        var device = new TseDeviceInfo("Swissbit", "TSE", "2", "USB", "SN-F3", "BSI-K-TR-0000", null, "E:");
        FiscalRelease.SetActiveTse(TseProviderCatalog.SwissbitHardware, device);
        var followsProbed =
            FiscalRelease.ProductionAllowed == FiscalRelease.EnabledForProvider(TseProviderCatalog.SwissbitHardware, device) &&
            FiscalRelease.MissingQualificationsForActiveTse().SequenceEqual(
                FiscalRelease.MissingQualificationsForProvider(TseProviderCatalog.SwissbitHardware, device));

        FiscalRelease.SetActiveTse("CLOUD_TSE", null);
        var unknownRefused = !FiscalRelease.ProductionAllowed;
        var threw = false;
        try { FiscalRelease.RequireProduction(); }
        catch (InvalidOperationException ex) { threw = ex.Message.Contains("unbekannter TSE-Provider", StringComparison.Ordinal); }
        FiscalRelease.ClearActiveTse();

        assert(
            beforeProbe && followsProbed && unknownRefused && threw,
            "F-3 before a TSE is probed the strict rule applies; afterwards every booking gate follows the probed provider and device, and an unknown provider is refused with its reason");

        var core = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Core/CheckoutSafety.cs"));
        var main = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"));
        var compliance = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Infrastructure/FiscalComplianceServices.cs"));
        var dsfinvk = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Infrastructure/DsfinvkExportService.cs"));
        var require = core[core.IndexOf("public static void RequireProduction()", StringComparison.Ordinal)..];
        require = require[..require.IndexOf('}')];
        assert(
            require.Contains("ProductionAllowed", StringComparison.Ordinal) &&
            main.Contains("FiscalRelease.SetActiveTse(_tseProvider.ProviderId, result.Device);", StringComparison.Ordinal) &&
            compliance.Contains("FiscalRelease.ProductionAllowed", StringComparison.Ordinal) &&
            !compliance.Contains("FiscalRelease.Enabled,", StringComparison.Ordinal) &&
            !dsfinvk.Contains("FiscalRelease.Enabled", StringComparison.Ordinal),
            "F-3 RequireProduction, the compliance overview and the DSFinV-K export share the probed-TSE rule the checkout uses");

        var settings = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/SettingsWindow.axaml.cs"));
        assert(
            settings.Contains("TseProviderKind.Normalize(values.GetValueOrDefault(TseProviderKind.Setting, \"\")) == TseProviderKind.Cloud", StringComparison.Ordinal) &&
            settings.Contains("!CloudTseRelease.IsValidated(values.GetValueOrDefault(CloudTseSettings.VendorSetting, \"\"))", StringComparison.Ordinal) &&
            !main.Contains("\"Swissbit TSE erkannt", StringComparison.Ordinal) &&
            !compliance.Contains("Swissbit TSE ist als AKTIV", StringComparison.Ordinal),
            "F-4 an unreleased cloud TSE cannot be saved as the till's TSE, and TSE status texts no longer assume Swissbit");

        return Task.CompletedTask;
    }

    private static string FindRepoFile(string relativePath)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        throw new FileNotFoundException(relativePath);
    }
}
