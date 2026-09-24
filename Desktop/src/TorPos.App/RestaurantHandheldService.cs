using System.Security.Cryptography;
using System.Text;
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
    private readonly IAuthenticationService _authentication;
    private readonly RestaurantOperatorSessionService _operatorSessions;
    private readonly RestaurantCommandJournal _commands;
    private readonly IProductCatalog _catalog;
    private readonly ControlledPosActionService _controlledActions;

    public RestaurantHandheldService(
        RestaurantEntitlementService entitlements,
        RestaurantRepository restaurant,
        RestaurantFiscalOrderService fiscal,
        RestaurantKitchenOutbox kitchen,
        RestaurantKitchenDispatcher kitchenDispatcher,
        RestaurantHandheldPairingService pairing,
        IAuthenticationService authentication,
        RestaurantOperatorSessionService operatorSessions,
        RestaurantCommandJournal commands,
        IProductCatalog catalog,
        ControlledPosActionService controlledActions)
    {
        _entitlements = entitlements;
        _restaurant = restaurant;
        _fiscal = fiscal;
        _kitchen = kitchen;
        _kitchenDispatcher = kitchenDispatcher;
        _pairing = pairing;
        _authentication = authentication;
        _operatorSessions = operatorSessions;
        _commands = commands;
        _catalog = catalog;
        _controlledActions = controlledActions;
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

        var rows =
            await _restaurant.ListLiveTableSummariesAsync(
                ct);

        return rows
            .Select(x =>
                new RestaurantHandheldTableSummary(
                    x.TableId,
                    x.TableName,
                    x.IsOpen,
                    x.SessionId,
                    x.SessionVersion,
                    x.Waiter,
                    x.GuestCount,
                    x.OpenTotalCents))
            .ToArray();
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

        var operatorUser = await RequireOperatorAsync(
            request.OperatorName,
            request.OperatorPin,
            request.DeviceId,
            request.OperatorSessionToken,
            ct);

        var session = await _restaurant.OpenTableAsync(
            request.TableId,
            operatorUser.Username,
            request.GuestCount,
            request.Note,
            request.DeviceId,
            ct);

        return new RestaurantHandheldCommandResult(
            session.Id,
            session.Version);
    }

    public async Task<IReadOnlyList<RestaurantHandheldItemSummary>> GetItemsAsync(
        string sessionId,
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

        sessionId = (sessionId ?? "").Trim();
        if (sessionId.Length == 0)
            throw new ArgumentException(
                "Tischvorgang fehlt.",
                nameof(sessionId));

        _ = await _restaurant.GetSessionAsync(
                sessionId,
                ct)
            ?? throw new InvalidOperationException(
                "Tischvorgang nicht gefunden.");

        return (await _restaurant.ListActiveItemsAsync(
                sessionId,
                ct))
            .Select(x => new RestaurantHandheldItemSummary(
                x.Id,
                x.ProductId,
                x.ProductName,
                x.VariantName,
                x.QuantityMilli / 1000m,
                x.UnitPriceCents,
                x.LineTotalCents))
            .ToArray();
    }

    public async Task<RestaurantHandheldCommandResult> UpdateTableAsync(
        RestaurantHandheldUpdateTableRequest request,
        CancellationToken ct = default)
    {
        _entitlements.Require(
            RestaurantFeature.HandheldBestellung);

        await _pairing.RequireAuthenticatedAsync(
            request.DeviceId,
            request.DeviceToken,
            ct);

        var operatorUser = await RequireOperatorAsync(
            request.OperatorName,
            request.OperatorPin,
            request.DeviceId,
            request.OperatorSessionToken,
            ct);

        var before = await _restaurant.GetSessionAsync(
                request.SessionId,
                ct)
            ?? throw new InvalidOperationException(
                "Tischvorgang nicht gefunden.");

        var updated = await _restaurant.UpdateSessionDetailsAsync(
            request.SessionId,
            request.ExpectedSessionVersion,
            request.GuestCount,
            request.Note,
            operatorUser.Username,
            request.DeviceId,
            ct);

        if (!string.Equals(
                before.Note,
                updated.Note,
                StringComparison.Ordinal) &&
            (await _restaurant.ListActiveItemsAsync(
                updated.Id,
                ct)).Count > 0)
        {
            var table = (await _restaurant.ListTablesAsync(ct))
                .FirstOrDefault(x => x.Id == updated.TableId);

            await _kitchen.EnqueueNoteAsync(
                updated,
                table?.DisplayName ?? "Tisch",
                operatorUser.Username,
                ct);

            _kitchenDispatcher.Notify();
        }

        return new RestaurantHandheldCommandResult(
            updated.Id,
            updated.Version);
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

        var operatorUser = await RequireOperatorAsync(
            request.OperatorName,
            request.OperatorPin,
            request.DeviceId,
            request.OperatorSessionToken,
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

        var quantityMilli =
            (long)Math.Round(
                request.Quantity * 1000m,
                MidpointRounding.AwayFromZero);

        if (quantityMilli <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(request.Quantity));

        var requestHash = CommandHash(
            "ADD_ITEM",
            request.SessionId,
            request.ExpectedSessionVersion.ToString(),
            request.ProductId.ToString(),
            quantityMilli.ToString(),
            operatorUser.Username);

        var claim = await _commands.BeginAsync(
            request.DeviceId,
            request.CommandId,
            "ADD_ITEM",
            requestHash,
            request.SessionId,
            ct);

        if (claim.State == RestaurantCommandClaimState.Completed)
        {
            return new RestaurantHandheldCommandResult(
                request.SessionId,
                claim.ResultSessionVersion);
        }

        if (claim.State == RestaurantCommandClaimState.Failed)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(claim.ErrorText)
                    ? "Restaurant-Befehl ist bereits fehlgeschlagen. Bitte Ansicht aktualisieren und erneut senden."
                    : claim.ErrorText);
        }

        if (claim.State == RestaurantCommandClaimState.InProgress)
        {
            throw new InvalidOperationException(
                "Restaurant-Befehl wird bereits verarbeitet. Bitte kurz synchronisieren.");
        }

        var lineToken = CommandToken(
            "ITEM",
            request.DeviceId,
            request.CommandId);

        var kitchenJobId = CommandGuid(
            "KITCHEN",
            request.DeviceId,
            request.CommandId);

        RestaurantFiscalVorgang? vorgang = null;
        RestaurantSessionItem? item = null;
        var createdInThisAttempt = false;

        try
        {
            item = await _restaurant.GetItemByLineTokenAsync(
                lineToken,
                ct);

            if (item is not null)
            {
                if (!string.Equals(
                        item.SessionId,
                        request.SessionId,
                        StringComparison.Ordinal) ||
                    item.ProductId != request.ProductId ||
                    item.QuantityMilli != quantityMilli)
                {
                    throw new InvalidOperationException(
                        "Command-ID gehört bereits zu einer anderen Restaurant-Position.");
                }

                if (item.State != RestaurantSessionItemState.Active)
                {
                    throw new InvalidOperationException(
                        "Die über diesen Command erzeugte Restaurant-Position ist nicht mehr offen.");
                }

                if (!await _fiscal.IsCurrentStateSecuredAsync(
                        request.SessionId,
                        ct))
                {
                    vorgang = await _fiscal.BeginChangeAsync(
                        request.SessionId,
                        operatorUser.Username,
                        ct);

                    await _fiscal.SecureAddedItemAsync(
                        request.SessionId,
                        item,
                        vorgang,
                        operatorUser.Username,
                        ct);

                    vorgang = null;
                }
            }
            else
            {
                var sessionBefore =
                    await _restaurant.GetSessionAsync(
                        request.SessionId,
                        ct)
                    ?? throw new InvalidOperationException(
                        "Tischvorgang nicht gefunden.");

                if (sessionBefore.State !=
                        RestaurantTableSessionState.Open ||
                    sessionBefore.Version !=
                        request.ExpectedSessionVersion)
                {
                    throw new InvalidOperationException(
                        "Tischvorgang wurde zwischenzeitlich geändert. Ansicht aktualisieren.");
                }

                var secured =
                    await _fiscal.IsCurrentStateSecuredAsync(
                        request.SessionId,
                        ct);

                if (!secured)
                {
                    throw new InvalidOperationException(
                        "Bestellung/TSE-Stand stimmt nicht mit dem Tisch überein.");
                }

                vorgang = await _fiscal.BeginChangeAsync(
                    request.SessionId,
                    operatorUser.Username,
                    ct);

                var mutation =
                    await _restaurant.AddItemWithLineTokenAsync(
                        request.SessionId,
                        request.ExpectedSessionVersion,
                        product,
                        request.Quantity,
                        operatorUser.Username,
                        lineToken,
                        request.DeviceId,
                        ct);

                item = mutation.Item;
                createdInThisAttempt = mutation.Created;

                if (!mutation.Created)
                {
                    await _fiscal.AbortChangeAsync(
                        vorgang,
                        operatorUser.Username,
                        ct);
                    vorgang = null;

                    if (!await _fiscal.IsCurrentStateSecuredAsync(
                            request.SessionId,
                            ct))
                    {
                        throw new InvalidOperationException(
                            "Restaurant-Befehl wird bereits verarbeitet. Bitte kurz synchronisieren.");
                    }
                }
                else
                {
                    await _fiscal.SecureAddedItemAsync(
                        request.SessionId,
                        item,
                        vorgang,
                        operatorUser.Username,
                        ct);

                    vorgang = null;
                }
            }

            var session = await _restaurant.GetSessionAsync(
                request.SessionId,
                ct)
                ?? throw new InvalidOperationException(
                    "Tischvorgang nicht gefunden.");

            if (item is null)
            {
                throw new InvalidOperationException(
                    "Restaurant-Position konnte nicht wiederhergestellt werden.");
            }

            var table = await _restaurant.GetTableAsync(
                session.TableId,
                ct);

            var category = _catalog.Categories
                .FirstOrDefault(x => x.Id == product.CategoryId);

            await _kitchen.EnqueueNewItemIdempotentAsync(
                session,
                item,
                table?.DisplayName ?? "Tisch",
                operatorUser.Username,
                kitchenJobId,
                KitchenStations.Normalize(
                    category?.KitchenStation),
                ct);

            _kitchenDispatcher.Notify();

            await _commands.CompleteAsync(
                request.DeviceId,
                request.CommandId,
                session.Version,
                ct);

            return new RestaurantHandheldCommandResult(
                session.Id,
                session.Version);
        }
        catch (Exception ex)
        {
            var discardedPending = false;
            var fiscalOutcomeUncertain = false;

            if (vorgang is not null)
            {
                try
                {
                    if (createdInThisAttempt && item is not null)
                    {
                        var mayDiscard =
                            await _fiscal.AbortPendingAddedItemAsync(
                                vorgang,
                                item,
                                operatorUser.Username,
                                CancellationToken.None);

                        if (mayDiscard)
                        {
                            discardedPending =
                                await _restaurant.DiscardPendingItemAsync(
                                    item.SessionId,
                                    item.Id,
                                    operatorUser.Username,
                                    request.DeviceId,
                                    CancellationToken.None);
                        }
                        else
                        {
                            // FINISHED/unknown means the TSE may already have
                            // accepted this item while the DB capture failed.
                            // Do not release this command for automatic retry:
                            // that could create a second fiscal signature.
                            fiscalOutcomeUncertain = true;
                        }
                    }
                    else
                    {
                        await _fiscal.AbortChangeAsync(
                            vorgang,
                            operatorUser.Username,
                            CancellationToken.None);
                    }
                }
                catch
                {
                }
            }

            try
            {
                var persistedItem =
                    discardedPending
                        ? null
                        : item ??
                          await _restaurant.GetItemByLineTokenAsync(
                              lineToken,
                              CancellationToken.None);

                if (fiscalOutcomeUncertain)
                {
                    await _commands.FailAsync(
                        request.DeviceId,
                        request.CommandId,
                        "Fiskalischer Status der Restaurant-Position ist unklar. Keine automatische Wiederholung; Kasse prüfen.",
                        CancellationToken.None);
                }
                else if (discardedPending || persistedItem is not null)
                {
                    await _commands.ReleaseForRecoveryAsync(
                        request.DeviceId,
                        request.CommandId,
                        CancellationToken.None);
                }
                else
                {
                    await _commands.FailAsync(
                        request.DeviceId,
                        request.CommandId,
                        ex.Message,
                        CancellationToken.None);
                }
            }
            catch
            {
            }

            throw;
        }
    }

    public async Task<RestaurantHandheldCommandResult> CancelItemAsync(
        RestaurantHandheldCancelItemRequest request,
        CancellationToken ct = default)
    {
        _entitlements.Require(
            RestaurantFeature.HandheldBestellung);

        await _pairing.RequireAuthenticatedAsync(
            request.DeviceId,
            request.DeviceToken,
            ct);

        var operatorUser = await RequireOperatorAsync(
            request.OperatorName,
            request.OperatorPin,
            request.DeviceId,
            request.OperatorSessionToken,
            ct);

        if (!operatorUser.Can(UserPermissions.ImmediateStorno))
        {
            throw new UnauthorizedAccessException(
                "Bediener ist nicht für Restaurant-Storno freigegeben.");
        }

        var reason = (request.Reason ?? "").Trim();
        if (reason.Length == 0)
        {
            throw new ArgumentException(
                "Stornogrund ist Pflicht.",
                nameof(request.Reason));
        }

        var requestHash = CommandHash(
            "CANCEL_ITEM",
            request.SessionId,
            request.ExpectedSessionVersion.ToString(),
            request.SessionItemId.ToString(),
            operatorUser.Username,
            reason);

        var claim = await _commands.BeginAsync(
            request.DeviceId,
            request.CommandId,
            "CANCEL_ITEM",
            requestHash,
            request.SessionId,
            ct);

        if (claim.State == RestaurantCommandClaimState.Completed)
        {
            return new RestaurantHandheldCommandResult(
                request.SessionId,
                claim.ResultSessionVersion);
        }

        if (claim.State == RestaurantCommandClaimState.Failed)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(claim.ErrorText)
                    ? "Restaurant-Storno ist bereits fehlgeschlagen. Bitte Ansicht aktualisieren und erneut senden."
                    : claim.ErrorText);
        }

        if (claim.State == RestaurantCommandClaimState.InProgress)
        {
            throw new InvalidOperationException(
                "Restaurant-Storno wird bereits verarbeitet. Bitte kurz synchronisieren.");
        }

        var kitchenJobId = CommandGuid(
            "KITCHEN-CANCEL",
            request.DeviceId,
            request.CommandId);

        RestaurantFiscalVorgang? vorgang = null;
        RestaurantSessionItem? cancelled = null;
        var actionId = CommandGuid(
            "RESTAURANT-STORNO-AUDIT",
            request.DeviceId,
            request.CommandId);
        var auditAuthorized = false;
        var auditApplied = false;
        long auditBeforeTotal = 0;
        long auditAfterTotal = 0;

        try
        {
            var item = await _restaurant.GetItemAsync(
                request.SessionId,
                request.SessionItemId,
                ct)
                ?? throw new InvalidOperationException(
                    "Restaurant-Position wurde nicht gefunden.");

            if (item.State == RestaurantSessionItemState.Cancelled)
            {
                if (claim.State != RestaurantCommandClaimState.Recovered)
                {
                    throw new InvalidOperationException(
                        "Restaurant-Position ist bereits storniert.");
                }

                cancelled = item;

                if (!await _fiscal.IsCurrentStateSecuredAsync(
                        request.SessionId,
                        ct))
                {
                    vorgang = await _fiscal.BeginChangeAsync(
                        request.SessionId,
                        operatorUser.Username,
                        ct);

                    await _fiscal.SecureCancelledItemAsync(
                        request.SessionId,
                        cancelled,
                        vorgang,
                        operatorUser.Username,
                        ct);

                    vorgang = null;
                }
            }
            else
            {
                if (item.State != RestaurantSessionItemState.Active)
                {
                    throw new InvalidOperationException(
                        "Nur eine offene Restaurant-Position kann storniert werden.");
                }

                var secured = await _fiscal.IsCurrentStateSecuredAsync(
                    request.SessionId,
                    ct);

                if (!secured)
                {
                    throw new InvalidOperationException(
                        "Bestellung/TSE-Stand stimmt nicht mit dem Tisch überein.");
                }

                var activeItems =
                    await _restaurant.ListActiveItemsAsync(
                        request.SessionId,
                        ct);
                auditBeforeTotal =
                    activeItems.Sum(x => x.LineTotalCents);
                auditAfterTotal =
                    Math.Max(
                        0,
                        auditBeforeTotal - item.LineTotalCents);

                await AppendRestaurantStornoAuditAsync(
                    actionId,
                    "AUTHORIZED",
                    operatorUser.Username,
                    request.DeviceId,
                    request.SessionId,
                    item,
                    reason,
                    auditBeforeTotal,
                    auditAfterTotal,
                    $"command_id={request.CommandId}; recovered={claim.State == RestaurantCommandClaimState.Recovered}",
                    ct);
                auditAuthorized = true;

                vorgang = await _fiscal.BeginChangeAsync(
                    request.SessionId,
                    operatorUser.Username,
                    ct);

                cancelled = await _restaurant.CancelItemAsync(
                    request.SessionId,
                    request.ExpectedSessionVersion,
                    request.SessionItemId,
                    operatorUser.Username,
                    request.DeviceId,
                    ct);

                await _fiscal.SecureCancelledItemAsync(
                    request.SessionId,
                    cancelled,
                    vorgang,
                    operatorUser.Username,
                    ct);

                vorgang = null;
            }

            if (!auditAuthorized)
            {
                var remaining =
                    await _restaurant.ListActiveItemsAsync(
                        request.SessionId,
                        ct);
                auditAfterTotal =
                    remaining.Sum(x => x.LineTotalCents);
                auditBeforeTotal =
                    auditAfterTotal + cancelled.LineTotalCents;
            }

            await AppendRestaurantStornoAuditAsync(
                actionId,
                "APPLIED",
                operatorUser.Username,
                request.DeviceId,
                request.SessionId,
                cancelled,
                reason,
                auditBeforeTotal,
                auditAfterTotal,
                $"command_id={request.CommandId}; recovered={claim.State == RestaurantCommandClaimState.Recovered}",
                ct);
            auditApplied = true;

            var session = await _restaurant.GetSessionAsync(
                request.SessionId,
                ct)
                ?? throw new InvalidOperationException(
                    "Tischvorgang nicht gefunden.");

            var table = await _restaurant.GetTableAsync(
                session.TableId,
                ct);

            var product = _catalog.Products
                .FirstOrDefault(x => x.Id == cancelled.ProductId);
            var category = product is null
                ? null
                : _catalog.Categories.FirstOrDefault(
                    x => x.Id == product.CategoryId);

            await _kitchen.EnqueueCancellationIdempotentAsync(
                session,
                cancelled,
                table?.DisplayName ?? "Tisch",
                operatorUser.Username,
                kitchenJobId,
                KitchenStations.Normalize(
                    category?.KitchenStation),
                ct);

            _kitchenDispatcher.Notify();

            await _commands.CompleteAsync(
                request.DeviceId,
                request.CommandId,
                session.Version,
                ct);

            return new RestaurantHandheldCommandResult(
                session.Id,
                session.Version);
        }
        catch (Exception ex)
        {
            if (auditAuthorized && !auditApplied && cancelled is not null)
            {
                try
                {
                    await AppendRestaurantStornoAuditAsync(
                        actionId,
                        "FAILED",
                        operatorUser.Username,
                        request.DeviceId,
                        request.SessionId,
                        cancelled,
                        reason,
                        auditBeforeTotal,
                        auditAfterTotal,
                        "error=" + ex.Message,
                        CancellationToken.None);
                }
                catch
                {
                }
            }

            if (vorgang is not null)
            {
                try
                {
                    await _fiscal.AbortChangeAsync(
                        vorgang,
                        operatorUser.Username,
                        CancellationToken.None);
                }
                catch
                {
                }
            }

            try
            {
                var persistedItem =
                    cancelled ??
                    await _restaurant.GetItemAsync(
                        request.SessionId,
                        request.SessionItemId,
                        CancellationToken.None);

                if (persistedItem?.State ==
                    RestaurantSessionItemState.Cancelled)
                {
                    await _commands.ReleaseForRecoveryAsync(
                        request.DeviceId,
                        request.CommandId,
                        CancellationToken.None);
                }
                else
                {
                    await _commands.FailAsync(
                        request.DeviceId,
                        request.CommandId,
                        ex.Message,
                        CancellationToken.None);
                }
            }
            catch
            {
            }

            throw;
        }
    }

    private static string CommandHash(
        params string[] values)
    {
        var raw = string.Join(
            "\u001f",
            values.Select(x => x ?? ""));

        return Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(raw)));
    }

    private static string CommandToken(
        string prefix,
        string deviceId,
        string commandId)
    {
        var hash = CommandHash(
            prefix,
            deviceId,
            commandId);

        return prefix + "-" + hash;
    }

    private static string CommandGuid(
        string prefix,
        string deviceId,
        string commandId)
    {
        var raw = string.Join(
            "\u001f",
            new[] { prefix, deviceId, commandId });

        var hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(raw));

        return new Guid(
            hash.AsSpan(0, 16))
            .ToString("N");
    }

    private Task AppendRestaurantStornoAuditAsync(
        string actionId,
        string phase,
        string actor,
        string deviceId,
        string sessionId,
        RestaurantSessionItem item,
        string reason,
        long beforeTotalCents,
        long afterTotalCents,
        string details,
        CancellationToken ct)
    {
        return _controlledActions.AppendAsync(
            new PosActionLogRequest
            {
                ActionId = actionId,
                Phase = phase,
                Actor = actor,
                RegisterId = string.IsNullOrWhiteSpace(deviceId)
                    ? "HANDHELD"
                    : deviceId,
                OperationId = sessionId,
                ActionType = "RESTAURANT_POSITION_STORNO",
                Reason = reason,
                EntityType = "RESTAURANT_SESSION_ITEM",
                EntityId = item.Id.ToString(),
                BeforeTotalCents = beforeTotalCents,
                AfterTotalCents = afterTotalCents,
                AmountCents = Math.Max(0, item.LineTotalCents),
                Details =
                    $"product_id={item.ProductId}; product={item.ProductName}; " +
                    $"qty_milli={item.QuantityMilli}; {details}"
            },
            ct);
    }

    private async Task<AuthenticatedUser> RequireOperatorAsync(
        string operatorName,
        string operatorPin,
        string deviceId,
        string operatorSessionToken,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(
                operatorSessionToken))
        {
            return await _operatorSessions.RequireAsync(
                deviceId,
                operatorSessionToken,
                ct);
        }

        var login = await _authentication.LoginWithPinAsync(
            (operatorName ?? "").Trim(),
            (operatorPin ?? "").Trim(),
            ct);

        if (!login.Success ||
            login.User is null ||
            !login.User.Can(UserPermissions.Sale))
        {
            throw new UnauthorizedAccessException(
                "Bediener/PIN ist ungültig oder nicht für Verkauf freigegeben.");
        }

        return login.User;
    }

}
