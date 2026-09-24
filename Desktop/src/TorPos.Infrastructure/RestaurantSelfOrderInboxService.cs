using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// Accepts customer-originated Self Order requests into a durable RECEIVED
/// inbox. This service never mutates Restaurant table items, kitchen state or
/// fiscal/TSE state. A later acceptance step is responsible for that boundary.
/// </summary>
public sealed class RestaurantSelfOrderInboxService
{
    private readonly SqliteDatabase _db;
    private readonly RestaurantEntitlementService _entitlements;
    private readonly RestaurantSelfOrderService _selfOrder;
    private readonly IProductCatalog _catalog;

    public RestaurantSelfOrderInboxService(
        SqliteDatabase db,
        RestaurantEntitlementService entitlements,
        RestaurantSelfOrderService selfOrder,
        IProductCatalog catalog)
    {
        _db = db;
        _entitlements = entitlements;
        _selfOrder = selfOrder;
        _catalog = catalog;
    }

    public async Task<RestaurantSelfOrderReceivedOrder> ReceiveAsync(
        string tablePublicToken,
        string publicSessionId,
        string capabilitySecret,
        string clientOrderId,
        IReadOnlyList<RestaurantSelfOrderLineRequest> lines,
        string note = "",
        CancellationToken ct = default)
    {
        _entitlements.Require(
            RestaurantFeature.QrTischbestellung);

        clientOrderId = (clientOrderId ?? "").Trim();
        note = (note ?? "").Trim();

        if (clientOrderId.Length is < 8 or > 100)
        {
            throw new ArgumentException(
                "Client-Order-ID ist ungültig.",
                nameof(clientOrderId));
        }

        if (note.Length > 500)
        {
            throw new ArgumentException(
                "Bestellnotiz ist zu lang.",
                nameof(note));
        }

        if (lines is null || lines.Count is < 1 or > 50)
        {
            throw new ArgumentException(
                "Self-Order benötigt 1 bis 50 Positionen.",
                nameof(lines));
        }

        var totalQuantity = 0;

        foreach (var request in lines)
        {
            if (request.ProductId <= 0 ||
                request.Quantity is < 1 or > 20)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(lines),
                    "Self-Order-Menge muss je Position zwischen 1 und 20 Stück liegen.");
            }

            totalQuantity = checked(
                totalQuantity + request.Quantity);

            if (totalQuantity > 99)
            {
                throw new InvalidOperationException(
                    "Self-Order-Gesamtmenge darf 99 Stück nicht überschreiten.");
            }
        }

        return await IoQueue.RunAsync(async () =>
        {
            // Validate the table/session capability only after entering the
            // same serialized mutation lane used by QR rotation, session close
            // and Restaurant table mutations. This removes the validation-to-
            // persist race: once validation succeeds here, no close/rotation
            // can interleave before the RECEIVED order is committed.
            var capability =
                await _selfOrder.ValidateOrderCapabilityAsync(
                    tablePublicToken,
                    publicSessionId,
                    capabilitySecret,
                    ct);

            if (!capability.Valid)
            {
                throw new UnauthorizedAccessException(
                    "Self-Order-Sitzung ist ungültig oder abgelaufen.");
            }

            var requestHash = ComputeRequestHash(
                capability.SessionId,
                clientOrderId,
                note,
                lines);

            var now = DateTimeOffset.UtcNow;
            var orderId = Guid.NewGuid().ToString("N");
            var publicOrderId =
                RestaurantSelfOrderSecurity.CreatePublicSessionId();
            var mode = capability.ApprovalMode ==
                RestaurantSelfOrderApprovalMode.Automatic
                    ? "AUTOMATIC"
                    : "CONFIRMATION_REQUIRED";

            await using var c = _db.OpenConnection();
            await using var tx = c.BeginTransaction();

            await using (var existing = c.CreateCommand())
            {
                existing.Transaction = tx;
                existing.CommandText = """
                    SELECT
                        id,public_order_id,request_hash,state,
                        approval_mode,total_cents,created_at
                    FROM restaurant_self_order_orders
                    WHERE session_id=$session
                      AND client_order_id=$client
                    LIMIT 1;
                    """;
                existing.Parameters.AddWithValue(
                    "$session",
                    capability.SessionId);
                existing.Parameters.AddWithValue(
                    "$client",
                    clientOrderId);

                await using var r =
                    await existing.ExecuteReaderAsync(ct);

                if (await r.ReadAsync(ct))
                {
                    var storedHash = r.GetString(2);
                    if (!string.Equals(
                            storedHash,
                            requestHash,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Client-Order-ID wurde bereits für einen anderen Bestellinhalt verwendet.");
                    }

                    var existingState =
                        Enum.Parse<RestaurantSelfOrderOrderState>(
                            r.GetString(3),
                            ignoreCase: true);
                    var existingMode =
                        string.Equals(
                            r.GetString(4),
                            "AUTOMATIC",
                            StringComparison.Ordinal)
                            ? RestaurantSelfOrderApprovalMode.Automatic
                            : RestaurantSelfOrderApprovalMode.ConfirmationRequired;

                    var replay = new RestaurantSelfOrderReceivedOrder(
                        r.GetString(0),
                        r.GetString(1),
                        capability.SessionId,
                        capability.TableId,
                        existingState,
                        existingMode,
                        r.GetInt64(5),
                        DateTimeOffset.Parse(r.GetString(6)),
                        Replay: true);

                    await tx.CommitAsync(ct);
                    return replay;
                }
            }

            var snapshots = new List<LineSnapshot>(
                lines.Count);
            long totalCents = 0;

            for (var index = 0; index < lines.Count; index++)
            {
                var request = lines[index];

                var product = _catalog.Products
                    .FirstOrDefault(x =>
                        x.Id == request.ProductId &&
                        x.IsActive)
                    ?? throw new InvalidOperationException(
                        "Artikel ist nicht vorhanden oder deaktiviert.");

                if (product.IsWeighted ||
                    product.IsCombo ||
                    product.Variants.Count > 0)
                {
                    throw new InvalidOperationException(
                        "Dieser Artikel benötigt eine erweiterte Self-Order-Auswahl und ist in dieser Stufe noch gesperrt.");
                }

                var unitPriceCents = checked(
                    product.BasePriceCents +
                    product.PfandCents);
                var quantityMilli = checked(
                    request.Quantity * 1000L);
                var lineTotalCents = checked(
                    unitPriceCents *
                    request.Quantity);

                totalCents = checked(
                    totalCents +
                    lineTotalCents);

                snapshots.Add(
                    new LineSnapshot(
                        index + 1,
                        product.Id,
                        product.Name,
                        quantityMilli,
                        unitPriceCents,
                        product.VatRate,
                        product.PfandCents,
                        lineTotalCents));
            }

            await using (var order = c.CreateCommand())
            {
                order.Transaction = tx;
                order.CommandText = """
                    INSERT INTO restaurant_self_order_orders(
                        id,public_order_id,session_id,table_id,
                        client_order_id,request_hash,state,approval_mode,
                        note,total_cents,created_at,updated_at)
                    VALUES(
                        $id,$public,$session,$table,
                        $client,$hash,'RECEIVED',$mode,
                        $note,$total,$now,$now);
                    """;
                order.Parameters.AddWithValue(
                    "$id",
                    orderId);
                order.Parameters.AddWithValue(
                    "$public",
                    publicOrderId);
                order.Parameters.AddWithValue(
                    "$session",
                    capability.SessionId);
                order.Parameters.AddWithValue(
                    "$table",
                    capability.TableId);
                order.Parameters.AddWithValue(
                    "$client",
                    clientOrderId);
                order.Parameters.AddWithValue(
                    "$hash",
                    requestHash);
                order.Parameters.AddWithValue(
                    "$mode",
                    mode);
                order.Parameters.AddWithValue(
                    "$note",
                    note);
                order.Parameters.AddWithValue(
                    "$total",
                    totalCents);
                order.Parameters.AddWithValue(
                    "$now",
                    now.ToString("O"));
                await order.ExecuteNonQueryAsync(ct);
            }

            foreach (var line in snapshots)
            {
                await using var item = c.CreateCommand();
                item.Transaction = tx;
                item.CommandText = """
                    INSERT INTO restaurant_self_order_order_items(
                        order_id,line_no,product_id,product_name,
                        quantity_milli,unit_price_cents,vat_rate,
                        pfand_cents,line_total_cents)
                    VALUES(
                        $order,$line,$product,$name,
                        $quantity,$price,$vat,
                        $pfand,$total);
                    """;
                item.Parameters.AddWithValue(
                    "$order",
                    orderId);
                item.Parameters.AddWithValue(
                    "$line",
                    line.LineNo);
                item.Parameters.AddWithValue(
                    "$product",
                    line.ProductId);
                item.Parameters.AddWithValue(
                    "$name",
                    line.ProductName);
                item.Parameters.AddWithValue(
                    "$quantity",
                    line.QuantityMilli);
                item.Parameters.AddWithValue(
                    "$price",
                    line.UnitPriceCents);
                item.Parameters.AddWithValue(
                    "$vat",
                    line.VatRate);
                item.Parameters.AddWithValue(
                    "$pfand",
                    line.PfandCents);
                item.Parameters.AddWithValue(
                    "$total",
                    line.LineTotalCents);
                await item.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);

            return new RestaurantSelfOrderReceivedOrder(
                orderId,
                publicOrderId,
                capability.SessionId,
                capability.TableId,
                RestaurantSelfOrderOrderState.Received,
                capability.ApprovalMode,
                totalCents,
                now,
                Replay: false);
        });
    }

    private static string ComputeRequestHash(
        string sessionId,
        string clientOrderId,
        string note,
        IReadOnlyList<RestaurantSelfOrderLineRequest> lines)
    {
        var canonical = JsonSerializer.Serialize(
            new
            {
                sessionId,
                clientOrderId,
                note,
                lines = lines.Select((x, index) => new
                {
                    lineNo = index + 1,
                    x.ProductId,
                    x.Quantity
                })
            });

        return Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private sealed record LineSnapshot(
        int LineNo,
        long ProductId,
        string ProductName,
        long QuantityMilli,
        long UnitPriceCents,
        decimal VatRate,
        long PfandCents,
        long LineTotalCents);
}
