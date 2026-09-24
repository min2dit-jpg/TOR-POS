using System.Linq;
using TorPos.Core;

// R95/R2026: Im-Haus/Außer-Haus VAT transition (§ 12 UStG).
// The 19% Im-Haus elevation applied in 2024/2025. From 01.01.2026,
// restaurant/catering food is reduced again; drinks remain 19%.
// CheckoutSnapshot.CopyLines still centralizes the effective VAT snapshot,
// while historical tests pass an explicit service date.
public static class R95ReviewTests
{
    public static Task Run(Action<bool, string> assert, Func<Func<Task>, string, Task> reject)
    {
        assert(
            ImHausVat.Effective(
                7m,
                imHaus: true,
                categoryApplies: true,
                serviceDate: new DateOnly(2025, 6, 1)) == 19m,
            "R95 historical 2024/2025: a reduced-rate food item becomes 19% when Im Haus is on");

        assert(
            ImHausVat.Effective(
                7m,
                imHaus: true,
                categoryApplies: true,
                serviceDate: new DateOnly(2026, 1, 1)) == 7m,
            "R2026 from 01.01.2026: restaurant food stays at the reduced 7% rate even when Im Haus is on");

        assert(
            ImHausVat.Effective(19m, imHaus: true) == 19m,
            "R95 a drink already at 19% is left untouched by Im Haus - it was never reduced-rate to begin with");

        assert(
            ImHausVat.Effective(0m, imHaus: true) == 0m,
            "R95 a 0% VAT line (e.g. Pfand-only) is never bumped to 19% by Im Haus");

        var cart = new[]
        {
            new CartLine { ProductId = 1, ProductName = "Döner", Quantity = 1, UnitPriceCents = 600, VatRate = 7m },
            new CartLine { ProductId = 2, ProductName = "Cola", Quantity = 1, UnitPriceCents = 300, VatRate = 19m }
        };

        var takeaway = CheckoutSnapshot.CopyLines(cart, imHaus: false);
        assert(
            takeaway[0].VatRate == 7m && takeaway[1].VatRate == 19m,
            "R95 Außer Haus (default) leaves the food line at 7% and the drink at 19%");

        var eatIn = CheckoutSnapshot.CopyLines(cart, imHaus: true);
        assert(
            eatIn[0].VatRate == 7m && eatIn[1].VatRate == 19m,
            "R2026 current Im Haus keeps food at 7% and leaves the already-19% drink unchanged");

        assert(
            eatIn[0].UnitPriceCents == takeaway[0].UnitPriceCents &&
            eatIn.Sum(x => x.LineTotalCents) == takeaway.Sum(x => x.LineTotalCents),
            "R95 the customer-facing price never changes between Außer Haus and Im Haus - only the internal VAT split does");

        assert(
            new CheckoutSnapshot("op", takeaway, 0, PaymentMethod.Cash, "tester", null).ImHaus == false,
            "R95 CheckoutSnapshot.ImHaus defaults to false (Außer Haus) for any caller that doesn't set it explicitly");

        return Task.CompletedTask;
    }
}
