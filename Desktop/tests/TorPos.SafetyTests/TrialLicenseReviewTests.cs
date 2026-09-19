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

        var fp1 = TrialLicenseService.FingerprintHashFor(
            "machine-guid-123",
            "a1b2c3d4");
        var fp2 = TrialLicenseService.FingerprintHashFor(
            "MACHINE-GUID-123",
            "A1B2C3D4");
        assert(
            fp1 == fp2 && fp1.Length == 64,
            "trial fingerprint is a stable case-normalized SHA-256 value and contains no installation id");

        var fp3 = TrialLicenseService.FingerprintHashFor(
            "machine-guid-123",
            "FFFFFFFF");
        assert(
            fp3 != fp1,
            "trial fingerprint changes when the stable machine material changes");

        assert(
            !TorDistribution.IsDemoBuild,
            "normal TOR POS build remains non-demo unless TorDemoBuild=true is supplied explicitly");

        return Task.CompletedTask;
    }
}
