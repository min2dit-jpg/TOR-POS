namespace TorPos.Infrastructure;

/// <summary>
/// Read-only projection of a German fiskaltrust ReceiptResponse.
///
/// This intentionally does not write TOR sale/TSE state yet. It gives the
/// sandbox integration one deterministic place to preserve the exact values
/// returned by the Middleware so they can later be compared with the real
/// Swissbit receipt and TOR's existing receipt/DSFinV-K representation.
/// </summary>
public sealed record FiskaltrustGermanFiscalEvidence(
    string QrPayload,
    string QrVersion,
    string CashRegisterSerial,
    string ProcessType,
    string ProcessData,
    string TransactionNumber,
    string SignatureCounter,
    string TransactionStartTime,
    string SignatureLogTime,
    string SignatureAlgorithm,
    string LogTimeFormat,
    string Signature,
    string PublicKey,
    string ProcessStartTime,
    string CertificationIdentification,
    string TseSerialNumber)
{
    public bool HasQrPayload => !string.IsNullOrWhiteSpace(QrPayload);

    public bool HasTextFiscalCore =>
        !string.IsNullOrWhiteSpace(CashRegisterSerial) &&
        !string.IsNullOrWhiteSpace(ProcessType) &&
        !string.IsNullOrWhiteSpace(ProcessData) &&
        !string.IsNullOrWhiteSpace(TransactionNumber) &&
        !string.IsNullOrWhiteSpace(SignatureCounter) &&
        !string.IsNullOrWhiteSpace(TransactionStartTime) &&
        !string.IsNullOrWhiteSpace(SignatureLogTime) &&
        !string.IsNullOrWhiteSpace(SignatureAlgorithm) &&
        !string.IsNullOrWhiteSpace(LogTimeFormat) &&
        !string.IsNullOrWhiteSpace(Signature);

    /// <summary>
    /// Rebuild the DSFinV-K V0 QR payload from the individual values returned
    /// by fiskaltrust. No timestamps/signatures are reformatted; comparison is
    /// byte-for-text exact apart from the joining semicolons.
    /// </summary>
    public string BuildComparableQrPayload()
    {
        var fields = new[]
        {
            QrVersion,
            CashRegisterSerial,
            ProcessType,
            ProcessData,
            TransactionNumber,
            SignatureCounter,
            TransactionStartTime,
            SignatureLogTime,
            SignatureAlgorithm,
            LogTimeFormat,
            Signature,
            PublicKey
        };

        return fields.Any(string.IsNullOrWhiteSpace)
            ? ""
            : string.Join(";", fields);
    }
}

public static class FiskaltrustGermanReceiptProjection
{
    public static FiskaltrustGermanFiscalEvidence Extract(
        FiskaltrustReceiptResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        string Data(ulong type) =>
            response.FtSignatures
                .FirstOrDefault(x => x.FtSignatureType == type)
                ?.Data?.Trim() ?? "";

        return new FiskaltrustGermanFiscalEvidence(
            Data(FiskaltrustDeSignatureTypes.KassenSichVQrPayload),
            Data(FiskaltrustDeSignatureTypes.QrVersion),
            Data(FiskaltrustDeSignatureTypes.CashRegisterSerial),
            Data(FiskaltrustDeSignatureTypes.ProcessType),
            Data(FiskaltrustDeSignatureTypes.ProcessData),
            Data(FiskaltrustDeSignatureTypes.TransactionNumber),
            Data(FiskaltrustDeSignatureTypes.SignatureCounter),
            Data(FiskaltrustDeSignatureTypes.TransactionStartTime),
            Data(FiskaltrustDeSignatureTypes.SignatureLogTime),
            Data(FiskaltrustDeSignatureTypes.SignatureAlgorithm),
            Data(FiskaltrustDeSignatureTypes.LogTimeFormat),
            Data(FiskaltrustDeSignatureTypes.Signature),
            Data(FiskaltrustDeSignatureTypes.PublicKey),
            Data(FiskaltrustDeSignatureTypes.ProcessStartTime),
            Data(FiskaltrustDeSignatureTypes.CertificationIdentification),
            Data(FiskaltrustDeSignatureTypes.TseSerialNumber));
    }

    /// <summary>
    /// Returns exactly the additional signature items TOR would have to
    /// visualize if fiskaltrust becomes the receipt source.
    ///
    /// When a real QR compliance item is available and QR output is preferred,
    /// items explicitly flagged as optional are omitted. Without QR output,
    /// nothing is silently discarded.
    /// </summary>
    public static IReadOnlyList<FiskaltrustSignatureItem> PrintableSignatures(
        FiskaltrustReceiptResponse response,
        bool preferQr)
    {
        ArgumentNullException.ThrowIfNull(response);

        var hasQr = response.FtSignatures.Any(x =>
            x.FtSignatureType == FiskaltrustDeSignatureTypes.KassenSichVQrPayload &&
            FiskaltrustSignatureFormats.BaseFormat(x.FtSignatureFormat) ==
                FiskaltrustSignatureFormats.QrCode &&
            !string.IsNullOrWhiteSpace(x.Data));

        if (!preferQr || !hasQr)
            return response.FtSignatures.ToArray();

        return response.FtSignatures
            .Where(x =>
                !FiskaltrustSignatureFormats.IsOptionalWhenQrIsPrinted(
                    x.FtSignatureFormat))
            .ToArray();
    }
}
