using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// Maps an accepted fiskaltrust German AVTraining response to TOR's existing
/// 80 mm receipt model without reformatting the signed QR/time evidence.
/// </summary>
public static class FiskaltrustTrainingReceiptPrintJobFactory
{
    public static ReceiptPrintJob Build(
        Sale sale,
        FiskaltrustReceiptRequest request,
        FiskaltrustReceiptResponse response,
        string companyName,
        string companyAddress,
        string taxNumber = "",
        string vatId = "",
        string logoPath = "",
        bool autoCut = true)
    {
        ArgumentNullException.ThrowIfNull(sale);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        if ((request.FtReceiptCase &
             FiskaltrustDeCases.TrainingReceiptFlag) == 0)
        {
            throw new InvalidOperationException(
                "Druckjob abgelehnt: fiskaltrust ReceiptRequest ist kein AVTraining-Beleg.");
        }

        var acceptance =
            FiskaltrustSandboxAcceptance.ValidateSimpleCashSale(
                sale,
                request,
                response);

        if (!acceptance.Passed)
        {
            throw new InvalidOperationException(
                "Druckjob abgelehnt: fiskaltrust Acceptance fehlgeschlagen: " +
                string.Join(" · ", acceptance.Errors));
        }

        var evidence = acceptance.Evidence;

        if (!long.TryParse(
                evidence.SignatureCounter,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var signatureCounter) ||
            signatureCounter <= 0)
        {
            throw new InvalidOperationException(
                "Druckjob abgelehnt: Signaturzähler fehlt oder ist ungültig.");
        }

        var normalizedCompany = companyName?.Trim() ?? "";
        var normalizedAddress = companyAddress?.Trim() ?? "";
        var missing =
            FiscalReceiptFields.Missing(
                normalizedCompany,
                normalizedAddress,
                evidence.CashRegisterSerial,
                tseOutage: false,
                evidence.TseSerialNumber,
                evidence.TransactionNumber,
                hasSignatureCounter: true,
                evidence.Signature,
                hasProcessStart:
                    !string.IsNullOrWhiteSpace(
                        evidence.TransactionStartTime),
                hasProcessEnd:
                    !string.IsNullOrWhiteSpace(
                        evidence.SignatureLogTime));

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Druckjob abgelehnt: Fiskal-/Firmendaten fehlen: " +
                string.Join(", ", missing));
        }

        return new ReceiptPrintJob(
            ReceiptNumber: 0,
            CreatedAt: sale.CreatedAt,
            CompanyName: normalizedCompany,
            CompanyAddress: normalizedAddress,
            TaxNumber: taxNumber?.Trim() ?? "",
            VatId: vatId?.Trim() ?? "",
            Header:
                "FISKALTRUST HARDWARE-ABNAHME\n" +
                "TRAININGSBON · AVTraining",
            Footer:
                "TRAININGSBON · KEIN PRODUKTIVUMSATZ\n" +
                "Nur für TSE-/Middleware-Abnahme",
            PaymentLabel: "BAR · TRAINING",
            DiscountCents: 0,
            TotalCents: sale.TotalCents,
            Lines: sale.Lines.Select(CloneLine).ToArray(),
            FiscalTestMode: false,
            EasSerial: evidence.CashRegisterSerial,
            TseSerial: evidence.TseSerialNumber,
            TseTransactionNumber: evidence.TransactionNumber,
            SignatureCounter: signatureCounter,
            ProcessStart: null,
            ProcessEnd: null,
            VerificationValue: evidence.Signature,
            TseOutage: false,
            OperatorName: sale.OperatorName,
            TenderedCents: sale.TotalCents,
            ChangeCents: 0,
            LogoPath: logoPath?.Trim() ?? "",
            PickupNumber: 0,
            TseQrCode: true,
            AutoCut: autoCut,
            OpenCashDrawer: false,
            OrderStart: null,
            TseClientId: evidence.CashRegisterSerial,
            TseProcessType: evidence.ProcessType,
            TseProcessData: evidence.ProcessData,
            TseStartLogTime: null,
            TseSignatureAlgorithm: evidence.SignatureAlgorithm,
            TseLogTimeFormat: evidence.LogTimeFormat,
            TsePublicKey: evidence.PublicKey,
            TseQrPayloadOverride: evidence.QrPayload,
            TseProcessStartRaw: evidence.TransactionStartTime,
            TseProcessEndRaw: evidence.SignatureLogTime,
            TrainingReceipt: true,
            ExternalReceiptId: response.FtReceiptIdentification);
    }

    private static CartLine CloneLine(CartLine line) =>
        new()
        {
            ProductId = line.ProductId,
            ProductName = line.ProductName,
            VariantName = line.VariantName,
            Barcode = line.Barcode,
            Quantity = line.Quantity,
            UnitPriceCents = line.UnitPriceCents,
            ListUnitPriceCents = line.EffectiveListUnitPriceCents,
            VatRate = line.VatRate,
            ImHausApplicable = line.ImHausApplicable,
            PfandCents = line.PfandCents,
            PromotionId = line.PromotionId,
            PromotionName = line.PromotionName,
            PromotionPercent = line.PromotionPercent,
            PromotionDiscountUnitCents = line.PromotionDiscountUnitCents,
            PromotionStartDate = line.PromotionStartDate,
            PromotionEndDate = line.PromotionEndDate
        };
}
