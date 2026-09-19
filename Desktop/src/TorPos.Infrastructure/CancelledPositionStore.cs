using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// R143: the positions cancelled during capture of a sale or training sale
/// (DSFinV-K 4.2.3), stored once with the receipt they belong to. Shared by
/// <c>sale_cancelled_items</c> and <c>training_cancelled_items</c>.
/// </summary>
internal static class CancelledPositionStore
{
    public const string Sales = "sale_cancelled_items";
    public const string Trainings = "training_cancelled_items";

    public static async Task InsertAsync(SqliteConnection c, SqliteTransaction tx, string table, long ownerId, IEnumerable<CartLine>? lines, CancellationToken ct)
    {
        var owner = OwnerColumn(table);
        foreach (var line in lines ?? Array.Empty<CartLine>())
        {
            if (line.Quantity <= 0)
                continue;

            await using var q = c.CreateCommand();
            q.Transaction = tx;
            q.CommandText = $"""
                INSERT INTO {table}(
                  {owner},product_id,product_name,variant_name,barcode,quantity,unit_price_cents,vat_rate,pfand_cents,
                  list_unit_price_cents,promotion_id,promotion_name,promotion_percent,promotion_discount_unit_cents,
                  vat_allocations_json,menu_components_json)
                VALUES($o,$p,$n,$v,$b,$q,$u,$vat,$pfand,$list,$pid,$pname,$ppct,$punit,$vatAllocations,$menuComponents);
                """;
            q.Parameters.AddWithValue("$o", ownerId);
            q.Parameters.AddWithValue("$p", line.ProductId);
            q.Parameters.AddWithValue("$n", line.ProductName);
            q.Parameters.AddWithValue("$v", line.VariantName);
            q.Parameters.AddWithValue("$b", line.Barcode);
            q.Parameters.AddWithValue("$q", (double)line.Quantity);
            q.Parameters.AddWithValue("$u", line.UnitPriceCents);
            q.Parameters.AddWithValue("$vat", (double)line.VatRate);
            q.Parameters.AddWithValue("$pfand", line.PfandCents);
            q.Parameters.AddWithValue("$list", line.EffectiveListUnitPriceCents);
            q.Parameters.AddWithValue("$pid", line.PromotionId);
            q.Parameters.AddWithValue("$pname", line.PromotionName);
            q.Parameters.AddWithValue("$ppct", line.PromotionPercent);
            q.Parameters.AddWithValue("$punit", line.PromotionDiscountUnitCents);
            q.Parameters.AddWithValue("$vatAllocations", VatAllocationStorage.Serialize(line));
            q.Parameters.AddWithValue("$menuComponents", MenuComponentStorage.Serialize(line));
            await q.ExecuteNonQueryAsync(ct);
        }
    }

    public static async Task<IReadOnlyList<CartLine>> LoadAsync(SqliteConnection c, string table, long ownerId, CancellationToken ct)
    {
        var lines = new List<CartLine>();
        await using var q = c.CreateCommand();
        q.CommandText = $"""
            SELECT product_id,product_name,variant_name,barcode,quantity,unit_price_cents,vat_rate,pfand_cents,
                   list_unit_price_cents,promotion_id,promotion_name,promotion_percent,promotion_discount_unit_cents,
                   COALESCE(vat_allocations_json,''),
                   COALESCE(menu_components_json,'')
            FROM {table} WHERE {OwnerColumn(table)}=$o ORDER BY id;
            """;
        q.Parameters.AddWithValue("$o", ownerId);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            lines.Add(new CartLine
            {
                ProductId = r.GetInt64(0),
                ProductName = r.GetString(1),
                VariantName = r.GetString(2),
                Barcode = r.GetString(3),
                Quantity = Convert.ToDecimal(r.GetDouble(4)),
                UnitPriceCents = r.GetInt64(5),
                VatRate = Convert.ToDecimal(r.GetDouble(6)),
                PfandCents = r.GetInt64(7),
                ListUnitPriceCents = r.GetInt64(8),
                PromotionId = r.GetInt64(9),
                PromotionName = r.GetString(10),
                PromotionPercent = r.GetInt32(11),
                PromotionDiscountUnitCents = r.GetInt64(12),
                VatAllocations = VatAllocationStorage.Deserialize(r.GetString(13)),
                MenuComponents = MenuComponentStorage.Deserialize(r.GetString(14))
            });
        }

        return lines;
    }

    private static string OwnerColumn(string table) => table switch
    {
        Sales => "sale_id",
        Trainings => "training_id",
        _ => throw new ArgumentOutOfRangeException(nameof(table))
    };
}
