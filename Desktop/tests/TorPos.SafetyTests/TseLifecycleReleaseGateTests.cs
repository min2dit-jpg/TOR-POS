using TorPos.Core;

internal static class TseLifecycleReleaseGateTests
{
    public static Task Run(Action<bool,string> assert)
    {
        var today = new DateOnly(2026, 9, 23);

        var far = TseCertificatePolicy.Evaluate(today.AddDays(91), today);
        assert(
            far.State == TseCertificateState.Valid &&
            !far.ShowBadge &&
            !far.ShowDialog,
            "TSE certificate >90 days stays quiet");

        var warning = TseCertificatePolicy.Evaluate(today.AddDays(90), today);
        assert(
            warning.State == TseCertificateState.Warning &&
            warning.ShowBadge &&
            !warning.ShowDialog &&
            warning.RemainingDays == 90,
            "TSE certificate at 90 days shows the early badge without modal warning");

        var critical = TseCertificatePolicy.Evaluate(today.AddDays(30), today);
        assert(
            critical.State == TseCertificateState.Critical &&
            critical.ShowBadge &&
            critical.ShowDialog &&
            critical.RemainingDays == 30,
            "TSE certificate at 30 days escalates to the one-per-process dialog path");

        var todayExpiry = TseCertificatePolicy.Evaluate(today, today);
        assert(
            todayExpiry.State == TseCertificateState.Critical &&
            todayExpiry.RemainingDays == 0 &&
            todayExpiry.ShowDialog,
            "TSE certificate remains critical on its stated end date");

        var expired = TseCertificatePolicy.Evaluate(today.AddDays(-1), today);
        assert(
            expired.State == TseCertificateState.Expired &&
            expired.ShowBadge &&
            expired.ShowDialog,
            "Expired TSE certificate is an explicit fail-closed state");

        TseDeviceInfo Device(string generation, string product = "Swissbit TSE") =>
            new(
                "Swissbit",
                product,
                generation,
                "USB",
                "SERIAL",
                "",
                today.AddYears(1),
                "D:\\",
                HardwareVersion: generation);

        assert(
            FiscalRelease.DetectPhysicalTseGeneration(Device("1.1")) ==
                PhysicalTseGeneration.Generation1_1,
            "Physical release gate detects Swissbit generation 1.1 before generation 1");

        assert(
            FiscalRelease.DetectPhysicalTseGeneration(Device("2.0")) ==
                PhysicalTseGeneration.Generation2,
            "Physical release gate detects Swissbit generation 2");

        assert(
            FiscalRelease.DetectPhysicalTseGeneration(Device("1.0")) ==
                PhysicalTseGeneration.Generation1,
            "Physical release gate detects Swissbit generation 1");

        var ambiguous = Device("1.1", "Swissbit TSE 2");
        assert(
            FiscalRelease.DetectPhysicalTseGeneration(ambiguous) ==
                PhysicalTseGeneration.Unknown,
            "Conflicting generation metadata fails closed instead of guessing");

        assert(
            !FiscalRelease.SelectPhysicalTseEvidence(
                PhysicalTseGeneration.Generation1_1,
                generation1Validated: false,
                generation11Validated: false,
                generation2Validated: true),
            "A generation-2 acceptance can never unlock a generation-1.1 device");

        assert(
            FiscalRelease.SelectPhysicalTseEvidence(
                PhysicalTseGeneration.Generation1_1,
                generation1Validated: false,
                generation11Validated: true,
                generation2Validated: false) &&
            !FiscalRelease.SelectPhysicalTseEvidence(
                PhysicalTseGeneration.Generation2,
                generation1Validated: false,
                generation11Validated: true,
                generation2Validated: false),
            "Generation-1.1 evidence unlocks only generation 1.1, not generation 2");

        var main = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"));
        var failSafe = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Infrastructure/TseFailSafeService.cs"));
        var xaml = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml"));

        assert(
            Count(main, "FiscalRelease.EnabledForProvider(_tseProvider.ProviderId, _lastTseDevice)") >= 2 &&
            !main.Contains("FiscalRelease.Enabled,", StringComparison.Ordinal),
            "Runtime sale/training gates use the actually probed provider and device generation");

        assert(
            failSafe.Contains("TseCertificateState.Expired", StringComparison.Ordinal) &&
            failSafe.Contains("TSE_CERTIFICATE_EXPIRED", StringComparison.Ordinal) &&
            main.Contains("static bool _tseCertificateDialogShownForProcess", StringComparison.Ordinal) &&
            xaml.Contains("TseCertificateWarningBadge", StringComparison.Ordinal),
            "Expired certificates become a TSE outage while 90/30-day warnings stay visible and modal only once per process");

        return Task.CompletedTask;
    }

    private static int Count(string text, string needle)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }
        return count;
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
            $"TSE lifecycle review could not locate repository file: {relativePath}");
    }
}
