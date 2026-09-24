using TorPos.Core;

namespace TorPos.Application;

public sealed record RestaurantHandheldTableSummary(
    long TableId,
    string TableName,
    bool IsOpen,
    string SessionId,
    long SessionVersion,
    string Waiter,
    int GuestCount,
    long OpenTotalCents);

public sealed record RestaurantHandheldCatalogProduct(
    long ProductId,
    string ProductName,
    string CategoryName,
    long PriceCents,
    decimal VatRate,
    string KitchenStation);

public sealed record RestaurantHandheldOpenTableRequest(
    long TableId,
    int GuestCount,
    string Note,
    string OperatorName,
    string OperatorPin,
    string DeviceId,
    string DeviceToken,
    string OperatorSessionToken = "");

public sealed record RestaurantHandheldUpdateTableRequest(
    string SessionId,
    long ExpectedSessionVersion,
    int GuestCount,
    string Note,
    string OperatorName,
    string OperatorPin,
    string DeviceId,
    string DeviceToken,
    string OperatorSessionToken = "");

public sealed record RestaurantHandheldItemSummary(
    long SessionItemId,
    long ProductId,
    string ProductName,
    string VariantName,
    decimal Quantity,
    long UnitPriceCents,
    long LineTotalCents);

public sealed record RestaurantHandheldAddItemRequest(
    string SessionId,
    long ExpectedSessionVersion,
    long ProductId,
    decimal Quantity,
    string OperatorName,
    string OperatorPin,
    string DeviceId,
    string DeviceToken,
    string CommandId = "",
    string OperatorSessionToken = "");

public sealed record RestaurantHandheldCancelItemRequest(
    string SessionId,
    long ExpectedSessionVersion,
    long SessionItemId,
    string OperatorName,
    string OperatorPin,
    string DeviceId,
    string DeviceToken,
    string CommandId = "",
    string OperatorSessionToken = "",
    string Reason = "");

public sealed record RestaurantHandheldCommandResult(
    string SessionId,
    long SessionVersion);

/// <summary>
/// Application boundary for Restaurant Plus handheld clients.
/// Network transports must call this service and must never expose SQLite
/// or repositories directly to handheld devices.
/// </summary>
public interface IRestaurantHandheldService
{
    Task<IReadOnlyList<RestaurantHandheldTableSummary>> GetTablesAsync(
        string deviceId,
        string deviceToken,
        CancellationToken ct = default);

    Task<IReadOnlyList<RestaurantHandheldCatalogProduct>> GetCatalogAsync(
        string deviceId,
        string deviceToken,
        CancellationToken ct = default);

    Task<RestaurantHandheldCommandResult> OpenTableAsync(
        RestaurantHandheldOpenTableRequest request,
        CancellationToken ct = default);

    Task<IReadOnlyList<RestaurantHandheldItemSummary>> GetItemsAsync(
        string sessionId,
        string deviceId,
        string deviceToken,
        CancellationToken ct = default);

    Task<RestaurantHandheldCommandResult> UpdateTableAsync(
        RestaurantHandheldUpdateTableRequest request,
        CancellationToken ct = default);

    Task<RestaurantHandheldCommandResult> AddItemAsync(
        RestaurantHandheldAddItemRequest request,
        CancellationToken ct = default);

    Task<RestaurantHandheldCommandResult> CancelItemAsync(
        RestaurantHandheldCancelItemRequest request,
        CancellationToken ct = default);
}
