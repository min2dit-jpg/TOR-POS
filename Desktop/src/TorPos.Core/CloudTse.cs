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

/// <summary>
/// The cloud TSE vendors TOR can be qualified against. A vendor is the company
/// that operates the certified signing device: you sign a contract with them,
/// they give you an endpoint, credentials and a tenant, and their REST contract
/// is theirs alone - there is no common standard between them.
/// </summary>
public static class CloudTseVendors
{
    public const string Fiskaltrust = "FISKALTRUST";
    public const string Fiskaly = "FISKALY";
    public const string DeutscheFiskal = "DEUTSCHE_FISKAL";

    public static IReadOnlyList<string> All =>
        [Fiskaltrust, Fiskaly, DeutscheFiskal];

    /// <summary>
    /// Anything that is not a vendor TOR knows stays exactly as typed, and is
    /// never validated. A name nobody recognises must not resolve to a name
    /// somebody does.
    /// </summary>
    public static string Normalize(string? value)
    {
        var trimmed = (value ?? "").Trim().ToUpperInvariant();

        foreach (var vendor in All)
        {
            if (string.Equals(trimmed, vendor, StringComparison.Ordinal))
                return vendor;
        }

        return trimmed;
    }
}

/// <summary>
/// Cloud TSE qualification, one flag per vendor.
///
/// A single cloud flag would have been a trap: qualifying against one vendor's
/// sandbox would have opened production for every other vendor too, including
/// ones nobody had ever run a transaction against. The REST contracts, the
/// failure modes and the certifications are separate, so the release decisions
/// are separate.
///
/// An unknown or empty vendor is never validated. That is the important
/// property: the default answer to "may this till sign through the cloud" is
/// no, and it stays no until somebody names a vendor AND flips that vendor's
/// flag after qualifying it.
/// </summary>
public static class CloudTseRelease
{
    public const bool FiskaltrustValidated = false;
    public const bool FiskalyValidated = false;
    public const bool DeutscheFiskalValidated = false;

    public static bool IsValidated(string? vendor)
    {
        var fiskaltrust = FiskaltrustValidated;
        var fiskaly = FiskalyValidated;
        var deutscheFiskal = DeutscheFiskalValidated;

        return CloudTseVendors.Normalize(vendor) switch
        {
            CloudTseVendors.Fiskaltrust => fiskaltrust,
            CloudTseVendors.Fiskaly => fiskaly,
            CloudTseVendors.DeutscheFiskal => deutscheFiskal,
            _ => false
        };
    }

    public static string NotReleasedMessage(string? vendor)
    {
        var normalized = CloudTseVendors.Normalize(vendor);

        if (normalized.Length == 0)
            return "Cloud-TSE: kein Anbieter ausgewählt. Es wird nichts signiert.";

        return CloudTseVendors.All.Contains(normalized)
            ? $"Cloud-TSE über {normalized} ist in diesem Build nicht freigegeben. Es wird nichts signiert."
            : $"Cloud-TSE-Anbieter '{normalized}' ist TOR nicht bekannt und nicht freigegeben. Es wird nichts signiert.";
    }
}
