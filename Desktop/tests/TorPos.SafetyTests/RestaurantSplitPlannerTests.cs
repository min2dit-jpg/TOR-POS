using TorPos.Core;

public static class RestaurantSplitPlannerTests
{
    private static RestaurantSessionItem Item(
        long id,
        string name,
        long quantityMilli,
        long unitPriceCents,
        string unit = "Stück") =>
        new(
            id,
            "split-plan",
            "token-" + id,
            id,
            name,
            "",
            quantityMilli,
            unitPriceCents,
            19m,
            0,
            RestaurantSessionItemState.Active,
            "KELLNER",
            DateTimeOffset.UtcNow,
            1)
        {
            Unit = unit,
            PersistedLineTotalCents =
                (long)Math.Round(
                    quantityMilli / 1000m * unitPriceCents,
                    MidpointRounding.AwayFromZero)
        };

    public static Task Run(Action<bool, string> assert)
    {
        var pizza = Item(1, "Pizza", 1000, 999);
        var cola = Item(2, "Cola", 3000, 350);
        var wine = Item(3, "Wein offen", 1500, 800, "l");
        var salad = Item(4, "Salat", 1000, 750);
        var water = Item(5, "Wasser", 2000, 290);
        var table = new[] { pizza, cola, wine, salad, water };

        var total =
            RestaurantSplitCalculator.ByItems(
                table,
                table.Select(x =>
                    new RestaurantSplitSelection(
                        x.Id,
                        x.QuantityMilli)).ToArray())
            .TotalCents;

        var plan =
            RestaurantSplitPlanner.DistributeWholeItems(
                table,
                3);

        var shares = plan
            .Select(x =>
                x.Length == 0
                    ? 0L
                    : RestaurantSplitCalculator.ByItems(
                        table,
                        x).TotalCents)
            .ToArray();

        var assigned =
            table.ToDictionary(x => x.Id, _ => 0L);
        foreach (var selection in plan.SelectMany(x => x))
            assigned[selection.SessionItemId] +=
                selection.QuantityMilli;

        assert(
            plan.Count == 3 &&
            shares.Sum() == total &&
            table.All(x =>
                assigned[x.Id] == x.QuantityMilli) &&
            plan.SelectMany(x => x).All(x =>
                x.SessionItemId == wine.Id ||
                x.QuantityMilli % 1000 == 0),
            "R-4 person split assigns every amount exactly once and only splits Stück lines into whole pieces");

        var again =
            RestaurantSplitPlanner.DistributeWholeItems(
                table,
                3);
        assert(
            plan.Select(x =>
                    string.Join(
                        ",",
                        x.Select(y =>
                            $"{y.SessionItemId}:{y.QuantityMilli}")))
                .SequenceEqual(
                    again.Select(x =>
                        string.Join(
                            ",",
                            x.Select(y =>
                                $"{y.SessionItemId}:{y.QuantityMilli}")))),
            "R-4 person split is deterministic for the same open table");

        var outOfRange = false;
        try
        {
            RestaurantSplitPlanner.DistributeWholeItems(
                table,
                1);
        }
        catch (ArgumentOutOfRangeException)
        {
            outOfRange = true;
        }

        assert(
            outOfRange &&
            RestaurantSplitPlanner
                .DistributeWholeItems(
                    new[] { pizza },
                    4)
                .Count(x => x.Length > 0) == 1,
            "R-4 person split accepts 2-20 persons and leaves empty shares when there are fewer pieces");

        return Task.CompletedTask;
    }
}
