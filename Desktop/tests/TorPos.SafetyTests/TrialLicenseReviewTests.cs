using TorPos.Core;
using TorPos.Infrastructure;

public static class TrialLicenseReviewTests
{
    public static Task Run(Action<bool, string> assert)
    {
        var started = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        var expires = started.AddDays(7);

        var active = TrialClockPolicy.EvaluateCached(
            started.AddDays(3),
            started,
            expires,
            started.AddDays(2));
        assert(
            active.IsActive && active.State == TrialLicenseState.Active && active.Offline,
            "7-day demo stays active offline while the cached absolute expiry is still in the future");

        var expired = TrialClockPolicy.EvaluateCached(
            expires,
            started,
            expires,
            expires.AddMinutes(-1));
        assert(
            !expired.IsActive && expired.State == TrialLicenseState.Expired,
            "7-day demo expires exactly at the original absolute seven-day deadline");

        var rollback = TrialClockPolicy.EvaluateCached(
            started.AddHours(2),
            started,
            expires,
            started.AddHours(3));
        assert(
            !rollback.IsActive && rollback.State == TrialLicenseState.ClockRollback,
            "demo refuses an offline clock rollback instead of extending local trial time");

        var root = Path.Combine(
            Path.GetTempPath(),
            "tor-pos-trial-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var identityPath = Path.Combine(root, "trial-installation.id");

        try
        {
            var created = TrialLicenseService.LoadOrCreateTrialId(identityPath);
            assert(
                created.Length == 64 &&
                TrialLicenseService.IsValidTrialId(created),
                "trial identity is a random 256-bit hex value and contains no hardware identifier");

            var reused = TrialLicenseService.LoadOrCreateTrialId(identityPath);
            assert(
                reused == created,
                "normal restart or reinstall reuses the same persisted machine-wide trial id");

            assert(
                !TorDistribution.IsDemoBuild,
                "normal TOR POS build remains non-demo unless TorDemoBuild=true is supplied explicitly");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }
}
