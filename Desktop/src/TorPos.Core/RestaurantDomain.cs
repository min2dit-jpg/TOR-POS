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
