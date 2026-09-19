namespace TorPos.Core;

/// <summary>
/// Controlled preparation for the real BAR/TSE acceptance test.
///
/// This type deliberately does not write to the checkout journal, sales tables or
/// the TSE. It creates the exact one-item cash receipt that will later be used by
/// the hardware acceptance flow and validates the evidence returned by that flow.
///
/// Rationale: an unpersisted "test" Kassenbeleg transaction on a real TSE would
/// create a fiscal record that TOR could not map back to its own immutable data.
/// Preparation therefore stays pure until the dedicated hardware acceptance
/// operation records both sides together.
/// </summary>
public sealed record BarTestBonPlan(
    decimal VatRate,
    CheckoutSnapshot Snapshot,
    Sale Sale,
    string ExpectedProcessType,
    string ExpectedProcessData);

public sealed record BarTestBonCheck(
    string Name,
    bool Passed,
    string Message);

public sealed record BarTestBonValidationReport(
    IReadOnlyList<BarTestBonCheck> Checks)
{
    public bool Passed => Checks.Count > 0 && Checks.All(x => x.Passed);
}

public static class BarTestBonPreparation
{
    public const long TestAmountCents = 100;

    public static BarTestBonPlan Create(decimal vatRate, string? operationId = null)
    {
        if (vatRate is not 7m and not 19m)
            throw new ArgumentOutOfRangeException(nameof(vatRate), "BAR TESTBON erlaubt nur 7 % oder 19 %.");

        var started = DateTimeOffset.Now;
        var line = new CartLine
        {
            ProductId = vatRate == 19m ? -150019 : -150007,
            ProductName = vatRate == 19m ? "TOR BAR TEST 19%" : "TOR BAR TEST 7%",
            Quantity = 1m,
            UnitPriceCents = TestAmountCents,
            ListUnitPriceCents = TestAmountCents,
            VatRate = vatRate,
            ImHausApplicable = false,
            PfandCents = 0
        };

        var snapshot = new CheckoutSnapshot(
            operationId ?? "BARTEST-" + Guid.NewGuid().ToString("N"),
            new[] { line },
            DiscountCents: 0,
            Method: PaymentMethod.Cash,
            OperatorName: "DIAGNOSE",
            ParkedReceiptId: null,
            ImHaus: false,
            CashPortionCents: 0,
            TseVorgangId: "",
            StartedAt: started,
            CancelledLines: Array.Empty<CartLine>());

        var sale = new Sale
        {
            ReceiptNumber = 0,
            CreatedAt = started,
            PaymentMethod = PaymentMethod.Cash,
            CashPortionCents = TestAmountCents,
            CardPortionCents = 0,
            DiscountCents = 0,
            ListSubtotalCents = TestAmountCents,
            PromotionDiscountCents = 0,
            TotalCents = TestAmountCents,
            TransactionType = "SALE",
            ImHaus = false,
            Lines = CheckoutSnapshot.CopyLines(snapshot.Lines),
            FiscalStatus = "DIAGNOSTIC_PREPARATION",
            OperatorName = "DIAGNOSE",
            StartedAt = started,
            CancelledLines = Array.Empty<CartLine>()
        };

        return new BarTestBonPlan(
            vatRate,
            snapshot,
            sale,
            FiscalProcessData.KassenbelegProcessType,
            FiscalProcessData.KassenbelegText(sale));
    }

    /// <summary>
    /// Print-only 80 mm preparation receipt. It is unmistakably a simulation:
    /// no fiscal receipt number, no TSE transaction and no cash-drawer pulse.
    /// </summary>
    public static ReceiptPrintJob BuildPreviewReceipt(
        BarTestBonPlan plan,
        string companyName,
        string companyAddress,
        string logoPath = "") =>
        new(
            ReceiptNumber: 0,
            CreatedAt: DateTimeOffset.Now,
            CompanyName: string.IsNullOrWhiteSpace(companyName) ? "TOR POS" : companyName.Trim(),
            CompanyAddress: companyAddress?.Trim() ?? "",
            TaxNumber: "",
            VatId: "",
            Header:
                "BAR TESTBON · VORBEREITUNG\n" +
                "KEINE TSE-TRANSAKTION\n" +
                $"MwSt {plan.VatRate:0} % · 1 Artikel · BAR",
            Footer:
                "NUR HARDWARE-VORBEREITUNG\n" +
                "Keine echte Buchung / kein Umsatz\n" +
                $"Erwartet: {plan.ExpectedProcessType}\n{plan.ExpectedProcessData}",
            PaymentLabel: "BAR · TEST",
            DiscountCents: 0,
            TotalCents: plan.Snapshot.TotalCents,
            Lines: CheckoutSnapshot.CopyLines(plan.Snapshot.Lines),
            FiscalTestMode: true,
            OperatorName: "DIAGNOSE",
            TenderedCents: plan.Snapshot.TotalCents,
            ChangeCents: 0,
            LogoPath: logoPath ?? "",
            TseQrCode: false,
            AutoCut: true,
            OpenCashDrawer: false);

    /// <summary>
    /// Validates the evidence of the future real hardware acceptance operation.
    /// The Swissbit API does not echo processData in its result, therefore the
    /// exact bytes TOR submitted are supplied separately and compared here.
    /// </summary>
    public static BarTestBonValidationReport ValidateHardwareEvidence(
        BarTestBonPlan plan,
        SaleTseResult result,
        string sentProcessType,
        string sentProcessData,
        string tseVorgangState,
        string qrPayload,
        string expectedTseSerial = "",
        string expectedClientId = "")
    {
        var checks = new List<BarTestBonCheck>();

        Add(
            "PROCESS_TYPE",
            string.Equals(sentProcessType, plan.ExpectedProcessType, StringComparison.Ordinal),
            $"Soll={plan.ExpectedProcessType} · Ist={sentProcessType}");

        Add(
            "PROCESS_DATA",
            string.Equals(sentProcessData, plan.ExpectedProcessData, StringComparison.Ordinal),
            $"Soll={plan.ExpectedProcessData} · Ist={sentProcessData}");

        var signatureOk = result.Signed && !string.IsNullOrWhiteSpace(result.Signature);
        Add(
            "TSE_SIGNED",
            signatureOk,
            signatureOk
                ? "TSE-Signatur bestätigt."
                : result.Signed ? "TSE meldet Erfolg, aber der Prüfwert/die Signatur fehlt." : result.OutageMessage);

        var clientIdOk =
            !string.IsNullOrWhiteSpace(result.ClientId) &&
            (string.IsNullOrWhiteSpace(expectedClientId) ||
             string.Equals(result.ClientId, expectedClientId, StringComparison.Ordinal));
        Add(
            "TSE_CLIENT_ID",
            clientIdOk,
            string.IsNullOrWhiteSpace(result.ClientId)
                ? "Client-ID fehlt."
                : string.IsNullOrWhiteSpace(expectedClientId) || clientIdOk
                    ? result.ClientId
                    : $"Erwartet={expectedClientId} · Ist={result.ClientId}");

        var serialOk =
            !string.IsNullOrWhiteSpace(result.SerialNumber) &&
            (string.IsNullOrWhiteSpace(expectedTseSerial) ||
             string.Equals(result.SerialNumber, expectedTseSerial, StringComparison.Ordinal));
        Add(
            "TSE_SERIAL",
            serialOk,
            string.IsNullOrWhiteSpace(result.SerialNumber)
                ? "TSE-Seriennummer fehlt."
                : string.IsNullOrWhiteSpace(expectedTseSerial) || serialOk
                    ? result.SerialNumber
                    : $"Erwartet={expectedTseSerial} · Ist={result.SerialNumber}");

        var transactionOk =
            ulong.TryParse(result.TransactionNumber, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var transactionNumber) &&
            transactionNumber > 0;
        Add(
            "TRANSACTION_NUMBER",
            transactionOk,
            transactionOk ? result.TransactionNumber : "Transaktionsnummer fehlt oder ist 0.");

        var counterOk =
            ulong.TryParse(result.SignatureCounter, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var signatureCounter) &&
            signatureCounter > 0;
        Add(
            "SIGNATURE_COUNTER",
            counterOk,
            counterOk ? result.SignatureCounter : "Signaturzähler fehlt oder ist 0.");

        var timeOk =
            result.StartLogTime is { } start &&
            result.LogTime is { } end &&
            end >= start;
        Add(
            "TSE_TIMES",
            timeOk,
            timeOk
                ? $"{TseReceiptTime.Format(result.StartLogTime!.Value)} → {TseReceiptTime.Format(result.LogTime!.Value)}"
                : "Start-/Endzeit fehlt oder Reihenfolge ist ungültig.");

        Add(
            "TSE_STATE",
            string.Equals(tseVorgangState, "FINISHED", StringComparison.Ordinal),
            string.IsNullOrWhiteSpace(tseVorgangState) ? "TSE-Vorgangszustand fehlt." : tseVorgangState);

        var qrOk = QrMatches(plan, result, qrPayload, out var qrMessage);
        Add("QR_PAYLOAD", qrOk, qrMessage);

        return new BarTestBonValidationReport(checks);

        void Add(string name, bool passed, string message) =>
            checks.Add(new BarTestBonCheck(name, passed, message));
    }

    private static bool QrMatches(
        BarTestBonPlan plan,
        SaleTseResult result,
        string qrPayload,
        out string message)
    {
        if (string.IsNullOrWhiteSpace(qrPayload))
        {
            message = "QR-Payload fehlt.";
            return false;
        }

        var fields = qrPayload.Split(';');
        if (fields.Length != 12)
        {
            message = $"QR-Feldanzahl {fields.Length}; erwartet 12.";
            return false;
        }

        var start = result.StartLogTime is { } s ? TseReceiptTime.Format(s) : "";
        var end = result.LogTime is { } e ? TseReceiptTime.Format(e) : "";

        var ok =
            fields[0] == TseQrCodePayload.Version &&
            fields[1] == result.ClientId &&
            fields[2] == plan.ExpectedProcessType &&
            fields[3] == plan.ExpectedProcessData &&
            fields[4] == result.TransactionNumber &&
            fields[5] == result.SignatureCounter &&
            fields[6] == start &&
            fields[7] == end &&
            !string.IsNullOrWhiteSpace(fields[8]) &&
            !string.IsNullOrWhiteSpace(fields[9]) &&
            !string.IsNullOrWhiteSpace(fields[10]) &&
            fields[10] == result.Signature &&
            !string.IsNullOrWhiteSpace(fields[11]);

        message = ok
            ? "DSFinV-K Anhang-I QR stimmt mit TOR/TSE-Daten überein."
            : "QR-Payload stimmt nicht vollständig mit ProcessData/TSE-Ergebnis überein.";
        return ok;
    }
}
