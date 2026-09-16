namespace TorPos.Core;

/// <summary>
/// R122 (audit finding, low): the training-mode entry code was the literal
/// constant "0000" in LoginWindow, and the login screen printed it next to the
/// field ("Training-Anmeldung nur mit Code 0000"). Anyone standing at the till
/// could open a session that bypasses the user password entirely.
///
/// Training mode itself is harmless by design - it books nothing, signs
/// nothing and cannot use the card terminal - so this is a shoulder-surfing/
/// housekeeping matter, not a fiscal one. What it should not be is
/// unchangeable. The code now lives in app_settings under
/// <see cref="SettingKey"/>; the factory value stays "0000" so no existing
/// installation is locked out by the upgrade.
///
/// The rule is kept here as a pure function so the safety suite can assert it
/// without an Avalonia window, same as UpdateTrustPolicy (R114),
/// SaleModePolicy (R116) and LoginLockoutPolicy (R118).
/// </summary>
public static class TrainingAccessPolicy
{
    public const string SettingKey = "training.access_code";
    public const string FactoryCode = "0000";

    /// <summary>
    /// A stored value is only honoured when it is exactly four digits. A blank
    /// or malformed setting falls back to the factory code rather than locking
    /// training mode out or - worse - accepting anything.
    /// </summary>
    public static string Effective(string? configured)
    {
        var trimmed = (configured ?? "").Trim();
        return IsValidCode(trimmed) ? trimmed : FactoryCode;
    }

    public static bool IsValidCode(string? code)
    {
        if (code is null || code.Length != 4) return false;
        foreach (var c in code)
            if (c is < '0' or > '9') return false;
        return true;
    }

    /// <summary>
    /// True while the effective code is still the one printed in the manual -
    /// the only situation in which the login screen may name it on screen.
    /// </summary>
    public static bool IsFactoryDefault(string? configured) =>
        Effective(configured) == FactoryCode;

    public static bool Matches(string? configured, string? typed) =>
        string.Equals(Effective(configured), (typed ?? "").Trim(), StringComparison.Ordinal);
}
