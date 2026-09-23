using System.Security.Cryptography;
using System.Text;
using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// The Swissbit TSE refuses to sign once its own clock is no longer valid, and
/// the only way to set that clock is worm_tse_updateTime, which needs the
/// TimeAdmin PIN.
///
/// Every transaction request already carries a TimeAdminPin field, but nothing
/// ever filled it: all six construction sites use the three-argument form. A
/// TSE that sat on a shelf, or a till switched off over a long holiday, would
/// therefore fail with 0x1002 and the only route back was the full activation
/// screen - the one with the PUK box next to it, where a wrong PUK entered
/// twice can block a production TSE for good.
///
/// So the till can be allowed to refresh the clock by itself. That means
/// keeping one PIN, and it is deliberately the weakest of the three: the
/// TimeAdmin PIN can set the time and nothing else. It cannot change PINs,
/// cannot register clients, cannot decommission the TSE. The Admin PIN, the PUK
/// and the credential seed are never stored, here or anywhere else.
///
/// It is off by default and opt-in, because whether a PIN may live on the till
/// at all is the operator's decision, not ours. When on, it is protected with
/// DPAPI for the Windows user that runs the till, exactly like the cloud token.
/// </summary>
public sealed class TseTimeAdminPinStore
{
    public const string EnabledSetting = "tse.time_admin_pin.stored";
    public const string ValueSetting = "tse.time_admin_pin.protected";

    private readonly ISettingsRepository _settings;

    // Read once and held in memory. The checkout path must never wait on a
    // database read to find out whether it may refresh the TSE clock.
    private volatile string _current = "";

    public TseTimeAdminPinStore(ISettingsRepository settings)
    {
        _settings = settings;
    }

    /// <summary>The cached PIN, or an empty string when none is stored.</summary>
    public string Current => _current;

    public bool Enabled => _current.Length > 0;

    /// <summary>
    /// Never throws and never logs the value. A profile copied to another
    /// Windows user cannot decrypt it, and that has to degrade to "no stored
    /// PIN" rather than to a crash on the path that signs a sale.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        _current = "";

        var enabled = await _settings.GetAsync(EnabledSetting, "false", ct);
        if (!string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
            return;

        var stored = await _settings.GetAsync(ValueSetting, "", ct);
        if (string.IsNullOrWhiteSpace(stored))
            return;

        try
        {
            if (!OperatingSystem.IsWindows())
                return;

            _current = Encoding.UTF8.GetString(
                ProtectedData.Unprotect(
                    Convert.FromBase64String(stored),
                    null,
                    DataProtectionScope.CurrentUser));
        }
        catch
        {
            _current = "";
        }
    }

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
                [EnabledSetting] = "true"
            },
            ct);

        _current = trimmed;
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _settings.SaveManyAsync(
            new Dictionary<string, string>
            {
                [ValueSetting] = "",
                [EnabledSetting] = "false"
            },
            ct);

        _current = "";
    }
}
