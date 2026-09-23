using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// Provider-specific direct-cloud HTTP clients implement this boundary.
/// Credentials are intentionally absent from this abstraction and must remain
/// inside the provider client/secret store implementation.
/// </summary>
public interface IDirectCloudTseClient
{
    bool IsConfigured { get; }
    bool ExportAvailable { get; }

    Task<TseProbeResult> ProbeAsync(
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

    Task<TseExportResult> ExportAsync(
        string targetPath,
        CancellationToken ct = default);
}

/// <summary>
/// Common fail-closed adapter for direct Cloud TSE providers.
/// Every operation capable of touching a provider is release-gated before the
/// injected client is called. No fallback transaction number, counter or
/// signature is ever synthesized here.
/// </summary>
public sealed class DirectCloudTseProvider : ITseProvider
{
    private readonly TseProviderDescriptor _descriptor;
    private readonly IDirectCloudTseClient _client;

    public DirectCloudTseProvider(
        string providerId,
        IDirectCloudTseClient client)
    {
        _descriptor = TseProviderCatalog.Get(
            providerId);

        if (_descriptor.Transport !=
            TseProviderTransport.DirectCloudApi)
        {
            throw new InvalidOperationException(
                "DirectCloudTseProvider akzeptiert nur direkte Cloud-API-Provider.");
        }

        _client = client ??
            throw new ArgumentNullException(nameof(client));
    }

    public string ProviderId =>
        _descriptor.ProviderId;

    public string DisplayName =>
        _descriptor.DisplayName;

    public string PreferredProduct =>
        _descriptor.DisplayName;

    public bool SdkAvailable =>
        FiscalRelease.CloudTseValidated &&
        _client.IsConfigured;

    public bool ActivationAvailable => false;

    public bool TransactionAvailable =>
        FiscalRelease.CloudTseValidated &&
        _client.IsConfigured;

    public bool ExportAvailable =>
        FiscalRelease.CloudTseValidated &&
        _client.IsConfigured &&
        _client.ExportAvailable;

    public TseRuntimeStatus GetRuntimeStatus()
    {
        if (!FiscalRelease.CloudTseValidated)
        {
            return new TseRuntimeStatus(
                false,
                false,
                "",
                "",
                "Cloud-TSE ist in diesem Build nicht validiert und bleibt gesperrt.");
        }

        return new TseRuntimeStatus(
            _client.IsConfigured,
            _client.IsConfigured,
            "",
            "",
            _client.IsConfigured
                ? "Cloud-TSE-Client ist konfiguriert."
                : "Cloud-TSE-Client ist nicht vollständig konfiguriert.");
    }

    public Task<IReadOnlyList<string>> FindSdkLibrariesAsync(
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(
            Array.Empty<string>());

    public TseRuntimeStatus ConfigureSdkLibrary(
        string libraryPath) =>
        GetRuntimeStatus();

    public async Task<TseProbeResult> ProbeAsync(
        CancellationToken ct = default)
    {
        FiscalRelease.RequireCloudTse();
        return await _client.ProbeAsync(ct);
    }

    public Task<TseActivationResult> ActivateAsync(
        TseActivationRequest request,
        CancellationToken ct = default)
    {
        FiscalRelease.RequireCloudTse();

        return Task.FromResult(
            new TseActivationResult(
                false,
                "Direkte Cloud-TSE wird beim Provider provisioniert; lokale PIN/PUK-Aktivierung ist nicht vorgesehen."));
    }

    public async Task<TseTransactionResult> StartTransactionAsync(
        TseTransactionStartRequest request,
        CancellationToken ct = default)
    {
        FiscalRelease.RequireCloudTse();
        return await _client.StartTransactionAsync(
            request,
            ct);
    }

    public async Task<TseTransactionResult> UpdateTransactionAsync(
        TseTransactionUpdateRequest request,
        CancellationToken ct = default)
    {
        FiscalRelease.RequireCloudTse();
        return await _client.UpdateTransactionAsync(
            request,
            ct);
    }

    public async Task<TseTransactionResult> FinishTransactionAsync(
        TseTransactionFinishRequest request,
        CancellationToken ct = default)
    {
        FiscalRelease.RequireCloudTse();
        return await _client.FinishTransactionAsync(
            request,
            ct);
    }

    public async Task<TseExportResult> ExportTarAsync(
        string targetPath,
        CancellationToken ct = default)
    {
        FiscalRelease.RequireCloudTse();
        return await _client.ExportAsync(
            targetPath,
            ct);
    }
}
