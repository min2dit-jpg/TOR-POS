using TorPos.Core;

internal static class TseLifecycleReleaseGateTests
{
    public static Task Run(Action<bool,string> assert)
    {
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

        var far = TseCertificatePolicy.Evaluate(now.AddDays(91), now);
        assert(
            far.State == TseCertificateState.Valid &&
            !far.ShowBadge &&
            !far.ShowDialog,
            "TSE certificate >90 days stays quiet");

        var warning = TseCertificatePolicy.Evaluate(now.AddDays(90), now);
        assert(
            warning.State == TseCertificateState.Warning &&
            warning.ShowBadge &&
            !warning.ShowDialog &&
            warning.RemainingDays == 90,
            "TSE certificate at 90 days shows the early badge without modal warning");

        var critical = TseCertificatePolicy.Evaluate(now.AddDays(30), now);
        assert(
            critical.State == TseCertificateState.Critical &&
            critical.ShowBadge &&
            critical.ShowDialog &&
            critical.RemainingDays == 30,
            "TSE certificate at 30 days escalates to the one-per-process dialog path");

        var todayExpiry = TseCertificatePolicy.Evaluate(today, now);
        assert(
            todayExpiry.State == TseCertificateState.Critical &&
            todayExpiry.RemainingDays == 0 &&
            todayExpiry.ShowDialog,
            "TSE certificate remains critical on its stated end date");

        var expired = TseCertificatePolicy.Evaluate(now.AddDays(-1), now);
        assert(
            expired.State == TseCertificateState.Expired &&
            expired.ShowBadge &&
            expired.ShowDialog,
            "TSE certificate becomes expired at its exact UTC expiration instant");

        TseDeviceInfo Device(
            string generation,
            string product = "Swissbit TSE",
            string hardwareVersion = "9.9") =>
            new(
                "Swissbit",
                product,
                generation,
                "USB",
                "SERIAL",
                "",
                DateOnly.FromDateTime(now.UtcDateTime.AddYears(1)),
                "D:\\",
                HardwareVersion: hardwareVersion,
                CertificateExpiresAtUtc: now.AddYears(1));

        assert(
            FiscalRelease.DetectPhysicalTseGeneration(
                Device("Swissbit TSE 1.1", hardwareVersion: "2.99")) ==
                PhysicalTseGeneration.Generation1_1,
            "Physical release gate uses TSE description for Gen 1.1 and ignores a misleading 2.x hardware revision");

        assert(
            FiscalRelease.DetectPhysicalTseGeneration(
                Device("", hardwareVersion: "2.0")) ==
                PhysicalTseGeneration.Unknown,
            "Physical release gate never infers Gen 2 from hardwareVersion alone");

        assert(
            FiscalRelease.DetectPhysicalTseGeneration(
                Device("Swissbit TSE 1", hardwareVersion: "2.10")) ==
                PhysicalTseGeneration.Generation1,
            "Physical release gate detects Gen 1 only from generation evidence, not hardware revision");

        var unknown = Device("", "Swissbit Hardware", "2.0");
        assert(
            FiscalRelease.DetectPhysicalTseGeneration(unknown) ==
                PhysicalTseGeneration.Unknown,
            "Missing generation evidence fails closed even when product/hardware strings look versioned");

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

        var reprobeIndex = failSafe.IndexOf(
            "var releaseProbe = await _provider.ProbeAsync(ct);",
            StringComparison.Ordinal);
        var commonGateIndex = failSafe.IndexOf(
            "if (FiscalRelease.CommonQualificationsValidated)",
            reprobeIndex < 0 ? 0 : reprobeIndex,
            StringComparison.Ordinal);

        assert(
            failSafe.Contains("TseCertificateState.Expired", StringComparison.Ordinal) &&
            failSafe.Contains("TSE_CERTIFICATE_EXPIRED", StringComparison.Ordinal) &&
            reprobeIndex >= 0 &&
            commonGateIndex > reprobeIndex &&
            failSafe.Contains("releaseProbe.Device?.CertificateExpiresAtUtc", StringComparison.Ordinal) &&
            failSafe.Contains("FiscalRelease.EnabledForProvider(", StringComparison.Ordinal) &&
            main.Contains("static bool _tseCertificateDialogShownForProcess", StringComparison.Ordinal) &&
            main.Contains("_tseCertificateTimer.Interval = TimeSpan.FromMinutes(15)", StringComparison.Ordinal) &&
            main.Contains("_tseCertificateTimer.Start()", StringComparison.Ordinal) &&
            main.Contains("_tseCertificateTimer.Stop()", StringComparison.Ordinal) &&
            xaml.Contains("TseCertificateWarningBadge", StringComparison.Ordinal),
            "Exact certificate expiry is checked outside release paperwork; production re-probes current generation; long-running tills re-evaluate 90/30/0 thresholds periodically");

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
