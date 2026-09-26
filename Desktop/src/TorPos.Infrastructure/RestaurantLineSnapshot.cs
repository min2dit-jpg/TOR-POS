using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TorPos.Core;

namespace TorPos.Infrastructure;

internal static class RestaurantLineSnapshot
{
    public static CartLine ToCartLine(
        RestaurantSessionItem item,
        decimal? quantity = null,
        long? exactLineTotalCents = null)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new CartLine
        {
            ProductId = item.ProductId,
            ProductName = item.ProductName,
            VariantName = item.VariantName,
            Quantity = quantity ?? item.Quantity,
            Unit = string.IsNullOrWhiteSpace(item.Unit) ? "Stück" : item.Unit,
            UnitPriceCents = item.UnitPriceCents,
            ListUnitPriceCents = item.EffectiveListUnitPriceCents,
            VatRate = item.VatRate,
            VatAllocations = item.VatAllocations.ToArray(),
            MenuComponents = item.MenuComponents.ToArray(),
            ImHausApplicable = item.ImHausApplicable,
            PfandCents = item.PfandCents,
            PersistedLineTotalCents =
                exactLineTotalCents ??
                (quantity is null ? item.LineTotalCents : null)
        };
    }

    public static string IdentityKey(CartLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var canonical = JsonSerializer.Serialize(new
        {
            line.ProductId,
            line.ProductName,
            line.VariantName,
            line.Unit,
            line.UnitPriceCents,
            ListUnitPriceCents = line.EffectiveListUnitPriceCents,
            VatRate = line.VatRate.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            line.PfandCents,
            line.ImHausApplicable,
            VatAllocations = line.VatAllocations
                .OrderBy(x => x.VatRate)
                .ThenBy(x => x.GrossCents)
                .ThenBy(x => x.MarketGrossCents)
                .ToArray(),
            MenuComponents = line.MenuComponents
                .OrderBy(x => x.ChoiceGroup, StringComparer.Ordinal)
                .ThenBy(x => x.ProductId)
                .ThenBy(x => x.Name, StringComparer.Ordinal)
                .ThenBy(x => x.Quantity)
                .ToArray()
        });

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
