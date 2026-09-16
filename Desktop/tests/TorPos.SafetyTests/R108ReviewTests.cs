using TorPos.Core;

// R108: self-directed audit (user delegated "nereden devam edeceğine sen
// karar ver" after two rounds of externally-generated architecture reviews
// that turned out to already be addressed in this codebase). Continued the
// one genuinely productive pattern from this session instead: search for
// any remaining independent, hand-rolled copy of the VAT-by-rate-group
// formula, the exact bug shape R106 already found and fixed twice (R103's
// digital receipt, R82's partial return).
//
// Found a THIRD instance - and the highest-stakes one yet:
// FiscalProcessData.BuildKassenbeleg/BuildBestellung, which builds the
// actual TSE ProcessData payload that gets cryptographically signed as the
// legally-binding Beleg record. Both grouped lines by VatRate and summed
// RAW (pre-discount) LineTotalCents, completely ignoring
// Sale.DiscountCents/ParkedReceipt.DiscountCents - so a discounted sale's
// "Betrag-Summe" tag (which correctly uses the already-discounted
// TotalCents) would not match the sum of its own VAT-class tags, an
// internally inconsistent signed fiscal document. Fixed by using the same
// shared TorPos.Core.VatSummaryCalculator.Compute both other fixes already
// use.
//
// R130: the "Betrag-Summe"/"UStNormal" tags described above were TOR's own
// invention and no longer exist; processData now follows DSFinV-K Anhang I.
// The point of R108 carries over unchanged - the tax containers must add up to
// what was actually paid.
public static class R108ReviewTests
{
    public static Task Run(Action<bool, string> assert)
    {
        // The exact R106 scenario: a 119,00 EUR sale (19% VAT) with a 10%
        // (11,90 EUR) manual discount, actually paid at 107,10 EUR.
        var line = new CartLine { ProductName = "R108 Artikel", Quantity = 1, UnitPriceCents = 11900, VatRate = 19m };
        var discountedSale = new Sale
        {
            Id = 1, ReceiptNumber = 108001, CreatedAt = DateTimeOffset.Now,
            PaymentMethod = PaymentMethod.Cash, TotalCents = 10710, CashPortionCents = 10710,
            DiscountCents = 1190,
            Lines = new[] { line }
        };
        var processData = System.Text.Encoding.UTF8.GetString(FiscalProcessData.BuildKassenbeleg(discountedSale));
        assert(
            processData == "Beleg^107.10_0.00_0.00_0.00_0.00^107.10:Bar",
            $"R108/R130 the 19 % tax container reflects the discount (107,10 EUR), not the raw pre-discount gross (119,00 EUR) - actual: {processData}");
        decimal Sum(IEnumerable<string> amounts) =>
            amounts.Sum(a => decimal.Parse(a, System.Globalization.CultureInfo.InvariantCulture));
        var parts = processData.Split('^');
        assert(
            Sum(parts[1].Split('_')) == Sum(parts[2].Split('_').Select(p => p.Split(':')[0])),
            "R108/R130 the tax containers of a discounted sale add up to its payments, not internally inconsistent");

        // A Bon with NO discount must be completely unaffected - the fix
        // must never change an ordinary (undiscounted) sale's payload.
        var undiscountedSale = new Sale
        {
            Id = 2, ReceiptNumber = 108002, CreatedAt = DateTimeOffset.Now,
            PaymentMethod = PaymentMethod.Cash, TotalCents = 11900, CashPortionCents = 11900,
            Lines = new[] { line }
        };
        var undiscountedProcessData = System.Text.Encoding.UTF8.GetString(FiscalProcessData.BuildKassenbeleg(undiscountedSale));
        assert(
            undiscountedProcessData == "Beleg^119.00_0.00_0.00_0.00_0.00^119.00:Bar",
            "R108 an undiscounted sale's ProcessData is unaffected by the fix");

        // Same fix, same scenario, for BuildBestellung (IMBISS order
        // acceptance's own separate TSE Vorgang).
        var discountedOrder = new ParkedReceipt
        {
            Id = 1, ParkNumber = 108,
            CreatedAt = DateTimeOffset.Now,
            TotalCents = 10710,
            DiscountCents = 1190,
            Lines = new[] { line }
        };
        var orderProcessData = System.Text.Encoding.UTF8.GetString(FiscalProcessData.BuildBestellung(discountedOrder));
        // R130: Bestellung-V1 lists positions at their unit price and carries no
        // tax totals, so an order discount can no longer make it inconsistent;
        // the discount is part of the Kassenbeleg signed at payment.
        assert(
            orderProcessData == "1;\"R108 Artikel\";119.00",
            $"R108/R130 BuildBestellung lists the position itself and no tax total that a discount could contradict - actual: {orderProcessData}");

        return Task.CompletedTask;
    }
}
