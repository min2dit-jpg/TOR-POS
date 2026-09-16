namespace TorPos.Core;

/// <summary>
/// R118: how long an account stays locked after repeated failed logins.
///
/// The previous rule reset <c>failed_attempts</c> to 0 at the moment it locked
/// the account, so every 5-minute lockout handed the next attacker another
/// five free guesses. That is a flat rate of 5 guesses per 5 minutes forever -
/// roughly 1440 per day, which puts a 4-digit PIN within a few days of
/// sustained guessing.
///
/// The counter now keeps rising until a successful login clears it, and each
/// further failure past the free attempts doubles the wait: 5, 10, 20, 40,
/// then capped. The cap is deliberate rather than an oversight: this is a
/// cash register, an admin who locks themselves out has no in-app unlock path
/// (only staff accounts can be cleared by an admin), and a till that cannot be
/// opened for a whole day is its own kind of outage. Even capped, the steady
/// state drops from 1440 guesses a day to roughly 120.
/// </summary>
public static class LoginLockoutPolicy
{
    public const int FreeAttempts = 5;

    private static readonly TimeSpan FirstLockout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumLockout = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Lockout for a given number of consecutive failures. Below
    /// <see cref="FreeAttempts"/> the account stays open.
    /// </summary>
    public static TimeSpan LockoutFor(int consecutiveFailures)
    {
        if (consecutiveFailures < FreeAttempts)
            return TimeSpan.Zero;

        var doublings = consecutiveFailures - FreeAttempts;

        // Guard the shift itself: a stored counter could be arbitrarily large.
        if (doublings >= 16)
            return MaximumLockout;

        var minutes = FirstLockout.TotalMinutes * (1 << doublings);
        return minutes >= MaximumLockout.TotalMinutes
            ? MaximumLockout
            : TimeSpan.FromMinutes(minutes);
    }
}
