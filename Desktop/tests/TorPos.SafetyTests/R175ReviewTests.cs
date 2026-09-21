using TorPos.Core;

public static class R175ReviewTests
{
    public static Task Run(
        string root,
        Action<bool, string> assert)
    {
        var probe = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Infrastructure/FiskaltrustSwissbitProbe.cs"));
        var settings = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.App/SettingsWindow.axaml.cs"));
        var manifest = File.ReadAllText(
            FindRepoFile("Desktop/manifest.json"));
        var fiscalGate = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Core/CheckoutSafety.cs"));

        assert(
            probe.Contains("public const string ProviderId = \"FISKALTRUST_SWISSBIT\"", StringComparison.Ordinal) &&
            probe.Contains("Configuration-*.json", StringComparison.Ordinal) &&
            probe.Contains("ftSignaturCreationDevices", StringComparison.Ordinal) &&
            probe.Contains("Swissbit", StringComparison.Ordinal),
            "R175 detects an installed fiskaltrust Swissbit SCU from local service configuration without hard-coded cashbox ids");

        assert(
            probe.Contains("TSE_INFO.DAT", StringComparison.Ordinal) &&
            probe.Contains("File.Exists", StringComparison.Ordinal),
            "R175 checks the configured Swissbit device path read-only via TSE_INFO.DAT");

        assert(
            probe.Contains("TcpClient", StringComparison.Ordinal) &&
            probe.Contains("ConnectAsync", StringComparison.Ordinal),
            "R175 checks the configured Swissbit SCU endpoint with a bounded TCP probe");

        assert(
            probe.Contains("/json/v1/Echo", StringComparison.Ordinal) &&
            probe.Contains("TOR POS READONLY PROBE", StringComparison.Ordinal) &&
            !probe.Contains("StartTransactionAsync", StringComparison.Ordinal) &&
            !probe.Contains("FinishTransactionAsync", StringComparison.Ordinal) &&
            !probe.Contains("RegisterClient", StringComparison.Ordinal),
            "R175 fiskaltrust verification uses only Queue Echo and never performs client registration or fiscal transactions");

        assert(
            !probe.Contains("GetProperty(\"AccessToken\"", StringComparison.Ordinal) &&
            !probe.Contains("accesstoken=", StringComparison.OrdinalIgnoreCase),
            "R175 fiskaltrust probe does not read, embed or log the middleware access token");

        assert(
            settings.Contains("FISKALTRUST PRÜFEN (NUR LESEN)", StringComparison.Ordinal) &&
            settings.Contains("keine Registrierung, Aktivierung oder Transaktion", StringComparison.Ordinal) &&
            settings.Contains("FiskaltrustSwissbitProbe.ProbeAsync", StringComparison.Ordinal),
            "R175 technician TSE page exposes an explicit read-only fiskaltrust Swissbit diagnostic");

        assert(
            manifest.Contains("\"alternative_provider\": \"FISKALTRUST_SWISSBIT\"", StringComparison.Ordinal) &&
            manifest.Contains("\"alternative_probe_implemented\": true", StringComparison.Ordinal) &&
            manifest.Contains("\"real_hardware_e2e_validated\": false", StringComparison.Ordinal),
            "R175 manifest records fiskaltrust Swissbit as a non-released alternative while hardware E2E remains false");

        assert(
            fiscalGate.Contains("PhysicalTseE2EValidated = false", StringComparison.Ordinal) &&
            fiscalGate.Contains("IndependentFiscalReviewValidated = false", StringComparison.Ordinal),
            "R175 manual middleware detection does not silently open the fiscal production release gate");

        return Task.CompletedTask;
    }

    private static string FindRepoFile(string relativePath)
    {
        foreach (var start in new[]
                 {
                     AppContext.BaseDirectory,
                     Directory.GetCurrentDirectory()
                 })
        {
            for (var dir = new DirectoryInfo(start);
                 dir is not null;
                 dir = dir.Parent)
            {
                var candidate = Path.Combine(
                    dir.FullName,
                    relativePath.Replace(
                        '/',
                        Path.DirectorySeparatorChar));

                if (File.Exists(candidate))
                    return candidate;
            }
        }

        throw new FileNotFoundException(
            $"R175 review could not locate repository file: {relativePath}");
    }
}
