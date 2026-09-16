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
            processData.Contains("UStNormal:107.10") && !processData.Contains("UStNormal:119.00"),
            $"R108 FiscalProcessData.BuildKassenbeleg's VAT-class tag reflects the discount (107,10 EUR), not the raw pre-discount gross (119,00 EUR) - actual: {processData}");
        assert(
            processData.Contains("Betrag-Summe:107.10"),
            "R108 the discounted sale's Betrag-Summe and its own VAT-class tag now agree (both 107,10 EUR), not internally inconsistent");

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
            undiscountedProcessData.Contains("UStNormal:119.00") && undiscountedProcessData.Contains("Betrag-Summe:119.00"),
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
        assert(
            orderProcessData.Contains("UStNormal:107.10") && !orderProcessData.Contains("UStNormal:119.00"),
            $"R108 FiscalProcessData.BuildBestellung's VAT-class tag also reflects a discounted parked order's real total - actual: {orderProcessData}");

        return Task.CompletedTask;
    }
}
