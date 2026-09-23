using TorPos.Application;
using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.App;

public sealed class RestaurantHandheldService : IRestaurantHandheldService
{
    private readonly RestaurantEntitlementService _entitlements;
    private readonly RestaurantRepository _restaurant;
    private readonly RestaurantFiscalOrderService _fiscal;
    private readonly RestaurantKitchenOutbox _kitchen;
    private readonly RestaurantKitchenDispatcher _kitchenDispatcher;
    private readonly RestaurantHandheldPairingService _pairing;
    private readonly IProductCatalog _catalog;

    public RestaurantHandheldService(
        RestaurantEntitlementService entitlements,
        RestaurantRepository restaurant,
        RestaurantFiscalOrderService fiscal,
        RestaurantKitchenOutbox kitchen,
        RestaurantKitchenDispatcher kitchenDispatcher,
        RestaurantHandheldPairingService pairing,
        IProductCatalog catalog)
    {
        _entitlements = entitlements;
        _restaurant = restaurant;
        _fiscal = fiscal;
        _kitchen = kitchen;
        _kitchenDispatcher = kitchenDispatcher;
        _pairing = pairing;
        _catalog = catalog;
    }

    public async Task<IReadOnlyList<RestaurantHandheldTableSummary>> GetTablesAsync(
        string deviceId,
        string deviceToken,
        CancellationToken ct = default)
    {
        _entitlements.Require(
            RestaurantFeature.HandheldBestellung);

        await _pairing.RequireAuthenticatedAsync(
            deviceId,
            deviceToken,
            ct);

        var result = new List<RestaurantHandheldTableSummary>();
        foreach (var table in await _restaurant.ListTablesAsync(ct))
        {
            var session = await _restaurant.GetLiveSessionForTableAsync(
                table.Id,
                ct);

            if (session is null)
            {
                result.Add(new RestaurantHandheldTableSummary(
                    table.Id,
                    table.DisplayName,
                    false,
                    "",
                    0,
                    "",
                    0,
                    0));
                continue;
            }

            var items = await _restaurant.ListActiveItemsAsync(
                session.Id,
                ct);

            result.Add(new RestaurantHandheldTableSummary(
                table.Id,
                table.DisplayName,
                true,
                session.Id,
                session.Version,
                session.AssignedWaiter,
                session.GuestCount,
                items.Sum(x => x.LineTotalCents)));
        }

        return result;
    }

    public async Task<IReadOnlyList<RestaurantHandheldCatalogProduct>> GetCatalogAsync(
        string deviceId,
        string deviceToken,
        CancellationToken ct = default)
    {
        _entitlements.Require(
            RestaurantFeature.HandheldBestellung);

        await _pairing.RequireAuthenticatedAsync(
            deviceId,
            deviceToken,
            ct);

        var categories = _catalog.Categories
            .ToDictionary(x => x.Id);

        return _catalog.Products
            .Where(x =>
                x.IsActive &&
                !x.IsWeighted &&
                !x.IsCombo &&
                x.Variants.Count == 0)
            .OrderBy(x =>
                categories.TryGetValue(x.CategoryId, out var category)
                    ? category.Name
                    : "")
            .ThenBy(x => x.Name)
            .Select(x =>
            {
                categories.TryGetValue(
                    x.CategoryId,
                    out var category);

                return new RestaurantHandheldCatalogProduct(
                    x.Id,
                    x.Name,
                    category?.Name ?? "",
                    x.BasePriceCents + x.PfandCents,
                    x.VatRate,
                    KitchenStations.Normalize(
                        category?.KitchenStation));
            })
            .ToArray();
    }

    public async Task<RestaurantHandheldCommandResult> OpenTableAsync(
        RestaurantHandheldOpenTableRequest request,
        CancellationToken ct = default)
    {
        _entitlements.Require(
            RestaurantFeature.HandheldBestellung);

        await _pairing.RequireAuthenticatedAsync(
            request.DeviceId,
            request.DeviceToken,
            ct);

        var table = (await _restaurant.ListTablesAsync(ct))
            .FirstOrDefault(x => x.Id == request.TableId)
            ?? throw new InvalidOperationException(
                "Tisch ist nicht vorhanden oder deaktiviert.");

        if (await _restaurant.GetLiveSessionForTableAsync(
                table.Id,
                ct) is not null)
        {
            throw new InvalidOperationException(
                "Tisch ist bereits belegt.");
        }

        var session = await _restaurant.OpenTableAsync(
            table.Id,
            request.OperatorName,
            request.GuestCount,
            request.Note,
            request.DeviceId,
            ct);

        return new RestaurantHandheldCommandResult(
            session.Id,
            session.Version);
    }

    public async Task<RestaurantHandheldCommandResult> AddItemAsync(
        RestaurantHandheldAddItemRequest request,
        CancellationToken ct = default)
    {
        _entitlements.Require(
            RestaurantFeature.HandheldBestellung);

        await _pairing.RequireAuthenticatedAsync(
            request.DeviceId,
            request.DeviceToken,
            ct);

        if (request.Quantity <= 0m)
            throw new ArgumentOutOfRangeException(
                nameof(request.Quantity));

        var product = _catalog.Products
            .FirstOrDefault(x =>
                x.Id == request.ProductId &&
                x.IsActive)
            ?? throw new InvalidOperationException(
                "Artikel ist nicht vorhanden oder deaktiviert.");

        var secured = await _fiscal.IsCurrentStateSecuredAsync(
            request.SessionId,
            ct);

        if (!secured)
            throw new InvalidOperationException(
                "Bestellung/TSE-Stand stimmt nicht mit dem Tisch überein.");

        RestaurantFiscalVorgang? vorgang = null;

        try
        {
            vorgang = await _fiscal.BeginChangeAsync(
                request.SessionId,
                request.OperatorName,
                ct);

            var item = await _restaurant.AddItemAsync(
                request.SessionId,
                request.ExpectedSessionVersion,
                product,
                request.Quantity,
                request.OperatorName,
                request.DeviceId,
                ct);

            await _fiscal.SecureAddedItemAsync(
                request.SessionId,
                item,
                vorgang,
                request.OperatorName,
                ct);

            vorgang = null;

            var session = await _restaurant.GetSessionAsync(
                request.SessionId,
                ct)
                ?? throw new InvalidOperationException(
                    "Tischvorgang nicht gefunden.");

            var table = (await _restaurant.ListTablesAsync(ct))
                .FirstOrDefault(x => x.Id == session.TableId);

            var category = _catalog.Categories
                .FirstOrDefault(x => x.Id == product.CategoryId);

            await _kitchen.EnqueueNewItemAsync(
                session,
                item,
                table?.DisplayName ?? "Tisch",
                request.OperatorName,
                KitchenStations.Normalize(
                    category?.KitchenStation),
                ct);

            _kitchenDispatcher.Notify();

            return new RestaurantHandheldCommandResult(
                session.Id,
                session.Version);
        }
        catch
        {
            if (vorgang is not null)
            {
                try
                {
                    await _fiscal.AbortChangeAsync(
                        vorgang,
                        request.OperatorName,
                        ct);
                }
                catch
                {
                }
            }

            throw;
        }
    }
}
