using System.Linq;
using TorPos.Core;

// R95: Im-Haus/Außer-Haus VAT rule (§12 UStG). Food eaten on the premises
// is taxed at the standard rate (19%) instead of the reduced rate (7%) a
// takeaway sale of the same item carries; drinks are already 19% either
// way and must never be touched by this rule. The swap happens exactly
// once, in CheckoutSnapshot.CopyLines, right when the live cart is
// captured for checkout - everything downstream (receipt MwSt breakdown,
// BusinessManagementService tax reports, FiscalProcessData VAT-class
// bucketing) already groups generically by CartLine.VatRate, so nothing
// else needed to change. The customer-facing gross price never changes -
// CartLine.LineTotalCents depends only on Quantity*UnitPriceCents.
public static class R95ReviewTests
{
    public static Task Run(Action<bool, string> assert, Func<Func<Task>, string, Task> reject)
    {
        assert(
            ImHausVat.Effective(7m, imHaus: true) == 19m,
            "R95 a reduced-rate (7%) item becomes standard-rate (19%) when Im Haus is on");

        assert(
            ImHausVat.Effective(7m, imHaus: false) == 7m,
            "R95 a reduced-rate item keeps its rate when Im Haus is off (Außer Haus, the default)");

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
            eatIn[0].VatRate == 19m && eatIn[1].VatRate == 19m,
            "R95 Im Haus bumps the food line to 19% and leaves the already-19% drink unchanged");

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
