namespace TorPos.Core;

public enum RestaurantTableSessionState
{
    Open,
    CheckRequested,
    Closed,
    Cancelled
}

public enum RestaurantSessionItemState
{
    Active,
    Cancelled
}

public sealed record RestaurantArea(
    long Id,
    string Name,
    int SortOrder,
    bool IsActive);

public sealed record RestaurantTable(
    long Id,
    long AreaId,
    string Code,
    string DisplayName,
    int Seats,
    int SortOrder,
    bool IsActive,
    long Version);

public sealed record RestaurantTableSession(
    string Id,
    long TableId,
    DateTimeOffset OpenedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ClosedAt,
    RestaurantTableSessionState State,
    string OpenedBy,
    string AssignedWaiter,
    int GuestCount,
    string Note,
    long Version);

public sealed record RestaurantSessionItem(
    long Id,
    string SessionId,
    string LineToken,
    long ProductId,
    string ProductName,
    string VariantName,
    long QuantityMilli,
    long UnitPriceCents,
    decimal VatRate,
    long PfandCents,
    RestaurantSessionItemState State,
    string AddedBy,
    DateTimeOffset AddedAt,
    long Version)
{
    public decimal Quantity => QuantityMilli / 1000m;

    public long LineTotalCents =>
        (long)Math.Round(
            Quantity * UnitPriceCents,
            MidpointRounding.AwayFromZero);
}


public sealed record RestaurantSplitSelection(
    long SessionItemId,
    long QuantityMilli);

public sealed record RestaurantSplitLine(
    long SessionItemId,
    string ProductName,
    long QuantityMilli,
    long AmountCents)
{
    public decimal Quantity => QuantityMilli / 1000m;
}

public sealed record RestaurantSplitQuote(
    IReadOnlyList<RestaurantSplitLine> Lines,
    long TotalCents)
{
    public bool IsEmpty => Lines.Count == 0;
}

/// <summary>
/// Pure, fiscal-neutral split calculator. It prepares a payment candidate only;
/// it never creates a sale, receipt number, TSE transaction or stock movement.
/// </summary>
public static class RestaurantSplitCalculator
{
    public static RestaurantSplitQuote ByItems(
        IReadOnlyList<RestaurantSessionItem> openItems,
        IReadOnlyList<RestaurantSplitSelection> selections)
    {
        var source = openItems.ToDictionary(x => x.Id);
        var lines = new List<RestaurantSplitLine>();

        foreach (var selection in selections)
        {
            if (!source.TryGetValue(selection.SessionItemId, out var item))
                throw new InvalidOperationException("Ausgewählte Position gehört nicht zum offenen Tischvorgang.");

            if (selection.QuantityMilli <= 0 || selection.QuantityMilli > item.QuantityMilli)
                throw new InvalidOperationException("Ungültige Teilmenge für Splitrechnung.");

            var amount = AllocateCents(
                item.LineTotalCents,
                item.QuantityMilli,
                selection.QuantityMilli);

            lines.Add(new RestaurantSplitLine(
                item.Id,
                item.ProductName,
                selection.QuantityMilli,
                amount));
        }

        return new RestaurantSplitQuote(
            lines,
            lines.Sum(x => x.AmountCents));
    }

    public static IReadOnlyList<long> EqualShares(
        long totalCents,
        int persons)
    {
        if (totalCents < 0)
            throw new ArgumentOutOfRangeException(nameof(totalCents));
        if (persons < 2 || persons > 99)
            throw new ArgumentOutOfRangeException(nameof(persons));

        var baseShare = totalCents / persons;
        var remainder = totalCents % persons;
        var result = new long[persons];

        for (var i = 0; i < persons; i++)
            result[i] = baseShare + (i < remainder ? 1 : 0);

        return result;
    }

    private static long AllocateCents(
        long fullAmountCents,
        long fullQuantityMilli,
        long selectedQuantityMilli)
    {
        if (selectedQuantityMilli == fullQuantityMilli)
            return fullAmountCents;

        return (long)Math.Round(
            fullAmountCents * (selectedQuantityMilli / (decimal)fullQuantityMilli),
            MidpointRounding.AwayFromZero);
    }
}
