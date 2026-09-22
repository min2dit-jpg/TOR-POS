using System.Globalization;
using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

internal static class RestaurantBestellungExportLoader
{
    public static async Task<List<DsfinvkOrderRecord>> LoadInPeriodAsync(
        SqliteConnection c,
        string fromUtc,
        string toUtc,
        CancellationToken ct)
    {
        if (!await ExistsAsync(c, ct))
            return new List<DsfinvkOrderRecord>();

        var heads = new List<(long Id,string SessionId,int Sequence,string Kind,DateTimeOffset Started,DateTimeOffset Created,string Operator,long Total,DsfinvkTseResult Tse)>();

        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT id,session_id,sequence,kind,started_at,created_at,operator_name,total_cents,
                       serial_number,transaction_number,signature_counter,signature,
                       log_time,outage,outage_reason,start_log_time
                FROM restaurant_bestellungen
                WHERE created_at_utc > $from AND created_at_utc <= $to
                ORDER BY id;
                """;
            q.Parameters.AddWithValue("$from", fromUtc);
            q.Parameters.AddWithValue("$to", toUtc);

            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                DateTimeOffset? Time(int i) =>
                    string.IsNullOrWhiteSpace(r.GetString(i))
                        ? null
                        : DateTimeOffset.Parse(r.GetString(i), CultureInfo.InvariantCulture);

                heads.Add((
                    r.GetInt64(0),
                    r.GetString(1),
                    r.GetInt32(2),
                    r.GetString(3),
                    DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture),
                    r.GetString(6),
                    r.GetInt64(7),
                    new DsfinvkTseResult(
                        r.GetString(8),
                        r.GetString(9),
                        r.GetString(10),
                        r.GetString(11),
                        Time(12),
                        r.GetInt64(13) != 0,
                        r.GetString(14),
                        Time(15))));
            }
        }

        var result = new List<DsfinvkOrderRecord>();
        foreach (var h in heads)
        {
            var lines = new List<CartLine>();
            await using var q = c.CreateCommand();
            q.CommandText = """
                SELECT product_id,product_name,quantity_milli,unit_price_cents,vat_rate,pfand_cents
                FROM restaurant_bestellung_items
                WHERE bestellung_id=$id
                ORDER BY id;
                """;
            q.Parameters.AddWithValue("$id", h.Id);

            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                lines.Add(new CartLine
                {
                    ProductId = r.GetInt64(0),
                    ProductName = r.GetString(1),
                    Quantity = QuantityStorage.FromMilli(r.GetInt64(2)),
                    UnitPriceCents = r.GetInt64(3),
                    ListUnitPriceCents = r.GetInt64(3),
                    VatRate = Convert.ToDecimal(r.GetDouble(4)),
                    PfandCents = r.GetInt64(5),
                    Unit = "Stück"
                });
            }

            var kind = h.Kind switch
            {
                "AENDERUNG" => OrderBestellungKind.Aenderung,
                "STORNO" => OrderBestellungKind.Storno,
                _ => OrderBestellungKind.Annahme
            };

            result.Add(new DsfinvkOrderRecord(
                h.Id,
                ParkNumber: 0,
                PickupNumber: 0,
                h.Sequence,
                kind,
                h.Started,
                h.Created,
                h.Operator,
                ImHaus: true,
                lines,
                h.Tse,
                Training: false,
                CustomBonId: $"RB-{h.Id.ToString(CultureInfo.InvariantCulture)}",
                CustomAllocationGroup: $"Restaurant {h.SessionId}"));
        }

        return result;
    }

    public static async Task<Dictionary<long,string>> LoadSaleAllocationGroupsAsync(
        SqliteConnection c,
        CancellationToken ct)
    {
        var result = new Dictionary<long,string>();
        if (!await ExistsAsync(c, ct))
            return result;

        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT sale_id,session_id
            FROM restaurant_payment_reservations
            WHERE state='APPLIED' AND sale_id IS NOT NULL;
            """;

        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result[r.GetInt64(0)] = $"Restaurant {r.GetString(1)}";

        return result;
    }

    public static async Task<long> CountAfterAsync(
        SqliteConnection c,
        string fromUtc,
        CancellationToken ct)
    {
        if (!await ExistsAsync(c, ct))
            return 0;

        await using var q = c.CreateCommand();
        q.CommandText =
            "SELECT COUNT(*) FROM restaurant_bestellungen WHERE created_at_utc > $from;";
        q.Parameters.AddWithValue("$from", fromUtc);
        return Convert.ToInt64(await q.ExecuteScalarAsync(ct));
    }

    private static async Task<bool> ExistsAsync(
        SqliteConnection c,
        CancellationToken ct)
    {
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type='table' AND name='restaurant_bestellungen';
            """;
        return Convert.ToInt32(await q.ExecuteScalarAsync(ct)) == 1;
    }
}
