namespace TorPos.Core;

public enum TseProviderTransport
{
    HardwareSdk,
    LocalMiddleware,
    DirectCloudApi
}

public sealed record TseProviderDescriptor(
    string ProviderId,
    string DisplayName,
    TseProviderTransport Transport,
    bool RequiresCloudReleaseGate);

/// <summary>
/// Stable provider identities. Transport and fiscal backend must not be conflated:
/// a local fiskaltrust Middleware installation is not itself a direct cloud API.
/// </summary>
public static class TseProviderCatalog
{
    public const string SwissbitHardware = "SWISSBIT_HARDWARE";
    public const string FiskaltrustLocalMiddleware = "FISKALTRUST_LOCAL_MIDDLEWARE";
    public const string FiskalyDirectCloud = "FISKALY_DIRECT_CLOUD";
    public const string DeutscheFiskalDirectCloud = "DEUTSCHE_FISKAL_DIRECT_CLOUD";

    private static readonly IReadOnlyDictionary<string, TseProviderDescriptor> Providers =
        new Dictionary<string, TseProviderDescriptor>(
            StringComparer.OrdinalIgnoreCase)
        {
            [SwissbitHardware] = new(
                SwissbitHardware,
                "Swissbit Hardware-TSE",
                TseProviderTransport.HardwareSdk,
                RequiresCloudReleaseGate: false),

            [FiskaltrustLocalMiddleware] = new(
                FiskaltrustLocalMiddleware,
                "fiskaltrust Middleware (lokal)",
                TseProviderTransport.LocalMiddleware,
                RequiresCloudReleaseGate: false),

            [FiskalyDirectCloud] = new(
                FiskalyDirectCloud,
                "fiskaly Cloud-TSE (direkte API)",
                TseProviderTransport.DirectCloudApi,
                RequiresCloudReleaseGate: true),

            [DeutscheFiskalDirectCloud] = new(
                DeutscheFiskalDirectCloud,
                "Deutsche Fiskal Cloud-TSE (direkte API)",
                TseProviderTransport.DirectCloudApi,
                RequiresCloudReleaseGate: true)
        };

    public static TseProviderDescriptor Get(string providerId)
    {
        providerId = (providerId ?? "").Trim();

        if (!Providers.TryGetValue(providerId, out var provider))
        {
            throw new InvalidOperationException(
                "Unbekannter TSE-Provider. Es wird keine TSE-Art automatisch geraten.");
        }

        return provider;
    }

    public static IReadOnlyList<TseProviderDescriptor> All() =>
        Providers.Values
            .OrderBy(x => x.ProviderId, StringComparer.Ordinal)
            .ToArray();

    public static string? CloudVendorForProvider(string providerId)
    {
        var provider = Get(providerId);

        if (provider.Transport != TseProviderTransport.DirectCloudApi)
            return null;

        return provider.ProviderId switch
        {
            FiskalyDirectCloud => CloudTseVendors.Fiskaly,
            DeutscheFiskalDirectCloud => CloudTseVendors.DeutscheFiskal,
            _ => null
        };
    }

    public static bool IsProviderReleaseValidated(string providerId)
    {
        var provider = Get(providerId);

        if (!provider.RequiresCloudReleaseGate)
            return true;

        var vendor = CloudVendorForProvider(provider.ProviderId);

        return vendor is not null &&
               CloudTseRelease.IsValidated(vendor);
    }

    /// <summary>
    /// Provider-specific gate only. Global fiscal production release remains a
    /// separate decision. Local Middleware is deliberately not treated as a
    /// cloud provider merely because its backend could be configured that way.
    /// </summary>
    public static void RequireProviderRelease(string providerId)
    {
        var provider = Get(providerId);

        if (!provider.RequiresCloudReleaseGate)
            return;

        var vendor = CloudVendorForProvider(provider.ProviderId);

        if (vendor is null ||
            !CloudTseRelease.IsValidated(vendor))
        {
            throw new InvalidOperationException(
                CloudTseRelease.NotReleasedMessage(
                    vendor ?? provider.ProviderId));
        }
    }
}
