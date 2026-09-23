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

    public SwissbitHardwareTseProvider(
        ISwissbitSdkBridge? bridge = null,
        Func<string>? timeAdminPin = null)
    {
        _bridge = bridge ??
            new SwissbitWatchdogBridge();

        _timeAdminPin = timeAdminPin;
    }

    // Every transaction request has carried a TimeAdminPin field from the
    // start, and not one of the six construction sites ever filled it. The
    // effect only shows on real hardware: when the TSE's own clock is no longer
    // valid, PrepareForTransaction needs that PIN to call updateTime, and
    // without it every signature fails with 0x1002 until somebody walks the
    // full activation screen.
    //
    // Filling it here covers all six sites at once and keeps the signing
    // services unaware of a PIN they have no business holding. A request that
    // already carries one is left exactly as it is.
    private string TimeAdminPin(string existing)
    {
        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        try
        {
            return _timeAdminPin?.Invoke() ?? "";
        }
        catch
        {
            // No stored PIN is a documented outage; a throw here would be a
            // crash in the middle of a sale.
            return "";
        }
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

    public Task<TseTransactionResult> StartTransactionAsync(
        TseTransactionStartRequest request,
        CancellationToken ct = default) =>
        _bridge.StartTransactionAsync(
            request with { TimeAdminPin = TimeAdminPin(request.TimeAdminPin) },
            ct);

    public Task<TseTransactionResult> UpdateTransactionAsync(
        TseTransactionUpdateRequest request,
        CancellationToken ct = default) =>
        _bridge.UpdateTransactionAsync(
            request with { TimeAdminPin = TimeAdminPin(request.TimeAdminPin) },
            ct);

    public Task<TseTransactionResult> FinishTransactionAsync(
        TseTransactionFinishRequest request,
        CancellationToken ct = default) =>
        _bridge.FinishTransactionAsync(
            request with { TimeAdminPin = TimeAdminPin(request.TimeAdminPin) },
            ct);

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
