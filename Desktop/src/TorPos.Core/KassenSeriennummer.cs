namespace TorPos.Core;

/// <summary>
/// R144: the one serial number of the till.
///
/// The same number has to appear wherever the till is identified:
/// - in the TSE as the client id of every transaction (§ 2 Satz 2 Nr. 8
///   KassenSichV, AEAO zu § 146a Nr. 2.2.3.1);
/// - on the receipt (AEAO Nr. 2.4.4 Nr. 6) and in its QR code ("Seriennummer
///   (Client-Id) der Kasse", DSFinV-K Anhang I);
/// - in the DSFinV-K export as KASSE_SERIENNR, which expects "die
///   Identifikationsnummer …, die … gemäß § 146a Abs. 4 AO zu melden ist";
/// - in the notification to the tax office (AEAO Nr. 1.16.2.5).
///
/// TOR's internal identity (<c>system_identity.eas_serial</c>, "TORPOS-" and 32
/// hex digits) is 39 characters long, the TSE client id at most 30. The serial
/// number is the identity cut to 30 characters: "TORPOS-" and 23 hex digits
/// (92 random bits), assigned by TOR as manufacturer and unique per installation.
/// The identity itself stays the DSFinV-K Z_KASSE_ID.
/// </summary>
public static class KassenSeriennummer
{
    /// <summary>Longest client id a TSE accepts (the TOR settings have always stated 30 ASCII characters).</summary>
    public const int MaxLength = 30;

    public static string From(string easSerial)
    {
        var serial = (easSerial ?? "").Trim();
        return serial.Length <= MaxLength ? serial : serial[..MaxLength];
    }

    /// <summary>
    /// A client id / serial the TSE and the DSFinV-K accept: printable ASCII,
    /// at most 30 characters, no "/" and no "_" (DSFinV-K KASSE_SERIENNR).
    /// </summary>
    public static bool IsValid(string serial) =>
        !string.IsNullOrEmpty(serial) &&
        serial.Length <= MaxLength &&
        serial.All(ch => ch > ' ' && ch <= '~') &&
        !serial.Contains('/') && !serial.Contains('_');

    /// <summary>The TSE client id configured for this till is its serial number.</summary>
    public static bool ClientIdMatches(string? clientId, string easSerial) =>
        string.Equals((clientId ?? "").Trim(), From(easSerial), StringComparison.Ordinal);
}
