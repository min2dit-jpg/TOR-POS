namespace TorPos.Core;

/// <summary>
/// R133: the Stamm_TSE data of one TSE (DSFinV-K 3.2.7), as read from the
/// TSE's own TAR export (BSI TR-03153-1): certificate and public key from the
/// certificate file, signature algorithm and log time format from a log
/// message. The serial number is the SHA-256 of the public key (AEAO zu § 146a
/// Nr. 2.2.3.2), which is how the certificate is matched to the TSE.
/// </summary>
public sealed record TseMasterData(
    string SerialNumber,
    string SignatureAlgorithm,
    string SignatureAlgorithmOid,
    string LogTimeFormat,
    string ProcessDataEncoding,
    string PublicKeyBase64,
    string CertificateBase64);

public static class TseSignatureAlgorithms
{
    /// <summary>
    /// BSI TR-03111 object identifiers of the algorithms DSFinV-K Anhang E
    /// lists for TSE_SIG_ALGO. An OID not in this list is kept as OID and not
    /// given a name.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        ["0.4.0.127.0.7.1.1.4.1.2"] = "ecdsa-plain-SHA224",
        ["0.4.0.127.0.7.1.1.4.1.3"] = "ecdsa-plain-SHA256",
        ["0.4.0.127.0.7.1.1.4.1.4"] = "ecdsa-plain-SHA384",
        ["0.4.0.127.0.7.1.1.4.1.5"] = "ecdsa-plain-SHA512",
        ["0.4.0.127.0.7.1.1.4.1.8"] = "ecdsa-plain-SHA3-224",
        ["0.4.0.127.0.7.1.1.4.1.9"] = "ecdsa-plain-SHA3-256",
        ["0.4.0.127.0.7.1.1.4.1.10"] = "ecdsa-plain-SHA3-384",
        ["0.4.0.127.0.7.1.1.4.1.11"] = "ecdsa-plain-SHA3-512",
    };

    public static string NameOf(string oid) => Names.TryGetValue(oid, out var name) ? name : "";
}
