using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed record DirectCloudTseConfiguration(
    string ProviderId,
    string Endpoint,
    string MandantId,
    string TssId,
    string ClientId);

public static class DirectCloudTseConfigurationPolicy
{
    /// <summary>
    /// Validates configuration for a fiscal cloud operation.
    /// The release gate is intentionally checked first: an unvalidated build
    /// must not probe credentials, tenant/TSS identities or endpoints.
    /// </summary>
    public static DirectCloudTseConfiguration RequireForFiscalUse(
        DirectCloudTseConfiguration configuration)
    {
        FiscalRelease.RequireCloudTse();

        ArgumentNullException.ThrowIfNull(configuration);

        var provider = TseProviderCatalog.Get(
            configuration.ProviderId);

        if (provider.Transport !=
            TseProviderTransport.DirectCloudApi)
        {
            throw new InvalidOperationException(
                "Der gewählte TSE-Provider ist keine direkte Cloud-API.");
        }

        if (!provider.RequiresCloudReleaseGate)
        {
            throw new InvalidOperationException(
                "Direkter Cloud-Provider besitzt keine Cloud-Freigabeklassifizierung.");
        }

        var endpoint = (configuration.Endpoint ?? "").Trim();
        var mandantId = (configuration.MandantId ?? "").Trim();
        var tssId = (configuration.TssId ?? "").Trim();
        var clientId = (configuration.ClientId ?? "").Trim();

        if (!Uri.TryCreate(
                endpoint,
                UriKind.Absolute,
                out var uri) ||
            !string.Equals(
                uri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Cloud-TSE-Endpoint muss eine absolute HTTPS-Adresse sein.");
        }

        if (mandantId.Length == 0)
        {
            throw new InvalidOperationException(
                "Cloud-TSE-Mandant fehlt.");
        }

        if (tssId.Length == 0)
        {
            throw new InvalidOperationException(
                "Cloud-TSE-TSS-ID fehlt.");
        }

        if (clientId.Length == 0)
        {
            throw new InvalidOperationException(
                "Cloud-TSE-Client-ID fehlt.");
        }

        return configuration with
        {
            ProviderId = provider.ProviderId,
            Endpoint = uri.ToString().TrimEnd('/'),
            MandantId = mandantId,
            TssId = tssId,
            ClientId = clientId
        };
    }
}
