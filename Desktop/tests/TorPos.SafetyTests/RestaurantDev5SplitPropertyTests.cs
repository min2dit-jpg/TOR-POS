using TorPos.Core;

// Split by persons and by items as properties over many seeded random tables
// (Stück and litre lines, odd prices, lines already partly paid): the persons'
// shares add up to the open table total to the cent, every open quantity is
// assigned exactly once, Stück lines only in whole pieces, no share is
// negative, and the same position can not be selected twice.
public static class RestaurantDev5SplitPropertyTests
{
    private static RestaurantSessionItem Line(long id, long quantityMilli, long unitPriceCents, string unit, long paidMilli)
    {
        RestaurantSessionItem Make(long qty, long persisted, long paid) =>
            new(id, "split-property", "token-" + id, id, "Artikel " + id, "", qty, unitPriceCents, 19m, 0,
                RestaurantSessionItemState.Active, "KELLNER", DateTimeOffset.UtcNow, 1)
            { Unit = unit, PersistedLineTotalCents = persisted, PaidCents = paid };

        var original = Make(quantityMilli, (long)Math.Round(quantityMilli / 1000m * unitPriceCents, MidpointRounding.AwayFromZero), 0);
        if (paidMilli == 0)
            return original;
        // A slice already paid: the quote the till charged for it at the time.
        var paidCents = RestaurantSplitCalculator.ByItems(new[] { original }, new[] { new RestaurantSplitSelection(id, paidMilli) }).TotalCents;
        return Make(quantityMilli - paidMilli, original.PersistedLineTotalCents!.Value, paidCents);
    }

    public static Task Run(Action<bool, string> assert)
    {
        var random = new Random(2026_09_27);
        var tables = 0;
        var failures = new List<string>();
        var oddCentTables = 0;
        var partlyPaidTables = 0;

        for (var t = 0; t < 400; t++)
        {
            var items = new List<RestaurantSessionItem>();
            var count = random.Next(1, 7);
            for (var i = 1; i <= count; i++)
            {
                var litre = random.Next(4) == 0;
                var quantity = litre ? random.Next(1, 11) * 250 : random.Next(1, 5) * 1000;
                var price = random.Next(1, 3000);
                var paid = random.Next(4) == 0 && quantity > (litre ? 250 : 1000)
                    ? (litre ? random.Next(1, quantity / 250) * 250 : random.Next(1, quantity / 1000) * 1000)
                    : 0;
                items.Add(Line(t * 10 + i, quantity, price, litre ? "l" : "Stück", paid));
            }

            var open = RestaurantSplitCalculator.ByItems(items, items.Select(x => new RestaurantSplitSelection(x.Id, x.QuantityMilli)).ToArray()).TotalCents;
            var expectedOpen = items.Sum(x => x.PersistedLineTotalCents!.Value - x.PaidCents);
            var persons = random.Next(2, 7);
            var plan = RestaurantSplitPlanner.DistributeWholeItems(items, persons);
            var shares = plan.Select(s => s.Length == 0 ? 0L : RestaurantSplitCalculator.ByItems(items, s).TotalCents).ToArray();
            var assigned = items.ToDictionary(x => x.Id, _ => 0L);
            foreach (var s in plan.SelectMany(x => x)) assigned[s.SessionItemId] += s.QuantityMilli;
            var wholePieces = plan.SelectMany(x => x).All(s => items.Single(i => i.Id == s.SessionItemId).Unit != "Stück" || s.QuantityMilli % 1000 == 0);
            var nonStueckKeptWhole = plan.SelectMany(x => x).All(s =>
                items.Single(i => i.Id == s.SessionItemId) is var item && (item.Unit == "Stück" || s.QuantityMilli == item.QuantityMilli));

            if (open != expectedOpen || shares.Sum() != open || shares.Any(x => x < 0) ||
                items.Any(x => assigned[x.Id] != x.QuantityMilli) || !wholePieces || !nonStueckKeptWhole || plan.Count != persons)
                failures.Add($"table {t}: open {open}/{expectedOpen}, shares {string.Join("+", shares)}");

            if (open % persons != 0) oddCentTables++;
            if (items.Any(x => x.PaidCents > 0)) partlyPaidTables++;
            tables++;
        }

        assert(
            failures.Count == 0 && tables == 400 && oddCentTables > 50 && partlyPaidTables > 50,
            $"DEV5 split: over 400 seeded tables ({oddCentTables} not divisible by the persons, {partlyPaidTables} partly paid) the person shares add up to the open total to the cent, every open quantity goes to exactly one person, Stück only in whole pieces, never negative{(failures.Count == 0 ? "" : " - " + string.Join("; ", failures.Take(3)))}");

        // The same position selected twice, or more than is open, is refused
        // instead of being charged twice.
        var cola = Line(1, 3000, 350, "Stück", 1000); // 2 open of 3, one already paid
        var refusedTwice = Throws(() => RestaurantSplitCalculator.ByItems(new[] { cola }, new[] { new RestaurantSplitSelection(1, 1000), new RestaurantSplitSelection(1, 1000) }));
        var refusedOver = Throws(() => RestaurantSplitCalculator.ByItems(new[] { cola }, new[] { new RestaurantSplitSelection(1, 3000) }));
        var refusedHalf = Throws(() => RestaurantSplitCalculator.ByItems(new[] { cola }, new[] { new RestaurantSplitSelection(1, 500) }));
        var rest = RestaurantSplitCalculator.ByItems(new[] { cola }, new[] { new RestaurantSplitSelection(1, 2000) }).TotalCents;
        assert(
            refusedTwice && refusedOver && refusedHalf && rest == 700,
            "DEV5 split: after one of three Stück is paid, selecting the line twice, more than the two open pieces or half a piece is refused, and the rest costs exactly the open 7,00 EUR");

        return Task.CompletedTask;
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (InvalidOperationException) { return true; }
        catch (ArgumentException) { return true; }
    }
}
