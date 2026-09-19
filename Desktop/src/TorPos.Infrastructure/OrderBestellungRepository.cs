using System.Globalization;
using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// R137: every secured order record - acceptance, change, cancellation - once,
/// immutable, with the positions it secured and its TSE result
/// (DSFinV-K 4.2.3). What the TSE has secured for an order is the sum of its
/// records; <c>parked_receipts</c> keeps only the order as it is now.
/// </summary>
public sealed class OrderBestellungRepository
{
    private readonly SqliteDatabase _db;

    public OrderBestellungRepository(SqliteDatabase db) => _db = db;

    /// <summary>How many records the order has, and the net positions they secured.</summary>
    public Task<(int Count, IReadOnlyList<CartLine> Secured)> SecuredAsync(long parkedReceiptId, CancellationToken ct = default) =>
        IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            var count = 0;
            await using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT COUNT(*) FROM order_bestellungen WHERE parked_receipt_id=$id;";
                q.Parameters.AddWithValue("$id", parkedReceiptId);
                count = Convert.ToInt32(await q.ExecuteScalarAsync(ct));
            }

            var lines = new List<CartLine>();
            await using (var q = c.CreateCommand())
            {
                q.CommandText = """
                    SELECT i.product_id,i.product_name,i.variant_name,i.barcode,i.quantity,i.unit_price_cents,i.vat_rate,i.pfand_cents,
                           i.list_unit_price_cents,i.promotion_id,i.promotion_name,i.promotion_percent,i.promotion_discount_unit_cents,
                           COALESCE(i.vat_allocations_json,'')
                    FROM order_bestellung_items i
                    JOIN order_bestellungen b ON b.id=i.bestellung_id
                    WHERE b.parked_receipt_id=$id
                    ORDER BY b.sequence,i.id;
                    """;
                q.Parameters.AddWithValue("$id", parkedReceiptId);
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    lines.Add(ReadLine(r, 0));
            }

            return (count, OrderBestellungDelta.Net(lines));
        });

    /// <summary>Stores one record with its positions and TSE result; the sequence is the next one of the order.</summary>
    public Task<DsfinvkOrderRecord> InsertAsync(
        ParkedReceipt order,
        OrderBestellungKind kind,
        DateTimeOffset startedAt,
        string operatorName,
        IReadOnlyList<CartLine> lines,
        SaleTseResult result,
        CancellationToken ct = default,
        DateTimeOffset? createdAt = null) =>
        IoQueue.RunAsync(async () =>
        {
            var now = createdAt ?? DateTimeOffset.Now;
            await using var c = _db.OpenConnection();
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);

            int sequence;
            await using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT COALESCE(MAX(sequence),0)+1 FROM order_bestellungen WHERE parked_receipt_id=$id;";
                q.Parameters.AddWithValue("$id", order.Id);
                sequence = Convert.ToInt32(await q.ExecuteScalarAsync(ct));
            }

            long id;
            await using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = """
                    INSERT INTO order_bestellungen(
                      parked_receipt_id,sequence,kind,started_at,created_at,operator_name,im_haus,total_cents,
                      client_id,transaction_number,signature_counter,serial_number,signature,start_log_time,log_time,outage,outage_reason)
                    VALUES($order,$seq,$kind,$started,$created,$op,$imHaus,$total,
                      $client,$tanr,$sigz,$serial,$sig,$startLog,$log,$outage,$reason);
                    SELECT last_insert_rowid();
                    """;
                q.Parameters.AddWithValue("$order", order.Id);
                q.Parameters.AddWithValue("$seq", sequence);
                q.Parameters.AddWithValue("$kind", KindText(kind));
                q.Parameters.AddWithValue("$started", startedAt.ToString("O"));
                q.Parameters.AddWithValue("$created", now.ToString("O"));
                q.Parameters.AddWithValue("$op", operatorName ?? "");
                q.Parameters.AddWithValue("$imHaus", order.ImHaus ? 1 : 0);
                q.Parameters.AddWithValue("$total", lines.Sum(l => l.LineTotalCents));
                q.Parameters.AddWithValue("$client", result.ClientId);
                q.Parameters.AddWithValue("$tanr", result.TransactionNumber);
                q.Parameters.AddWithValue("$sigz", result.SignatureCounter);
                q.Parameters.AddWithValue("$serial", result.SerialNumber);
                q.Parameters.AddWithValue("$sig", result.Signature);
                q.Parameters.AddWithValue("$startLog", result.StartLogTime?.ToString("O") ?? "");
                q.Parameters.AddWithValue("$log", result.LogTime?.ToString("O") ?? "");
                q.Parameters.AddWithValue("$outage", result.Signed ? 0 : 1);
                q.Parameters.AddWithValue("$reason", result.OutageMessage);
                id = Convert.ToInt64(await q.ExecuteScalarAsync(ct));
            }

            foreach (var line in lines)
            {
                await using var item = c.CreateCommand();
                item.Transaction = tx;
                item.CommandText = """
                    INSERT INTO order_bestellung_items(
                      bestellung_id,product_id,product_name,variant_name,barcode,quantity,unit_price_cents,vat_rate,
                      pfand_cents,line_total_cents,list_unit_price_cents,promotion_id,promotion_name,promotion_percent,
                      promotion_discount_unit_cents,vat_allocations_json)
                    VALUES($b,$p,$n,$v,$bc,$q,$u,$vat,$pfand,$total,$list,$pid,$pname,$ppct,$punit,$vatAllocations);
                    """;
                item.Parameters.AddWithValue("$b", id);
                item.Parameters.AddWithValue("$p", line.ProductId);
                item.Parameters.AddWithValue("$n", line.ProductName);
                item.Parameters.AddWithValue("$v", line.VariantName);
                item.Parameters.AddWithValue("$bc", line.Barcode);
                item.Parameters.AddWithValue("$q", (double)line.Quantity);
                item.Parameters.AddWithValue("$u", line.UnitPriceCents);
                item.Parameters.AddWithValue("$vat", (double)line.VatRate);
                item.Parameters.AddWithValue("$pfand", line.PfandCents);
                item.Parameters.AddWithValue("$total", line.LineTotalCents);
                item.Parameters.AddWithValue("$list", line.EffectiveListUnitPriceCents);
                item.Parameters.AddWithValue("$pid", line.PromotionId);
                item.Parameters.AddWithValue("$pname", line.PromotionName);
                item.Parameters.AddWithValue("$ppct", line.PromotionPercent);
                item.Parameters.AddWithValue("$punit", line.PromotionDiscountUnitCents);
                item.Parameters.AddWithValue("$vatAllocations", VatAllocationStorage.Serialize(line));
                await item.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);

            return new DsfinvkOrderRecord(
                id, order.ParkNumber, order.PickupNumber, sequence, kind, startedAt, now, operatorName ?? "", order.ImHaus, lines,
                new DsfinvkTseResult(result.SerialNumber, result.TransactionNumber, result.SignatureCounter, result.Signature,
                    result.LogTime, !result.Signed, result.OutageMessage, result.StartLogTime));
        });

    /// <summary>Order records in a closing window, for the DSFinV-K export.</summary>
    internal static async Task<List<DsfinvkOrderRecord>> LoadInPeriodAsync(SqliteConnection c, string fromUtc, string toUtc, CancellationToken ct)
    {
        var heads = new List<DsfinvkOrderRecord>();
        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT b.id,p.park_number,p.pickup_number,b.sequence,b.kind,b.started_at,b.created_at,b.operator_name,b.im_haus,
                       b.serial_number,b.transaction_number,b.signature_counter,b.signature,b.log_time,b.outage,b.outage_reason,b.start_log_time,
                       COALESCE(p.is_training,0)
                FROM order_bestellungen b
                JOIN parked_receipts p ON p.id=b.parked_receipt_id
                WHERE b.created_at_utc > $from AND b.created_at_utc <= $to
                ORDER BY b.id;
                """;
            q.Parameters.AddWithValue("$from", fromUtc);
            q.Parameters.AddWithValue("$to", toUtc);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                DateTimeOffset? Time(int i) => string.IsNullOrWhiteSpace(r.GetString(i)) ? null : DateTimeOffset.Parse(r.GetString(i), CultureInfo.InvariantCulture);
                heads.Add(new DsfinvkOrderRecord(
                    r.GetInt64(0),
                    r.GetInt64(1),
                    r.GetInt64(2),
                    r.GetInt32(3),
                    ParseKind(r.GetString(4)),
                    DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture),
                    r.GetString(7),
                    r.GetInt64(8) != 0,
                    Array.Empty<CartLine>(),
                    new DsfinvkTseResult(r.GetString(9), r.GetString(10), r.GetString(11), r.GetString(12), Time(13), r.GetInt64(14) != 0, r.GetString(15), Time(16)),
                    r.GetInt64(17) != 0));
            }
        }

        var result = new List<DsfinvkOrderRecord>();
        foreach (var head in heads)
        {
            var lines = new List<CartLine>();
            await using var q = c.CreateCommand();
            q.CommandText = """
                SELECT product_id,product_name,variant_name,barcode,quantity,unit_price_cents,vat_rate,pfand_cents,
                       list_unit_price_cents,promotion_id,promotion_name,promotion_percent,promotion_discount_unit_cents,
                       COALESCE(vat_allocations_json,'')
                FROM order_bestellung_items WHERE bestellung_id=$id ORDER BY id;
                """;
            q.Parameters.AddWithValue("$id", head.Id);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                lines.Add(ReadLine(r, 0));
            result.Add(head with { Lines = lines });
        }

        return result;
    }

    public static string KindText(OrderBestellungKind kind) => kind switch
    {
        OrderBestellungKind.Aenderung => "AENDERUNG",
        OrderBestellungKind.Storno => "STORNO",
        _ => "ANNAHME",
    };

    private static OrderBestellungKind ParseKind(string text) => text switch
    {
        "AENDERUNG" => OrderBestellungKind.Aenderung,
        "STORNO" => OrderBestellungKind.Storno,
        _ => OrderBestellungKind.Annahme,
    };

    private static CartLine ReadLine(SqliteDataReader r, int o) => new()
    {
        ProductId = r.GetInt64(o),
        ProductName = r.GetString(o + 1),
        VariantName = r.GetString(o + 2),
        Barcode = r.GetString(o + 3),
        Quantity = Convert.ToDecimal(r.GetDouble(o + 4)),
        UnitPriceCents = r.GetInt64(o + 5),
        VatRate = Convert.ToDecimal(r.GetDouble(o + 6)),
        PfandCents = r.GetInt64(o + 7),
        ListUnitPriceCents = r.GetInt64(o + 8),
        PromotionId = r.GetInt64(o + 9),
        PromotionName = r.GetString(o + 10),
        PromotionPercent = r.GetInt32(o + 11),
        PromotionDiscountUnitCents = r.GetInt64(o + 12),
        VatAllocations = VatAllocationStorage.Deserialize(r.GetString(o + 13))
    };
}
