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

        // R-4 (B): split by persons distributes whole positions only.
        var table = new[] { pizza, cola, wine, Item(4, "Salat", 1000, 750), Item(5, "Wasser", 2000, 290) };
        var tableTotal = RestaurantSplitCalculator.ByItems(table,
            table.Select(x => new RestaurantSplitSelection(x.Id, x.QuantityMilli)).ToArray()).TotalCents;
        var plan = RestaurantSplitPlanner.DistributeWholeItems(table, 3);
        var shares = plan.Select(x => x.Length == 0 ? 0L : RestaurantSplitCalculator.ByItems(table, x).TotalCents).ToArray();
        var assigned = table.ToDictionary(x => x.Id, _ => 0L);
        foreach (var selection in plan.SelectMany(x => x)) assigned[selection.SessionItemId] += selection.QuantityMilli;
        var largestPiece = Math.Max(1200, 999); // Wein 1,5 l goes whole (1200), else a Pizza
        assert(
            plan.Count == 3 && shares.Sum() == tableTotal &&
            table.All(x => assigned[x.Id] == x.QuantityMilli) &&
            plan.SelectMany(x => x).All(x => x.SessionItemId == 3 || x.QuantityMilli % 1000 == 0),
            "R-4 every piece goes to exactly one person, whole pieces only, and the shares add up to the table exactly");
        assert(
            shares.Max() - shares.Min() <= largestPiece,
            $"R-4 shares are as even as whole positions allow ({string.Join(" / ", shares)})");
        var again = RestaurantSplitPlanner.DistributeWholeItems(table, 3);
        var outOfRange = false;
        try { RestaurantSplitPlanner.DistributeWholeItems(table, 1); } catch (ArgumentOutOfRangeException) { outOfRange = true; }
        var many = RestaurantSplitPlanner.DistributeWholeItems(new[] { pizza }, 4);
        assert(
            plan.Select(x => string.Join(",", x.Select(y => $"{y.SessionItemId}:{y.QuantityMilli}")))
                .SequenceEqual(again.Select(x => string.Join(",", x.Select(y => $"{y.SessionItemId}:{y.QuantityMilli}")))) &&
            outOfRange && many.Count(x => x.Length > 0) == 1,
            "R-4 the plan is deterministic, needs 2-20 persons and leaves persons empty when there are fewer pieces");
        return Task.CompletedTask;
    }
}
