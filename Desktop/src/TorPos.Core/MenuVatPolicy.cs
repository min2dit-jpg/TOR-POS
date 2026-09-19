namespace TorPos.Core;

/// <summary>
/// KassenSichV/DSFinV-K production guard for menu/combo articles.
///
/// A commercial menu may contain components whose effective VAT rates differ
/// (for example 7 % food + 19 % drink for Außer Haus). TOR currently stores one
/// commercial CartLine with one VAT rate. Until the immutable sale/TSE/DSFinV-K
/// position model can preserve multiple VAT buckets for that single commercial
/// line, such a menu must never reach a real payment.
///
/// The market-value calculation below is deliberately retained even while the
/// production path is blocked: it gives diagnostics/tests a deterministic
/// allocation and prevents a later implementation from inventing a different
/// formula in receipt, TSE and export code.
///
/// Component deposits are also blocked for combos. A deposit is its own
/// DSFinV-K business case and must not be silently hidden inside a menu price.
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
                return MenuVatAnalysis.Invalid(
                    menu,
                    $"Menübestandteil {item.ComponentProductId} fehlt im Artikelstamm.");

            if (item.Quantity <= 0m)
                return MenuVatAnalysis.Invalid(
                    menu,
                    $"Menübestandteil {component.Name} hat keine gültige Menge.");

            // A component deposit needs its own immutable Pfand position in
            // DSFinV-K. The current commercial menu line cannot express that
            // faithfully, so refuse production instead of hiding it in Umsatz.
            if (component.PfandCents != 0)
                return MenuVatAnalysis.Invalid(
                    menu,
                    $"Menübestandteil {component.Name} enthält Pfand. " +
                    "Menü-Pfand muss vor Produktivfreigabe als eigener DSFinV-K-Vorgang/Position abgebildet werden.");

            var singleGross = component.BasePriceCents;
            if (singleGross <= 0)
                return MenuVatAnalysis.Invalid(
                    menu,
                    $"Menübestandteil {component.Name} hat keinen positiven Einzelverkaufspreis. " +
                    "Für eine belastbare Marktwertaufteilung ist ein Marktwert erforderlich.");

            var marketGross = checked((long)Math.Round(
                item.Quantity * singleGross,
                MidpointRounding.AwayFromZero));

            if (marketGross <= 0)
                return MenuVatAnalysis.Invalid(
                    menu,
                    $"Marktwert von {component.Name} ist nicht positiv.");

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
            return MenuVatAnalysis.Invalid(
                menu,
                "Menüpreis darf für die Aufteilung nicht negativ sein.");

        var totalMarket = marketByRate.Values.Sum();
        if (totalMarket <= 0)
            return MenuVatAnalysis.Invalid(
                menu,
                "Summe der Marktwerte ist nicht positiv.");

        // A bundle price above all known component market values is ambiguous.
        // Do not fabricate a tax allocation; require explicit master-data review.
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
            drafts[index] = drafts[index] with
            {
                GrossCents = drafts[index].GrossCents + 1
            };
        }

        var allocations = drafts
            .OrderBy(x => x.VatRate)
            .Select(x => new MenuVatAllocation(
                x.VatRate,
                x.GrossCents,
                x.MarketGrossCents))
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

    /// <summary>
    /// Returns only combos that cannot safely enter production: invalid master
    /// data, component/menu deposits, or more than one effective VAT rate.
    /// The caller must execute this before checkout journal/terminal/TSE effects.
    /// </summary>
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
            if (!byId.TryGetValue(line.ProductId, out var product) ||
                !product.IsCombo)
            {
                continue;
            }

            var analysis = Analyze(
                product,
                catalog,
                imHaus,
                line.UnitPriceCents);

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
        new(
            product.Id,
            product.Name,
            false,
            false,
            true,
            "",
            Array.Empty<MenuVatAllocation>());

    public static MenuVatAnalysis Invalid(
        Product product,
        string message) =>
        new(
            product.Id,
            product.Name,
            true,
            false,
            false,
            message,
            Array.Empty<MenuVatAllocation>());
}
