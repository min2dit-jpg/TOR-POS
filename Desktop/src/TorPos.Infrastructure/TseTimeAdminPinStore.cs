using System.Security.Cryptography;
using System.Text;
using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// Stores the optional Swissbit TimeAdmin PIN encrypted with DPAPI.
///
/// F-1: an automatically stored PIN is fail-closed. After the first failed
/// TimeAdmin login TOR persists a suspension marker and will not submit the
/// stored PIN again until an operator explicitly saves it again. The last
/// remaining-retry count is persisted as a second guard so a restart cannot
/// silently consume the final TSE attempt.
/// </summary>
public sealed class TseTimeAdminPinStore
{
    public const string EnabledSetting = "tse.time_admin_pin.stored";
    public const string ValueSetting = "tse.time_admin_pin.protected";
    public const string SuspendedSetting = "tse.time_admin_pin.suspended";
    public const string RemainingRetriesSetting = "tse.time_admin_pin.remaining_retries";
    public const string SuspensionReasonSetting = "tse.time_admin_pin.suspension_reason";

    private readonly ISettingsRepository _settings;
    private volatile string _stored = "";
    private volatile bool _suspended;
    private int _remainingRetries = -1;
    private volatile string _suspensionReason = "";

    public TseTimeAdminPinStore(ISettingsRepository settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Cached PIN available for automatic use. It is intentionally empty while
    /// suspended or when the TSE reported one or fewer retries remaining.
    /// </summary>
    public string Current =>
        CanAutoUse ? _stored : "";

    public bool HasStoredValue => _stored.Length > 0;

    public bool Enabled => CanAutoUse;

    public bool Suspended => _suspended;

    public int? RemainingRetries =>
        Volatile.Read(ref _remainingRetries) >= 0
            ? Volatile.Read(ref _remainingRetries)
            : null;

    public string SuspensionReason => _suspensionReason;

    public bool CanAutoUse =>
        _stored.Length > 0 &&
        !_suspended &&
        (RemainingRetries is null or > 1);

    /// <summary>
    /// Never throws and never logs/decrypts into persistent plaintext. A
    /// profile copied to another Windows user degrades to "no stored PIN".
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        _stored = "";
        _suspended = false;
        _suspensionReason = "";
        Volatile.Write(ref _remainingRetries, -1);

        var enabled = await _settings.GetAsync(EnabledSetting, "false", ct);
        if (!string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
            return;

        var suspended =
            await _settings.GetAsync(SuspendedSetting, "false", ct);
        _suspended = string.Equals(
            suspended,
            "true",
            StringComparison.OrdinalIgnoreCase);

        var retryText =
            await _settings.GetAsync(RemainingRetriesSetting, "", ct);
        if (int.TryParse(retryText, out var retries) && retries >= 0)
            Volatile.Write(ref _remainingRetries, retries);

        _suspensionReason =
            await _settings.GetAsync(SuspensionReasonSetting, "", ct);

        var stored = await _settings.GetAsync(ValueSetting, "", ct);
        if (string.IsNullOrWhiteSpace(stored))
            return;

        try
        {
            if (!OperatingSystem.IsWindows())
                return;

            _stored = Encoding.UTF8.GetString(
                ProtectedData.Unprotect(
                    Convert.FromBase64String(stored),
                    null,
                    DataProtectionScope.CurrentUser));
        }
        catch
        {
            _stored = "";
        }
    }

    /// <summary>
    /// Explicit operator save is the only action that re-arms a suspended
    /// stored PIN.
    /// </summary>
    public async Task SaveAsync(string pin, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            await ClearAsync(ct);
            return;
        }

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Die TimeAdmin-PIN wird nur unter Windows gespeichert.");

        var trimmed = pin.Trim();

        await _settings.SaveManyAsync(
            new Dictionary<string, string>
            {
                [ValueSetting] = Convert.ToBase64String(
                    ProtectedData.Protect(
                        Encoding.UTF8.GetBytes(trimmed),
                        null,
                        DataProtectionScope.CurrentUser)),
                [EnabledSetting] = "true",
                [SuspendedSetting] = "false",
                [RemainingRetriesSetting] = "",
                [SuspensionReasonSetting] = ""
            },
            ct);

        _stored = trimmed;
        _suspended = false;
        _suspensionReason = "";
        Volatile.Write(ref _remainingRetries, -1);
    }

    public async Task SuspendAsync(
        int? remainingRetries,
        string reason,
        CancellationToken ct = default)
    {
        var retries = remainingRetries is >= 0
            ? remainingRetries.Value
            : -1;
        var safeReason = string.IsNullOrWhiteSpace(reason)
            ? "Gespeicherte TimeAdmin-PIN nach fehlgeschlagenem Login gesperrt."
            : reason.Trim();

        await _settings.SaveManyAsync(
            new Dictionary<string, string>
            {
                [SuspendedSetting] = "true",
                [RemainingRetriesSetting] =
                    retries >= 0 ? retries.ToString() : "",
                [SuspensionReasonSetting] = safeReason
            },
            ct);

        _suspended = true;
        _suspensionReason = safeReason;
        Volatile.Write(ref _remainingRetries, retries);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _settings.SaveManyAsync(
            new Dictionary<string, string>
            {
                [ValueSetting] = "",
                [EnabledSetting] = "false",
                [SuspendedSetting] = "false",
                [RemainingRetriesSetting] = "",
                [SuspensionReasonSetting] = ""
            },
            ct);

        _stored = "";
        _suspended = false;
        _suspensionReason = "";
        Volatile.Write(ref _remainingRetries, -1);
    }
}
