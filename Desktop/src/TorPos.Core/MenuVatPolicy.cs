namespace TorPos.Core;

/// <summary>
/// R150: evaluates menu/combo articles that contain components with different
/// effective VAT rates. The menu stays one commercial article, but its gross
/// price can only be split safely when every component has a usable single-sale
/// market price. Allocation uses the components' gross single-sale prices as
/// weights and distributes cent rounding deterministically.
///
/// R151 completes the representation: the customer still sees one menu line,
/// while the checkout snapshot carries hidden per-rate gross allocations.
/// Those allocations are persisted with the sale and reused by receipt VAT,
/// TSE process data and DSFinV-K.
/// </summary>
public static class MenuVatPolicy
{
    public static MenuVatAnalysis Analyze(
        Product menu,
        IReadOnlyList<Product> catalog,
        bool imHaus,
        long? menuGrossCents = null,
        IReadOnlyList<MenuComponentSnapshot>? selectedComponents = null)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(catalog);

        if (!menu.IsCombo)
            return MenuVatAnalysis.NotCombo(menu);

        var byId = catalog.ToDictionary(x => x.Id);
        var marketByRate = new Dictionary<decimal, long>();

        IReadOnlyList<MenuComponentSnapshot> effectiveComponents;
        if (selectedComponents is { Count: > 0 })
        {
            effectiveComponents = selectedComponents;
        }
        else
        {
            if (menu.ComboItems.Any(x => x.IsChoice))
                return MenuVatAnalysis.Invalid(
                    menu,
                    "Menü enthält Auswahlgruppen. Vor dem Verkauf muss für jede Gruppe ein Artikel gewählt werden.");

            var fixedComponents = new List<MenuComponentSnapshot>();
            foreach (var item in menu.ComboItems.OrderBy(x => x.SortOrder))
            {
                if (!byId.TryGetValue(item.ComponentProductId, out var component))
                    return MenuVatAnalysis.Invalid(menu, $"Menübestandteil {item.ComponentProductId} fehlt im Artikelstamm.");

                if (component.PfandCents != 0)
                    return MenuVatAnalysis.Invalid(
                        menu,
                        $"Menübestandteil {component.Name} enthält Pfand. " +
                        "Menü-Pfand muss vor Produktivfreigabe separat abgebildet werden.");

                fixedComponents.Add(new MenuComponentSnapshot(
                    component.Id,
                    component.Name,
                    item.Quantity,
                    component.BasePriceCents,
                    component.VatRate,
                    component.ImHausApplicable,
                    ""));
            }

            effectiveComponents = fixedComponents;
        }

        foreach (var item in effectiveComponents)
        {
            if (item.Quantity <= 0m)
                return MenuVatAnalysis.Invalid(menu, $"Menübestandteil {item.Name} hat keine gültige Menge.");

            if (item.MarketPriceCents <= 0)
                return MenuVatAnalysis.Invalid(
                    menu,
                    $"Menübestandteil {item.Name} hat keinen positiven Einzelverkaufspreis. " +
                    "Für eine belastbare Aufteilung ist ein Marktwert erforderlich.");

            var marketGross = checked((long)Math.Round(
                item.Quantity * item.MarketPriceCents,
                MidpointRounding.AwayFromZero));

            if (marketGross <= 0)
                return MenuVatAnalysis.Invalid(menu, $"Marktwert von {item.Name} ist nicht positiv.");

            var rate = ImHausVat.Effective(
                item.VatRate,
                imHaus,
                item.ImHausApplicable);

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

            var analysis = Analyze(
                product,
                catalog,
                imHaus,
                line.UnitPriceCents,
                line.MenuComponents);
            if (!analysis.IsValid)
            {
                result.Add(analysis);
                continue;
            }

            // R151: ordinary menu pricing is supported even when rates are mixed.
            // A separate Angebot on top of a mixed menu would need an immutable
            // pre-promotion allocation snapshot for DSFinV-K Preisfindung; until
            // that representation exists, refuse it instead of inventing one.
            if (analysis.IsMixed && line.HasPromotion)
            {
                result.Add(MenuVatAnalysis.Invalid(
                    product,
                    "Zusätzliches ANGEBOT auf einem Menü mit gemischter MwSt. ist noch nicht freigegeben. " +
                    "Bitte den gewünschten Menüpreis direkt als Verkaufspreis des Menü-Artikels speichern."));
            }
        }

        return result;
    }

    public static CartLine[] ApplyAllocations(
        IEnumerable<CartLine> lines,
        IReadOnlyList<Product> catalog,
        bool imHaus)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(catalog);

        var byId = catalog.ToDictionary(x => x.Id);
        var result = new List<CartLine>();

        foreach (var line in lines)
        {
            if (line.ProductId <= 0 ||
                !byId.TryGetValue(line.ProductId, out var product) ||
                !product.IsCombo)
            {
                result.Add(CloneWithAllocations(line, line.VatAllocations));
                continue;
            }

            var analysis = Analyze(
                product,
                catalog,
                imHaus,
                line.UnitPriceCents,
                line.MenuComponents);
            if (!analysis.IsValid)
                throw new InvalidOperationException($"{product.Name}: {analysis.Message}");

            if (analysis.IsMixed && line.HasPromotion)
                throw new InvalidOperationException(
                    $"{product.Name}: zusätzliches ANGEBOT auf gemischtem Menü ist nicht freigegeben.");

            result.Add(CloneWithAllocations(line, analysis.Allocations.ToArray()));
        }

        return result.ToArray();
    }

    private static CartLine CloneWithAllocations(
        CartLine line,
        IReadOnlyList<MenuVatAllocation> allocations) =>
        new()
        {
            SaleItemId = line.SaleItemId,
            ProductId = line.ProductId,
            ProductName = line.ProductName,
            VariantName = line.VariantName,
            Barcode = line.Barcode,
            Quantity = line.Quantity,
            UnitPriceCents = line.UnitPriceCents,
            ListUnitPriceCents = line.EffectiveListUnitPriceCents,
            VatRate = line.VatRate,
            VatAllocations = allocations.ToArray(),
            MenuComponents = line.MenuComponents.ToArray(),
            ImHausApplicable = line.ImHausApplicable,
            PfandCents = line.PfandCents,
            PromotionId = line.PromotionId,
            PromotionName = line.PromotionName,
            PromotionPercent = line.PromotionPercent,
            PromotionDiscountUnitCents = line.PromotionDiscountUnitCents,
            PromotionStartDate = line.PromotionStartDate,
            PromotionEndDate = line.PromotionEndDate
        };

    /// <summary>
    /// Converts the hidden per-unit allocation into exact gross amounts for one
    /// cart/sale line. The last VAT bucket absorbs any cent rounding so the
    /// buckets always sum to LineTotalCents, including quantities > 1.
    /// </summary>
    public static IReadOnlyList<MenuVatAllocation> LineAllocations(CartLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (line.VatAllocations.Length == 0)
            return new[] { new MenuVatAllocation(line.VatRate, line.LineTotalCents, line.LineTotalCents) };

        var ordered = line.VatAllocations.OrderBy(x => x.VatRate).ToArray();
        var total = line.LineTotalCents;
        long assigned = 0;
        var result = new List<MenuVatAllocation>(ordered.Length);

        for (var i = 0; i < ordered.Length; i++)
        {
            var source = ordered[i];
            var gross = i == ordered.Length - 1
                ? total - assigned
                : (long)Math.Round(line.Quantity * source.GrossCents, MidpointRounding.AwayFromZero);
            assigned += gross;
            var market = (long)Math.Round(
                line.Quantity * source.MarketGrossCents,
                MidpointRounding.AwayFromZero);
            result.Add(new MenuVatAllocation(source.VatRate, gross, market));
        }

        return result;
    }

    public static IReadOnlyList<decimal> EffectiveRates(CartLine line) =>
        LineAllocations(line)
            .Where(x => x.GrossCents != 0)
            .Select(x => x.VatRate)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

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
