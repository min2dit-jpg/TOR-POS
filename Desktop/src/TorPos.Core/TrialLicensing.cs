namespace TorPos.Core;

public enum TrialLicenseState
{
    Active,
    Expired,
    VerificationRequired,
    ClockRollback
}

public sealed record TrialLicenseStatus(
    TrialLicenseState State,
    string Message,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? ExpiresAtUtc = null,
    bool Offline = false,
    bool Reused = false)
{
    public bool IsActive => State == TrialLicenseState.Active;

    public int RemainingDays
    {
        get
        {
            if (!IsActive || ExpiresAtUtc is null)
                return 0;

            var remaining = ExpiresAtUtc.Value - DateTimeOffset.UtcNow;
            return Math.Max(0, (int)Math.Ceiling(remaining.TotalDays));
        }
    }
}

public static class TrialPolicy
{
    public const int DurationDays = 7;
    public static readonly TimeSpan ClockRollbackTolerance = TimeSpan.FromMinutes(5);
    public const string PublicApiBaseUrl = "https://api.torpos.de";
}

public static class TrialClockPolicy
{
    public static TrialLicenseStatus EvaluateCached(
        DateTimeOffset nowUtc,
        DateTimeOffset startedAtUtc,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset lastObservedUtc)
    {
        if (nowUtc < lastObservedUtc - TrialPolicy.ClockRollbackTolerance)
        {
            return new TrialLicenseStatus(
                TrialLicenseState.ClockRollback,
                "Die Windows-Uhr wurde zurückgestellt. Für die Demo ist eine Online-Prüfung erforderlich.",
                startedAtUtc,
                expiresAtUtc,
                Offline: true);
        }

        if (nowUtc >= expiresAtUtc)
        {
            return new TrialLicenseStatus(
                TrialLicenseState.Expired,
                $"Die 7-Tage-Demo ist am {expiresAtUtc.ToLocalTime():dd.MM.yyyy HH:mm} abgelaufen.",
                startedAtUtc,
                expiresAtUtc,
                Offline: true);
        }

        return new TrialLicenseStatus(
            TrialLicenseState.Active,
            $"TOR POS Demo aktiv bis {expiresAtUtc.ToLocalTime():dd.MM.yyyy HH:mm}.",
            startedAtUtc,
            expiresAtUtc,
            Offline: true);
    }
}

public static class TorDistribution
{
#if TOR_DEMO_BUILD
    public const bool IsDemoBuild = true;
#else
    public const bool IsDemoBuild = false;
#endif
}

public static class TrialRuntime
{
    private static TrialLicenseStatus? _current;

    public static TrialLicenseStatus? Current => _current;

    public static void Set(TrialLicenseStatus status) =>
        _current = status ?? throw new ArgumentNullException(nameof(status));
}
