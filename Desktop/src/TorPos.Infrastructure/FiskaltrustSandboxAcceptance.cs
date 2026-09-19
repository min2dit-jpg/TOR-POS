using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed record FiskaltrustSandboxAcceptanceReport(
    bool Passed,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    FiskaltrustGermanFiscalEvidence Evidence);

/// <summary>
/// Deterministic acceptance checks for the first real German fiskaltrust POS
/// receipt. This is a sandbox comparison tool, not a production gate yet.
/// </summary>
public static class FiskaltrustSandboxAcceptance
{
    public static FiskaltrustSandboxAcceptanceReport ValidateSimpleCashSale(
        Sale sale,
        FiskaltrustReceiptRequest request,
        FiskaltrustReceiptResponse response)
    {
        ArgumentNullException.ThrowIfNull(sale);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        var errors = new List<string>();
        var warnings = new List<string>();
        var evidence = FiskaltrustGermanReceiptProjection.Extract(response);

        if (string.IsNullOrWhiteSpace(request.CbReceiptReference))
            errors.Add("Request ohne cbReceiptReference.");

        if (!string.Equals(
                request.CbReceiptReference,
                response.CbReceiptReference,
                StringComparison.Ordinal))
        {
            errors.Add(
                $"ReceiptReference stimmt nicht überein: request='{request.CbReceiptReference}', response='{response.CbReceiptReference}'.");
        }

        if (string.IsNullOrWhiteSpace(response.FtQueueItemId))
            errors.Add("ftQueueItemID fehlt.");

        if (string.IsNullOrWhiteSpace(response.FtReceiptIdentification))
            errors.Add("ftReceiptIdentification fehlt.");

        if (!FiskaltrustDeState.IsGerman(response.FtState))
        {
            errors.Add(
                $"ftState ist kein deutscher fiskaltrust-Status: 0x{response.FtState:X16}.");
        }
        else
        {
            if (FiskaltrustDeState.HasFlag(
                    response.FtState,
                    FiskaltrustDeState.TseCommunicationFailedFlag))
            {
                errors.Add("TSE-Kommunikation laut ftState fehlgeschlagen.");
            }

            if (FiskaltrustDeState.HasFlag(
                    response.FtState,
                    FiskaltrustDeState.ScuSwitchingFlag))
            {
                errors.Add("SCU befindet sich laut ftState im Wechselzustand.");
            }

            if (!FiskaltrustDeState.IsReady(response.FtState) &&
                errors.Count == 0)
            {
                warnings.Add(
                    $"Unbekannte deutsche ftState-Zusatzflags: 0x{FiskaltrustDeState.Flags(response.FtState):X12}.");
            }
        }

        if (!evidence.HasQrPayload)
            errors.Add("KassenSichV QR-Payload fehlt.");

        if (!evidence.HasTextFiscalCore)
            errors.Add("Textuelle TSE-Kerndaten sind unvollständig.");

        if (string.IsNullOrWhiteSpace(evidence.TseSerialNumber))
            errors.Add("TSE-Seriennummer fehlt.");

        if (string.IsNullOrWhiteSpace(evidence.CertificationIdentification))
            errors.Add("TSE-Zertifizierungskennung fehlt.");

        if (string.IsNullOrWhiteSpace(evidence.ProcessStartTime))
            errors.Add("Vorgangsbeginn (process-start) fehlt.");

        if (!string.Equals(
                evidence.ProcessType,
                FiscalProcessData.KassenbelegProcessType,
                StringComparison.Ordinal))
        {
            errors.Add(
                $"ProcessType erwartet '{FiscalProcessData.KassenbelegProcessType}', erhalten '{evidence.ProcessType}'.");
        }

        string expectedProcessData;
        try
        {
            expectedProcessData = FiscalProcessData.KassenbelegText(sale);
        }
        catch (Exception ex)
        {
            errors.Add("TOR ProcessData konnte nicht gebildet werden: " + ex.Message);
            expectedProcessData = "";
        }

        if (expectedProcessData.Length > 0 &&
            !string.Equals(
                evidence.ProcessData,
                expectedProcessData,
                StringComparison.Ordinal))
        {
            errors.Add(
                $"ProcessData weicht von TOR/DSFinV-K ab. Erwartet '{expectedProcessData}', erhalten '{evidence.ProcessData}'.");
        }

        if (evidence.HasQrPayload)
        {
            var rebuilt = evidence.BuildComparableQrPayload();
            if (rebuilt.Length == 0)
                errors.Add("QR-Payload kann aus den Einzel-SignatureItems nicht vollständig rekonstruiert werden.");
            else if (!string.Equals(rebuilt, evidence.QrPayload, StringComparison.Ordinal))
                errors.Add("QR-Payload stimmt nicht exakt mit den einzelnen TSE-SignatureItems überein.");
        }

        if (!ulong.TryParse(evidence.TransactionNumber, out var transaction) ||
            transaction == 0)
        {
            errors.Add("TSE-Transaktionsnummer fehlt oder ist ungültig.");
        }

        if (!ulong.TryParse(evidence.SignatureCounter, out var counter) ||
            counter == 0)
        {
            errors.Add("TSE-Signaturzähler fehlt oder ist ungültig.");
        }

        return new FiskaltrustSandboxAcceptanceReport(
            errors.Count == 0,
            errors,
            warnings,
            evidence);
    }
}
