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

public sealed record RestaurantHandheldAddItemRequest(
    string SessionId,
    long ExpectedSessionVersion,
    long ProductId,
    decimal Quantity,
    string OperatorName,
    string DeviceId,
    string DeviceToken);

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

    Task<RestaurantHandheldCommandResult> AddItemAsync(
        RestaurantHandheldAddItemRequest request,
        CancellationToken ct = default);
}
