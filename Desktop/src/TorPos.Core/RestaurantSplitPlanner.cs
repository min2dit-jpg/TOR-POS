namespace TorPos.Core;

/// <summary>
/// R-4 (variant B): "split by persons" distributes WHOLE positions - or whole
/// pieces of a position counted in pieces - to the persons, as evenly as the
/// prices allow. Nothing is divided mathematically: every person pays real
/// positions through the existing item split (RestaurantSplitSelection), so the
/// payment and fiscal path is unchanged. A position that is not counted in whole
/// pieces (e.g. weighed) goes to one person in full. Dividing a single position
/// into thirds stays closed until the remainder carries its own cents (R-9.2).
///
/// Greedy "largest first, to the lowest total": pieces are sorted by amount
/// (then position id), each goes to the person with the lowest running total
/// (lowest index on ties). Deterministic, so the same table yields the same plan.
/// </summary>
public static class RestaurantSplitPlanner
{
    public const int MaxPersons = 20;

    public static IReadOnlyList<RestaurantSplitSelection[]> DistributeWholeItems(
        IReadOnlyList<RestaurantSessionItem> openItems,
        int persons)
    {
        if (persons < 2 || persons > MaxPersons)
            throw new ArgumentOutOfRangeException(nameof(persons), $"Personenzahl muss zwischen 2 und {MaxPersons} liegen.");

        var pieces = new List<(long ItemId, long QuantityMilli, long AmountCents)>();
        foreach (var item in openItems
                     .Where(x => x.State == RestaurantSessionItemState.Active && x.QuantityMilli > 0)
                     .OrderBy(x => x.Id))
        {
            if (item.QuantityMilli % 1000 == 0)
            {
                var one = RestaurantSplitCalculator.ByItems(
                    new[] { item },
                    new[] { new RestaurantSplitSelection(item.Id, 1000) }).TotalCents;
                for (var i = 0; i < item.QuantityMilli / 1000; i++)
                    pieces.Add((item.Id, 1000, one));
            }
            else
            {
                pieces.Add((item.Id, item.QuantityMilli, item.LineTotalCents));
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
                if (totals[p] < totals[target])
                    target = p;

            totals[target] += piece.AmountCents;
            shares[target][piece.ItemId] =
                shares[target].GetValueOrDefault(piece.ItemId) + piece.QuantityMilli;
        }

        return shares
            .Select(share => share
                .OrderBy(x => x.Key)
                .Select(x => new RestaurantSplitSelection(x.Key, x.Value))
                .ToArray())
            .ToArray();
    }
}
