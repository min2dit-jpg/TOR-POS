namespace TorPos.Core;

/// <summary>
/// R-4(B): splits a table across persons by assigning real whole positions or
/// whole Stück units. Weighted/non-piece positions stay intact and go to one
/// person. The existing item-split/payment path remains the only checkout path.
/// </summary>
public static class RestaurantSplitPlanner
{
    public const int MaxPersons = 20;

    public static IReadOnlyList<RestaurantSplitSelection[]> DistributeWholeItems(
        IReadOnlyList<RestaurantSessionItem> openItems,
        int persons)
    {
        if (persons < 2 || persons > MaxPersons)
            throw new ArgumentOutOfRangeException(
                nameof(persons),
                $"Personenzahl muss zwischen 2 und {MaxPersons} liegen.");

        var pieces = new List<(long ItemId, long QuantityMilli, long AmountCents)>();

        foreach (var item in openItems
                     .Where(x =>
                         x.State == RestaurantSessionItemState.Active &&
                         x.QuantityMilli > 0)
                     .OrderBy(x => x.Id))
        {
            var wholePieces =
                string.Equals(
                    item.Unit,
                    "Stück",
                    StringComparison.OrdinalIgnoreCase) &&
                item.QuantityMilli % 1000 == 0;

            if (wholePieces)
            {
                var one =
                    RestaurantSplitCalculator.ByItems(
                        new[] { item },
                        new[]
                        {
                            new RestaurantSplitSelection(
                                item.Id,
                                1000)
                        }).TotalCents;

                for (var i = 0; i < item.QuantityMilli / 1000; i++)
                    pieces.Add((item.Id, 1000, one));
            }
            else
            {
                pieces.Add(
                    (item.Id, item.QuantityMilli, item.LineTotalCents));
            }
        }

        var totals = new long[persons];
        var shares = Enumerable.Range(0, persons)
            .Select(_ => new Dictionary<long, long>())
            .ToArray();

        foreach (var piece in pieces
                     .OrderByDescending(x => x.AmountCents)
                     .ThenBy(x => x.ItemId))
        {
            var target = 0;
            for (var p = 1; p < persons; p++)
            {
                if (totals[p] < totals[target])
                    target = p;
            }

            totals[target] += piece.AmountCents;
            shares[target][piece.ItemId] =
                shares[target].GetValueOrDefault(piece.ItemId) +
                piece.QuantityMilli;
        }

        return shares
            .Select(share => share
                .OrderBy(x => x.Key)
                .Select(x =>
                    new RestaurantSplitSelection(
                        x.Key,
                        x.Value))
                .ToArray())
            .ToArray();
    }
}
