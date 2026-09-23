using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// The cloud TSE configuration, read once and held in memory.
///
/// Same reason as the TimeAdmin PIN store: nothing on a selling path may wait
/// on a database read to find out how to reach the signing device. The API key
/// stays DPAPI-protected in here and is only unwrapped at the moment a request
/// needs it.
/// </summary>
public sealed class CloudTseSettings
{
    public const string VendorSetting = "tse.cloud.vendor";
    public const string BaseUrlSetting = "tse.cloud.base_url";
    public const string TenantSetting = "tse.cloud.tenant_id";
    public const string QueueSetting = "tse.cloud.queue_id";
    public const string ClientSetting = "tse.cloud.client_id";
    public const string ApiKeySetting = "tse.cloud.api_key.protected";

    private readonly ISettingsRepository _settings;
    private CloudTseConfiguration _current = CloudTseConfiguration.Empty;

    public CloudTseSettings(ISettingsRepository settings)
    {
        _settings = settings;
    }

    public CloudTseConfiguration Current => _current;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        _current = new CloudTseConfiguration(
            await _settings.GetAsync(VendorSetting, "", ct),
            await _settings.GetAsync(BaseUrlSetting, "", ct),
            await _settings.GetAsync(TenantSetting, "", ct),
            await _settings.GetAsync(QueueSetting, "", ct),
            await _settings.GetAsync(ClientSetting, "", ct),
            await _settings.GetAsync(ApiKeySetting, "", ct));
    }

    public async Task SaveAsync(
        CloudTseConfiguration configuration,
        string? newApiKey = null,
        CancellationToken ct = default)
    {
        // An empty key box means "leave the stored key alone", not "delete it".
        // Anything else would wipe a working configuration the first time
        // somebody corrected a typo in the queue id.
        var protectedKey = string.IsNullOrWhiteSpace(newApiKey)
            ? configuration.ProtectedApiKey
            : CloudTseProvider.ProtectApiKey(newApiKey);

        await _settings.SaveManyAsync(
            new Dictionary<string, string>
            {
                [VendorSetting] = configuration.Vendor.Trim(),
                [BaseUrlSetting] = configuration.BaseUrl.Trim(),
                [TenantSetting] = configuration.TenantId.Trim(),
                [QueueSetting] = configuration.QueueId.Trim(),
                [ClientSetting] = configuration.ClientId.Trim(),
                [ApiKeySetting] = protectedKey
            },
            ct);

        _current = configuration with { ProtectedApiKey = protectedKey };
    }
}
