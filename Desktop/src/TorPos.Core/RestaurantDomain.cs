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
    Paid,
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

    // R-9.2: immutable original gross cents plus the cents already removed
    // from the still-active logical line. Paid slices are normalised back to
    // PaidCents=0. Legacy/in-memory rows fall back to quantity × unit price.
    public long? PersistedLineTotalCents { get; init; }
    public long PaidCents { get; init; }

    // R-3: immutable commercial/fiscal snapshot. Never re-read variant/menu
    // details from today's Artikelstamm when paying, reversing or reconciling.
    public string Unit { get; init; } = "Stück";
    public long ListUnitPriceCents { get; init; }
    public bool ImHausApplicable { get; init; } = true;
    public MenuVatAllocation[] VatAllocations { get; init; } =
        Array.Empty<MenuVatAllocation>();
    public MenuComponentSnapshot[] MenuComponents { get; init; } =
        Array.Empty<MenuComponentSnapshot>();

    public long EffectiveListUnitPriceCents =>
        ListUnitPriceCents > 0 ? ListUnitPriceCents : UnitPriceCents;

    public long LineTotalCents =>
        PersistedLineTotalCents is long persisted
            ? Math.Max(0L, persisted - PaidCents)
            : (long)Math.Round(
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

        var duplicateSelection =
            selections
                .GroupBy(x => x.SessionItemId)
                .FirstOrDefault(x => x.Count() > 1);

        if (duplicateSelection is not null)
        {
            throw new InvalidOperationException(
                "Position mehrfach ausgewählt. Jede Tischposition darf nur einmal in einer Teilrechnung vorkommen.");
        }

        var lines = new List<RestaurantSplitLine>();

        foreach (var selection in selections)
        {
            if (!source.TryGetValue(selection.SessionItemId, out var item))
                throw new InvalidOperationException("Ausgewählte Position gehört nicht zum offenen Tischvorgang.");

            if (selection.QuantityMilli <= 0 || selection.QuantityMilli > item.QuantityMilli)
                throw new InvalidOperationException("Ungültige Teilmenge für Splitrechnung.");

            if (string.Equals(
                    item.Unit,
                    "Stück",
                    StringComparison.OrdinalIgnoreCase) &&
                selection.QuantityMilli % 1000 != 0)
            {
                throw new InvalidOperationException(
                    "Diese Position kann nur in ganzen Stückzahlen geteilt werden.");
            }

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


public static class RestaurantServiceModes
{
    public const string InHouse = "IN_HOUSE";
    public const string Takeaway = "TAKEAWAY";
    public const string Pickup = "PICKUP";

    public static string Normalize(string? value) =>
        (value ?? "").Trim().ToUpperInvariant() switch
        {
            Takeaway => Takeaway,
            Pickup => Pickup,
            _ => InHouse
        };

    public static bool IsImHaus(string? value) =>
        string.Equals(
            Normalize(value),
            InHouse,
            StringComparison.Ordinal);
}

public sealed record RestaurantCheckoutDraft(
    string SessionId,
    long SessionVersion,
    string OperationId,
    CartLine[] Lines,
    RestaurantSplitSelection[] Selections,
    string ServiceMode = RestaurantServiceModes.InHouse)
{
    public long TotalCents => Lines.Sum(x => x.LineTotalCents);
    public bool ImHaus => RestaurantServiceModes.IsImHaus(ServiceMode);
}
