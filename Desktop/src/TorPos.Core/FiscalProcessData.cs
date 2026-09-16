using System.Globalization;

namespace TorPos.Core;

/// <summary>
/// Builds the TSE "ProcessData" payload for one completed sale, submitted as
/// a single Start+Finish "Beleg" transaction (process type
/// <see cref="KassenbelegProcessType"/>).
///
/// IMPORTANT - draft, not yet legally validated:
/// This follows the general field shape described by BSI TR-03153 /
/// DSFinV-K for a Kassenbeleg process ("Beleg^Zeitstempel^Betrag-Angaben^
/// UStKlasse-Angaben"), but has NOT been verified byte-for-byte against the
/// official BSI TR-03153 Anlage, a real Hardware TSE 2, or the DSFinV-K test
/// tool. Treat this as a starting point only. It must be explicitly
/// re-verified - and this comment removed once confirmed - before
/// FiscalComplianceService's ksichvReceiptValidated/dsfinvkImplementedAndValidated
/// gates are ever flipped away from false.
/// </summary>
public static class FiscalProcessData
{
    public const string KassenbelegProcessType = "Kassenbeleg-V1";
    public const string BestellungProcessType = "Bestellung-V1";

    public static byte[] BuildKassenbeleg(Sale sale)
    {
        ArgumentNullException.ThrowIfNull(sale);

        // R108: must use the shared, discount-prorated calculator, not a raw
        // per-VatRate sum of LineTotalCents - the latter ignores
        // sale.DiscountCents entirely, so a discounted sale's VAT-class
        // breakdown here would sum to MORE than Betrag-Summe below (which
        // already reflects the discount via sale.TotalCents), producing an
        // internally inconsistent TSE-signed Beleg. Same bug family as
        // R103's digital receipt and R82's partial return, found on a
        // review of every remaining independent VatRate-grouping site.
        var vatGroups = VatSummaryCalculator.Compute(sale.Lines, sale.DiscountCents)
            .OrderByDescending(x => x.Rate)
            .Select(g => $"{VatClass(g.Rate)}:{Amount(g.GrossCents)}")
            .ToArray();

        // R101: Sale.CashPortionCents/CardPortionCents are always populated
        // (Cash=(Total,0), Card=(0,Total), Mixed=(X,Total-X)), so a split
        // sale emits BOTH tags with their real portions instead of one
        // amount tag for the whole total under a single tender type.
        var paymentField = string.Join("_", new[]
        {
            sale.CashPortionCents > 0 ? $"Bar:{Amount(sale.CashPortionCents)}" : null,
            sale.CardPortionCents > 0 ? $"Unbar:{Amount(sale.CardPortionCents)}" : null
        }.Where(x => x is not null));
        if (paymentField.Length == 0)
            paymentField = sale.PaymentMethod == PaymentMethod.Cash ? $"Bar:{Amount(0)}" : $"Unbar:{Amount(0)}";

        // A STORNO/RETURN must be distinguishable from an ordinary sale of the
        // same items - both the Vorgang marker and the reference back to the
        // original Beleg-Nr matter for later DSFinV-K classification (an
        // AVBelegstorno without its Referenz-Beleg-Nr would be indistinguishable
        // from a duplicate sale). This was missing from the first R78/R79 draft
        // and only found on a later review pass.
        var isReversal = sale.TransactionType is "STORNO" or "RETURN";

        // R121: a Teilretoure used to be signed as "AVBelegabbruch", which
        // means an ABORTED process - a receipt that was broken off before it
        // completed, with no money moved. A Retoure is the opposite: a
        // completed transaction that hands money back, and it is modelled as a
        // normal Beleg carrying negative positions, not as an abort. Signing
        // it as an abort would misclassify every partial return.
        //
        // The Referenz-Beleg-Nr below still links it to the original receipt,
        // which is what makes the negative booking traceable.
        var vorgang = sale.TransactionType switch
        {
            "STORNO" => "AVBelegstorno",
            "RETURN" => "Beleg",
            _ => "Beleg"
        };

        var fields = new List<string>
        {
            vorgang,
            sale.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffK", CultureInfo.InvariantCulture),
            $"Betrag-Summe:{Amount(sale.TotalCents)}_{paymentField}",
            vatGroups.Length > 0 ? string.Join("_", vatGroups) : $"{VatClass(19m)}:{Amount(0)}",
            $"Beleg-Nr:{sale.ReceiptNumber}",
        };

        if (isReversal && sale.OriginalReceiptNumber is long originalReceipt)
            fields.Add($"Referenz-Beleg-Nr:{originalReceipt}");

        return System.Text.Encoding.UTF8.GetBytes(string.Join("^", fields));
    }

    /// <summary>
    /// R83: ProcessData for accepting an IMBISS ORDER, signed as its own
    /// "Bestellung-V1" Vorgang - separate from the "Kassenbeleg-V1" signed
    /// later at payment (BuildKassenbeleg, via SaleFiscalSigningService).
    /// Same DRAFT caveat as BuildKassenbeleg applies.
    /// </summary>
    public static byte[] BuildBestellung(ParkedReceipt order)
    {
        ArgumentNullException.ThrowIfNull(order);

        // R108: same fix as BuildKassenbeleg - use the shared calculator so a
        // discounted parked order's VAT-class breakdown stays consistent
        // with Betrag-Summe below, not just a raw per-VatRate sum.
        var vatGroups = VatSummaryCalculator.Compute(order.Lines, order.DiscountCents)
            .OrderByDescending(x => x.Rate)
            .Select(g => $"{VatClass(g.Rate)}:{Amount(g.GrossCents)}")
            .ToArray();

        var fields = new[]
        {
            "Bestellung",
            order.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffK", CultureInfo.InvariantCulture),
            $"Betrag-Summe:{Amount(order.TotalCents)}",
            vatGroups.Length > 0 ? string.Join("_", vatGroups) : $"{VatClass(19m)}:{Amount(0)}",
            $"Park-Nr:{order.ParkNumber}",
        };

        return System.Text.Encoding.UTF8.GetBytes(string.Join("^", fields));
    }

    private static string Amount(long cents) =>
        (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    private static string VatClass(decimal rate) => rate switch
    {
        19m => "UStNormal",
        7m => "UStErmaessigt",
        0m => "UStNull",
        _ => "UStSonstige"
    };
}
