using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// TOR POS standard TSE provider for Swissbit Hardware-TSE.
///
/// The provider is now wired to a runtime WORM API bridge. TOR itself does
/// not redistribute Swissbit SDK files; the official SDK must be obtained
/// from Swissbit / the authorized TSE supplier and installed into TOR.
/// </summary>
public sealed class SwissbitHardwareTseProvider : ITseProvider
{
    private readonly ISwissbitSdkBridge _bridge;
    private readonly Func<string>? _timeAdminPin;
    private readonly TseTimeAdminPinStore? _timeAdminPinStore;
    private readonly TseClockSafetyState? _clockSafety;

    public SwissbitHardwareTseProvider(
        ISwissbitSdkBridge? bridge = null,
        Func<string>? timeAdminPin = null,
        TseTimeAdminPinStore? timeAdminPinStore = null,
        TseClockSafetyState? clockSafety = null)
    {
        _bridge = bridge ??
            new SwissbitWatchdogBridge();

        _timeAdminPin = timeAdminPin;
        _timeAdminPinStore = timeAdminPinStore;
        _clockSafety = clockSafety;
    }

    private sealed record PreparedTimeAdminPin(
        string Pin,
        bool FromStoredPin,
        string WithheldReason);

    private PreparedTimeAdminPin PrepareTimeAdminPin(
        string existing)
    {
        string candidate;
        var fromStored = false;

        if (!string.IsNullOrWhiteSpace(existing))
        {
            candidate = existing;
        }
        else if (_timeAdminPinStore is not null)
        {
            if (!_timeAdminPinStore.HasStoredValue)
                return new("", false, "");

            if (_timeAdminPinStore.Suspended)
            {
                var reason =
                    _timeAdminPinStore.SuspensionReason.Length > 0
                        ? _timeAdminPinStore.SuspensionReason
                        : "Gespeicherte TimeAdmin-PIN ist nach einem fehlgeschlagenen Login gesperrt. PIN prüfen und neu speichern.";
                return new("", false, reason);
            }

            if (_timeAdminPinStore.RemainingRetries is <= 1)
            {
                return new(
                    "",
                    false,
                    "Automatische TimeAdmin-PIN-Anmeldung gesperrt: höchstens ein TSE-Versuch verbleibt. PIN zuerst manuell prüfen.");
            }

            candidate = _timeAdminPinStore.Current;
            fromStored = candidate.Length > 0;
        }
        else
        {
            try
            {
                candidate = _timeAdminPin?.Invoke() ?? "";
            }
            catch
            {
                candidate = "";
            }
        }

        if (candidate.Length == 0)
            return new("", false, "");

        if (_clockSafety is not null)
        {
            var clock =
                _clockSafety.Assess(
                    DateTimeOffset.UtcNow);

            if (!clock.Allowed)
                return new("", false, clock.Message);
        }

        return new(candidate, fromStored, "");
    }

    private async Task<TseTransactionResult> CompleteTransactionAsync(
        TseTransactionResult result,
        PreparedTimeAdminPin prepared,
        CancellationToken ct)
    {
        if (result.TimeAdminPinRejected &&
            prepared.FromStoredPin &&
            _timeAdminPinStore is not null)
        {
            var retryText =
                result.TimeAdminRemainingRetries is { } retries
                    ? $" Verbleibende TSE-Versuche: {retries}."
                    : "";

            var reason =
                "Gespeicherte TimeAdmin-PIN wurde nach fehlgeschlagenem Login automatisch gesperrt." +
                retryText +
                " PIN in den TSE-Einstellungen prüfen und ausdrücklich neu speichern.";

            await _timeAdminPinStore.SuspendAsync(
                result.TimeAdminRemainingRetries,
                reason,
                ct);

            return result with { Message = reason };
        }

        if (!result.Success &&
            prepared.WithheldReason.Length > 0)
        {
            return result with
            {
                Message = prepared.WithheldReason
            };
        }

        if (result.Success)
            _clockSafety?.Observe(result.LogTime);

        return result;
    }

    public string ProviderId =>
        "SWISSBIT_HARDWARE";

    public string DisplayName =>
        "Swissbit Hardware-TSE";

    public string PreferredProduct =>
        "Swissbit Hardware TSE 2 · USB";

    public bool SdkAvailable =>
        _bridge.IsAvailable;

    public bool ActivationAvailable =>
        _bridge.ActivationAvailable;

    public bool TransactionAvailable =>
        _bridge.TransactionAvailable;

    public bool ExportAvailable =>
        _bridge.ExportAvailable;

    public TseRuntimeStatus GetRuntimeStatus() =>
        _bridge.GetRuntimeStatus();

    public Task<IReadOnlyList<string>> FindSdkLibrariesAsync(
        CancellationToken ct = default) =>
        _bridge.FindInstalledLibrariesAsync(ct);

    public TseRuntimeStatus ConfigureSdkLibrary(
        string libraryPath) =>
        _bridge.ConfigureLibrary(libraryPath);

    public async Task<TseProbeResult> ProbeAsync(
        CancellationToken ct = default)
    {
        if (!_bridge.IsAvailable)
        {
            var runtime = _bridge.GetRuntimeStatus();

            // The state stays SdkMissing - without the WORM API nothing can be
            // signed, and that must not be softened. But the stick is a USB
            // volume before it is an API, so a TSE that is physically plugged
            // in can be named even here. Saying only "SDK fehlt" to somebody
            // holding their new TSE reads as "your device is not there".
            // It also lands in the outage record, which is where a Pruefer
            // later reads what the till actually saw.
            var mounts = SwissbitDeviceScan.FindMountPoints();

            return new TseProbeResult(
                TseConnectionState.SdkMissing,
                mounts.Count > 0
                    ? $"TSE erkannt auf {string.Join(", ", mounts)} · Swissbit SDK fehlt: {runtime.Message}"
                    : runtime.Message);
        }

        try
        {
            var devices =
                await _bridge.FindDevicesAsync(ct);

            if (devices.Count == 0)
            {
                return new TseProbeResult(
                    TseConnectionState.NotFound,
                    "Swissbit SDK ist geladen, aber keine unterstützte Hardware-TSE wurde gefunden.");
            }

            var device =
                devices.FirstOrDefault(d =>
                    string.Equals(
                        d.FormFactor,
                        "USB",
                        StringComparison.OrdinalIgnoreCase)) ??
                devices[0];

            var ready =
                string.Equals(
                    device.InitializationState,
                    "INITIALIZED",
                    StringComparison.OrdinalIgnoreCase) &&
                device.SelfTestPassed &&
                device.ValidTime &&
                device.CtssActive;

            return new TseProbeResult(
                ready
                    ? TseConnectionState.Ready
                    : TseConnectionState.Connected,
                ready
                    ? $"Swissbit TSE bereit: {device.SerialNumber}"
                    : $"Swissbit TSE erkannt: {device.SerialNumber} · Status {device.InitializationState}",
                device);
        }
        catch (Exception ex)
        {
            return new TseProbeResult(
                TseConnectionState.Error,
                $"Swissbit TSE-Prüfung fehlgeschlagen: {ex.Message}");
        }
    }

    public async Task<TseActivationResult> ActivateAsync(
        TseActivationRequest request,
        CancellationToken ct = default)
    {
        if (!_bridge.ActivationAvailable)
        {
            return new TseActivationResult(
                false,
                "Aktivierung nicht möglich: Swissbit WORM API ist nicht vollständig verfügbar.");
        }

        try
        {
            return await _bridge.ActivateAsync(
                request,
                ct);
        }
        finally
        {
            // Admin PIN / TimeAdmin PIN / PUK / CredentialSeed are never
            // persisted or logged by this provider.
        }
    }

    public async Task<TseTransactionResult> StartTransactionAsync(
        TseTransactionStartRequest request,
        CancellationToken ct = default)
    {
        var prepared =
            PrepareTimeAdminPin(request.TimeAdminPin);

        var result =
            await _bridge.StartTransactionAsync(
                request with
                {
                    TimeAdminPin = prepared.Pin
                },
                ct);

        return await CompleteTransactionAsync(
            result,
            prepared,
            ct);
    }

    public async Task<TseTransactionResult> UpdateTransactionAsync(
        TseTransactionUpdateRequest request,
        CancellationToken ct = default)
    {
        var prepared =
            PrepareTimeAdminPin(request.TimeAdminPin);

        var result =
            await _bridge.UpdateTransactionAsync(
                request with
                {
                    TimeAdminPin = prepared.Pin
                },
                ct);

        return await CompleteTransactionAsync(
            result,
            prepared,
            ct);
    }

    public async Task<TseTransactionResult> FinishTransactionAsync(
        TseTransactionFinishRequest request,
        CancellationToken ct = default)
    {
        var prepared =
            PrepareTimeAdminPin(request.TimeAdminPin);

        var result =
            await _bridge.FinishTransactionAsync(
                request with
                {
                    TimeAdminPin = prepared.Pin
                },
                ct);

        return await CompleteTransactionAsync(
            result,
            prepared,
            ct);
    }

    public Task<TseExportResult> ExportTarAsync(
        string targetPath,
        CancellationToken ct = default) =>
        _bridge.ExportTarAsync(
            targetPath,
            ct);
}

/// <summary>
/// Vendor isolation layer. The concrete TOR implementation dynamically
/// loads the officially obtained Swissbit WORM API binaries.
/// </summary>
public interface ISwissbitSdkBridge
{
    bool IsAvailable { get; }
    bool ActivationAvailable { get; }
    bool TransactionAvailable { get; }
    bool ExportAvailable { get; }

    TseRuntimeStatus GetRuntimeStatus();

    Task<IReadOnlyList<string>> FindInstalledLibrariesAsync(
        CancellationToken ct = default);

    TseRuntimeStatus ConfigureLibrary(
        string libraryPath);

    Task<IReadOnlyList<TseDeviceInfo>> FindDevicesAsync(
        CancellationToken ct = default);

    Task<TseActivationResult> ActivateAsync(
        TseActivationRequest request,
        CancellationToken ct = default);

    Task<TseTransactionResult> StartTransactionAsync(
        TseTransactionStartRequest request,
        CancellationToken ct = default);

    Task<TseTransactionResult> UpdateTransactionAsync(
        TseTransactionUpdateRequest request,
        CancellationToken ct = default);

    Task<TseTransactionResult> FinishTransactionAsync(
        TseTransactionFinishRequest request,
        CancellationToken ct = default);

    Task<TseExportResult> ExportTarAsync(
        string targetPath,
        CancellationToken ct = default);
}
