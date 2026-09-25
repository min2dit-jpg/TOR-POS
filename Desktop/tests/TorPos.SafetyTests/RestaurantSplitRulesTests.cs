using TorPos.Core;

// Restaurant split rules that need no table, TSE or payment: R-6 (the same
// position selected twice) and R-9.1 (no fractions of whole-piece positions
// until the remainder carries its own cents).
public static class RestaurantSplitRulesTests
{
    internal static RestaurantSessionItem Item(long id, string name, long quantityMilli, long unitPriceCents) =>
        new(id, "session-split", "token-" + id, id, name, "", quantityMilli, unitPriceCents, 19m, 0,
            RestaurantSessionItemState.Active, "KELLNER", DateTimeOffset.UtcNow, 1);

    public static Task Run(Action<bool, string> assert)
    {
        var pizza = Item(1, "Pizza", 1000, 999);
        var cola = Item(2, "Cola", 3000, 350);
        var wine = Item(3, "Wein offen", 1500, 800);
        var items = new[] { pizza, cola, wine };

        string Refusal(params RestaurantSplitSelection[] selections)
        {
            try { RestaurantSplitCalculator.ByItems(items, selections); return ""; }
            catch (InvalidOperationException ex) { return ex.Message; }
        }

        assert(
            Refusal(new RestaurantSplitSelection(2, 1000), new RestaurantSplitSelection(2, 1000))
                .Contains("mehrfach ausgewählt", StringComparison.Ordinal),
            "R-6 the same position selected twice is refused with a clear German message");

        assert(
            Refusal(new RestaurantSplitSelection(1, 500)).Contains("ganzen Stück", StringComparison.Ordinal) &&
            Refusal(new RestaurantSplitSelection(2, 1333)).Contains("ganzen Stück", StringComparison.Ordinal),
            "R-9.1 half or a third of a whole-piece position is refused (5,00 + 5,00 for a 9,99 EUR pizza)");

        var whole = RestaurantSplitCalculator.ByItems(items, new[]
        {
            new RestaurantSplitSelection(2, 2000),
            new RestaurantSplitSelection(3, 500)
        });
        assert(
            whole.TotalCents == 700 + 400 && whole.Lines.Count == 2,
            "R-9.1 whole pieces and fractions of a position not counted in pieces stay allowed");
        return Task.CompletedTask;
    }
}
