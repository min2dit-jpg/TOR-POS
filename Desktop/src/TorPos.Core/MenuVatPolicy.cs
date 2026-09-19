namespace TorPos.Core;

/// <summary>
/// R150: evaluates menu/combo articles that contain components with different
/// effective VAT rates. The menu stays one commercial article, but its gross
/// price can only be split safely when every component has a usable single-sale
/// market price. Allocation uses the components' gross single-sale prices as
/// weights and distributes cent rounding deterministically.
///
/// This is deliberately a guard/calculation primitive only. R150 does not yet
/// rewrite sale_items/DSFinV-K into multi-rate menu snapshots; until that
/// representation is implemented, a mixed-rate menu must not enter a
/// production checkout.
/// </summary>
public static class MenuVatPolicy
{
    public static MenuVatAnalysis Analyze(
        Product menu,
        IReadOnlyList<Product> catalog,
        bool imHaus,
        long? menuGrossCents = null)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(catalog);

        if (!menu.IsCombo)
            return MenuVatAnalysis.NotCombo(menu);

        var byId = catalog.ToDictionary(x => x.Id);
        var marketByRate = new Dictionary<decimal, long>();

        foreach (var item in menu.ComboItems.OrderBy(x => x.SortOrder))
        {
            if (!byId.TryGetValue(item.ComponentProductId, out var component))
                return MenuVatAnalysis.Invalid(menu, $"Menübestandteil {item.ComponentProductId} fehlt im Artikelstamm.");

            if (item.Quantity <= 0m)
                return MenuVatAnalysis.Invalid(menu, $"Menübestandteil {component.Name} hat keine gültige Menge.");

            // Pfand needs its own immutable fiscal/DSFinV-K position. The current
            // commercial combo line cannot represent that separately, so it is
            // blocked rather than hidden inside the menu turnover.
            if (component.PfandCents != 0)
                return MenuVatAnalysis.Invalid(
                    menu,
                    $"Menübestandteil {component.Name} enthält Pfand. " +
                    "Menü-Pfand muss vor Produktivfreigabe separat abgebildet werden.");

            var singleGross = component.BasePriceCents;
            if (singleGross <= 0)
                return MenuVatAnalysis.Invalid(
                    menu,
                    $"Menübestandteil {component.Name} hat keinen positiven Einzelverkaufspreis. " +
                    "Für eine belastbare Aufteilung ist ein Marktwert erforderlich.");

            var marketGross = checked((long)Math.Round(
                item.Quantity * singleGross,
                MidpointRounding.AwayFromZero));

            if (marketGross <= 0)
                return MenuVatAnalysis.Invalid(menu, $"Marktwert von {component.Name} ist nicht positiv.");

            var rate = ImHausVat.Effective(
                component.VatRate,
                imHaus,
                component.ImHausApplicable);

            marketByRate[rate] = checked(
                marketByRate.GetValueOrDefault(rate) + marketGross);
        }

        if (marketByRate.Count == 0)
            return MenuVatAnalysis.Invalid(menu, "Menü hat keine Bestandteile.");

        if (menu.PfandCents != 0)
            return MenuVatAnalysis.Invalid(
                menu,
                "Das Menü selbst enthält Pfand. Menü-Pfand muss vor Produktivfreigabe separat abgebildet werden.");

        var targetGross = menuGrossCents ?? menu.BasePriceCents;
        if (targetGross < 0)
            return MenuVatAnalysis.Invalid(menu, "Menüpreis darf für die Aufteilung nicht negativ sein.");

        var totalMarket = marketByRate.Values.Sum();
        if (totalMarket <= 0)
            return MenuVatAnalysis.Invalid(menu, "Summe der Marktwerte ist nicht positiv.");

        // A component/rate bucket must never receive more than the comparable
        // single-sale market value. If the menu is more expensive than all
        // components bought separately, a hypothetical market value must be
        // supplied/validated before production use; R150 refuses to guess it.
        if (targetGross > totalMarket)
            return MenuVatAnalysis.Invalid(
                menu,
                $"Menüpreis {targetGross} ct übersteigt die Summe der Einzelverkaufspreise {totalMarket} ct.");

        var drafts = marketByRate
            .OrderBy(x => x.Key)
            .Select(x =>
            {
                var exact = targetGross * (decimal)x.Value / totalMarket;
                var cents = (long)Math.Floor(exact);
                return new Draft(x.Key, x.Value, cents, exact - cents);
            })
            .ToList();

        var remainder = targetGross - drafts.Sum(x => x.GrossCents);
        foreach (var index in drafts
                     .Select((x, i) => new { Item = x, Index = i })
                     .OrderByDescending(x => x.Item.Fraction)
                     .ThenBy(x => x.Item.VatRate)
                     .Take(checked((int)remainder))
                     .Select(x => x.Index))
        {
            drafts[index] = drafts[index] with { GrossCents = drafts[index].GrossCents + 1 };
        }

        var allocations = drafts
            .OrderBy(x => x.VatRate)
            .Select(x => new MenuVatAllocation(x.VatRate, x.GrossCents, x.MarketGrossCents))
            .ToArray();

        return new MenuVatAnalysis(
            menu.Id,
            menu.Name,
            IsCombo: true,
            IsMixed: allocations.Select(x => x.VatRate).Distinct().Count() > 1,
            IsValid: true,
            "",
            allocations);
    }

    public static IReadOnlyList<MenuVatAnalysis> BlockingMenus(
        IEnumerable<CartLine> lines,
        IReadOnlyList<Product> catalog,
        bool imHaus)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(catalog);

        var byId = catalog.ToDictionary(x => x.Id);
        var result = new List<MenuVatAnalysis>();

        foreach (var line in lines
                     .Where(x => x.ProductId > 0)
                     .GroupBy(x => x.ProductId)
                     .Select(x => x.First()))
        {
            if (!byId.TryGetValue(line.ProductId, out var product) || !product.IsCombo)
                continue;

            var analysis = Analyze(product, catalog, imHaus, line.UnitPriceCents);
            if (!analysis.IsValid || analysis.IsMixed)
                result.Add(analysis);
        }

        return result;
    }

    private sealed record Draft(
        decimal VatRate,
        long MarketGrossCents,
        long GrossCents,
        decimal Fraction);
}

public sealed record MenuVatAllocation(
    decimal VatRate,
    long GrossCents,
    long MarketGrossCents);

public sealed record MenuVatAnalysis(
    long MenuProductId,
    string MenuName,
    bool IsCombo,
    bool IsMixed,
    bool IsValid,
    string Message,
    IReadOnlyList<MenuVatAllocation> Allocations)
{
    public static MenuVatAnalysis NotCombo(Product product) =>
        new(product.Id, product.Name, false, false, true, "", Array.Empty<MenuVatAllocation>());

    public static MenuVatAnalysis Invalid(Product product, string message) =>
        new(product.Id, product.Name, true, false, false, message, Array.Empty<MenuVatAllocation>());
}
