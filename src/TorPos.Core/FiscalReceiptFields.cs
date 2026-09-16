namespace TorPos.Core;

/// <summary>
/// R122 (audit finding F5): which fields a §6 KassenSichV Beleg must carry,
/// in one place.
///
/// The printed receipt has refused to print without them since R63
/// (StarMcPrint3PrinterService.ValidateFiscalReceipt). The digital receipt -
/// which may be the customer's ONLY copy of the Beleg - printed the line
/// "Elektronischer Beleg gem. §6 KassenSichV" unconditionally and then simply
/// skipped whichever fields happened to be blank. A receipt missing its
/// Transaktionsnummer looked exactly like a complete one.
///
/// Rather than give the digital receipt a second hand-written copy of the same
/// list - the mistake R106 had to undo for the VAT formula - both now ask this
/// function. They act on the answer differently, and that difference is
/// deliberate: the printer refuses to produce the document at all, while the
/// digital page still renders (the customer already scanned the code and an
/// empty page helps nobody) but says plainly that the Beleg is incomplete
/// instead of claiming compliance it cannot show.
/// </summary>
public static class FiscalReceiptFields
{
    public static IReadOnlyList<string> Missing(
        string? companyName,
        string? companyAddress,
        string? easSerial,
        bool tseOutage,
        string? tseSerial,
        string? tseTransactionNumber,
        bool hasSignatureCounter,
        string? verificationValue,
        bool hasProcessStart,
        bool hasProcessEnd)
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(companyName)) missing.Add("Firma");
        if (string.IsNullOrWhiteSpace(companyAddress)) missing.Add("Anschrift");
        if (string.IsNullOrWhiteSpace(easSerial)) missing.Add("eAS-Seriennummer");

        // A genuine TSE outage must be visible on the receipt. In that case the
        // TSE-generated fields are unavailable by definition and must NOT be
        // invented (R121 removed the last place that invented one).
        if (!tseOutage)
        {
            if (string.IsNullOrWhiteSpace(tseSerial)) missing.Add("TSE-Seriennummer");
            if (string.IsNullOrWhiteSpace(tseTransactionNumber)) missing.Add("Transaktionsnummer");
            if (!hasSignatureCounter) missing.Add("Signaturzähler");
            if (string.IsNullOrWhiteSpace(verificationValue)) missing.Add("Prüfwert");
            if (!hasProcessEnd) missing.Add("Vorgangsende");
        }

        // Vorgangsbeginn is the register's own record of when the transaction
        // started, so it is available whatever the TSE is doing.
        if (!hasProcessStart) missing.Add("Vorgangsbeginn");

        return missing;
    }
}
