namespace TorPos.Core;

/// <summary>
/// Which kind of TSE this till signs with. The rest of the program does not
/// ask: everything above ITseProvider - outage handling, DSFinV-K, the receipt
/// fields, the signature counter - is written against the interface, so the
/// choice is meant to be invisible outside this file and the composition root.
/// </summary>
public static class TseProviderKind
{
    public const string SwissbitUsb = "SWISSBIT_USB";
    public const string Cloud = "CLOUD";

    public const string Setting = "tse.provider";

    /// <summary>
    /// Unknown or empty falls back to the hardware TSE. A typo in a settings
    /// row must never silently move a till onto a different fiscal device.
    /// </summary>
    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Cloud, StringComparison.OrdinalIgnoreCase)
            ? Cloud
            : SwissbitUsb;
}

/// <summary>
/// What a cloud TSE needs to be addressed at all. No secret lives in here in
/// clear text: the API key is held DPAPI-protected and only unwrapped at the
/// moment a request is signed.
///
/// TenantId and QueueId are carried explicitly because the single most
/// dangerous mistake with a cloud TSE is a till signing into the wrong tenant's
/// queue - the receipts look valid and belong to somebody else's books.
/// </summary>
public sealed record CloudTseConfiguration(
    string Vendor,
    string BaseUrl,
    string TenantId,
    string QueueId,
    string ClientId,
    string ProtectedApiKey)
{
    public static readonly CloudTseConfiguration Empty =
        new("", "", "", "", "", "");

    public bool IsAddressable =>
        !string.IsNullOrWhiteSpace(Vendor) &&
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        !string.IsNullOrWhiteSpace(TenantId) &&
        !string.IsNullOrWhiteSpace(QueueId) &&
        !string.IsNullOrWhiteSpace(ClientId);

    public string MissingPart()
    {
        if (string.IsNullOrWhiteSpace(Vendor)) return "Anbieter";
        if (string.IsNullOrWhiteSpace(BaseUrl)) return "Endpunkt";
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)) return "Endpunkt";
        if (uri.Scheme != Uri.UriSchemeHttps) return "HTTPS";
        if (string.IsNullOrWhiteSpace(TenantId)) return "Mandant";
        if (string.IsNullOrWhiteSpace(QueueId)) return "Queue";
        if (string.IsNullOrWhiteSpace(ClientId)) return "Client-ID";
        return "";
    }
}

public sealed record CloudTseHealth(
    bool Reachable,
    int StatusCode,
    long RoundTripMs,
    string Message);
