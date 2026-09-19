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
                companyName: normalizedCompany,
                companyAddress: normalizedAddress,
                easSerial: evidence.CashRegisterSerial,
                tseOutage: false,
                tseSerial: evidence.TseSerialNumber,
                tseTransactionNumber: evidence.TransactionNumber,
                hasSignatureCounter: true,
                verificationValue: evidence.Signature,
                hasProcessStart:
                    !string.IsNullOrWhiteSpace(
                        evidence.ProcessStartTime),
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
            BusinessProcessStartRaw: evidence.ProcessStartTime,
            BusinessProcessEndRaw: evidence.SignatureLogTime,
            TrainingReceipt: true,
            ExternalReceiptId: response.FtReceiptIdentification,
            MiddlewareHeaderLines:
                (response.FtReceiptHeader ?? Array.Empty<string>())
                    .ToArray(),
            MiddlewareChargeItemLines:
                (response.FtChargeItems ??
                 Array.Empty<FiskaltrustChargeItem>())
                    .Select(FormatChargeSupplement)
                    .ToArray(),
            MiddlewareChargeLines:
                (response.FtChargeLines ?? Array.Empty<string>())
                    .ToArray(),
            MiddlewarePayItemLines:
                (response.FtPayItems ??
                 Array.Empty<FiskaltrustPayItem>())
                    .Select(FormatPaySupplement)
                    .ToArray(),
            MiddlewarePayLines:
                (response.FtPayLines ?? Array.Empty<string>())
                    .ToArray(),
            MiddlewareRequiredSignatureLines:
                RequiredQrModeSignatureLines(response),
            MiddlewareTextFallbackSignatureLines:
                TextFallbackSignatureLines(response),
            MiddlewareFooterLines:
                (response.FtReceiptFooter ?? Array.Empty<string>())
                    .ToArray());
    }

    private static string FormatChargeSupplement(
        FiskaltrustChargeItem item) =>
        $"fiskaltrust Zusatz: {GermanFormat.Number(item.Quantity, "0.###")} x " +
        $"{item.Description} · {GermanFormat.Number(item.Amount, "0.00")} EUR" +
        (item.VatRate == 0m
            ? ""
            : $" · MwSt {GermanFormat.Number(item.VatRate, "0.##")} %");

    private static string FormatPaySupplement(
        FiskaltrustPayItem item) =>
        $"fiskaltrust Zahlung: {item.Description} · " +
        $"{GermanFormat.Number(item.Amount, "0.00")} EUR";

    private static IReadOnlyList<string> RequiredQrModeSignatureLines(
        FiskaltrustReceiptResponse response)
    {
        var required =
            FiskaltrustGermanReceiptProjection
                .PrintableSignatures(response, preferQr: true)
                .Where(x =>
                    x.FtSignatureType !=
                        FiskaltrustDeSignatureTypes.KassenSichVQrPayload)
                .Where(x =>
                    x.FtSignatureType !=
                        FiskaltrustDeSignatureTypes.ProcessStartTime &&
                    x.FtSignatureType !=
                        FiskaltrustDeSignatureTypes.SignatureLogTime)
                .Where(x => !string.IsNullOrWhiteSpace(x.Data))
                .ToArray();

        EnsureTextRenderable(required, "QR-Pflichtsignatur");

        return required
            .Select(FormatSignatureLine)
            .ToArray();
    }

    private static IReadOnlyList<string> TextFallbackSignatureLines(
        FiskaltrustReceiptResponse response)
    {
        var fallback =
            FiskaltrustGermanReceiptProjection
                .PrintableSignatures(response, preferQr: false)
                .Where(x =>
                    x.FtSignatureType !=
                        FiskaltrustDeSignatureTypes.KassenSichVQrPayload)
                .Where(x =>
                    x.FtSignatureType !=
                        FiskaltrustDeSignatureTypes.ProcessStartTime &&
                    x.FtSignatureType !=
                        FiskaltrustDeSignatureTypes.SignatureLogTime)
                .Where(x => !string.IsNullOrWhiteSpace(x.Data))
                .ToArray();

        EnsureTextRenderable(fallback, "Text-Fallback-Signatur");

        return fallback
            .Select(FormatSignatureLine)
            .ToArray();
    }

    private static void EnsureTextRenderable(
        IReadOnlyList<FiskaltrustSignatureItem> items,
        string context)
    {
        var unsupported = items.FirstOrDefault(x =>
            FiskaltrustSignatureFormats.BaseFormat(x.FtSignatureFormat) !=
                FiskaltrustSignatureFormats.Text);

        if (unsupported is not null)
        {
            throw new InvalidOperationException(
                $"Druckjob abgelehnt: {context} 0x{unsupported.FtSignatureType:X16} " +
                $"hat ein nicht unterstütztes Format 0x{unsupported.FtSignatureFormat:X}.");
        }
    }

    private static string FormatSignatureLine(
        FiskaltrustSignatureItem item)
    {
        var caption = string.IsNullOrWhiteSpace(item.Caption)
            ? item.FtSignatureType switch
            {
                FiskaltrustDeSignatureTypes.CertificationIdentification =>
                    "TSE-Zertifizierung",
                FiskaltrustDeSignatureTypes.TseSerialNumber =>
                    "TSE-Seriennummer",
                _ => $"ftSignature 0x{item.FtSignatureType:X16}"
            }
            : item.Caption;

        return caption + ": " + item.Data;
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
