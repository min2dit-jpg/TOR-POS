namespace TorPos.Core;

/// <summary>R137: what a secured order record documents.</summary>
public enum OrderBestellungKind
{
    /// <summary>The order was accepted: all its positions.</summary>
    Annahme,

    /// <summary>The accepted order was changed: only the difference.</summary>
    Aenderung,

    /// <summary>The order was cancelled: everything secured so far, with reversed sign.</summary>
    Storno
}

/// <summary>
/// R137: the positions of an order change or cancellation.
///
/// DSFinV-K 4.2.3: orders are Vorgänge of their own and secured separately;
/// once a transaction is signed it is never changed - a cancelled order is "ein
/// neuer Datensatz mit umgekehrtem Vorzeichen, der wiederum abgesichert werden
/// muss", a removed position a record with negated quantity. So every change of
/// an accepted order is its own Bestellung-V1 transaction holding exactly the
/// difference between what the TSE already secured and the order as it is now.
/// </summary>
public static class OrderBestellungDelta
{
    /// <summary>
    /// R138: technical product id of a discount position in an order record.
    /// BMF Kassen-FAQ: "Alle Veränderungen müssen nachvollziehbar in Form einer
    /// Bestellung abgebildet werden. Die Summe aus der Menge multipliziert mit
    /// dem Bruttopreis aller Bestellungen muss dem Gesamtbruttobetrag der
    /// entsprechenden Rechnungen entsprechen." A manual discount on the receipt
    /// therefore is a position of the order, one per VAT rate.
    /// </summary>
    public const long DiscountProductId = -9_000_000;

    public const string DiscountName = "Rabatt";

    public static bool IsDiscount(CartLine line) => line.ProductId == DiscountProductId;

    /// <summary>
    /// R138: the positions of an order together with its receipt discount, split
    /// by VAT rate exactly as the receipt splits it (VatSummaryCalculator), so the
    /// positions add up to the gross amount that is paid.
    /// </summary>
    public static IReadOnlyList<CartLine> WithDiscount(IReadOnlyList<CartLine> lines, long discountCents)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (discountCents == 0 || lines.Count == 0)
            return lines;

        var undiscounted = lines
            .SelectMany(MenuVatPolicy.LineAllocations)
            .GroupBy(a => a.VatRate)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.GrossCents));
        var result = lines.ToList();
        foreach (var group in VatSummaryCalculator.Compute(lines, discountCents))
        {
            var share = group.GrossCents - undiscounted[group.Rate];
            if (share == 0)
                continue;

            result.Add(new CartLine
            {
                ProductId = DiscountProductId,
                ProductName = DiscountName,
                Quantity = 1,
                UnitPriceCents = share,
                ListUnitPriceCents = share,
                VatRate = group.Rate,
                ImHausApplicable = false,
            });
        }

        return result;
    }

    /// <summary>
    /// The positions to secure so that the secured records add up to
    /// <paramref name="current"/>. Nothing secured yet: the order's positions as
    /// they are (acceptance). Otherwise one line per changed position with the
    /// quantity difference - positive when added, negative when removed.
    /// </summary>
    public static IReadOnlyList<CartLine> Compute(IReadOnlyList<CartLine> secured, IReadOnlyList<CartLine> current)
    {
        ArgumentNullException.ThrowIfNull(secured);
        ArgumentNullException.ThrowIfNull(current);

        if (secured.Count == 0)
            return current.Where(l => l.Quantity != 0).Select(l => WithQuantity(l, l.Quantity)).ToList();

        var before = Group(secured);
        var after = Group(current);
        var result = new List<CartLine>();

        foreach (var (key, (template, quantity)) in after)
        {
            var old = before.TryGetValue(key, out var found) ? found.Quantity : 0m;
            if (quantity != old)
                result.Add(WithQuantity(template, quantity - old));
        }

        foreach (var (key, (template, quantity)) in before)
        {
            if (!after.ContainsKey(key) && quantity != 0)
                result.Add(WithQuantity(template, -quantity));
        }

        return result;
    }

    /// <summary>The net positions of all records secured so far, zero quantities left out.</summary>
    public static IReadOnlyList<CartLine> Net(IEnumerable<CartLine> recordLines) =>
        Group(recordLines)
            .Where(g => g.Value.Quantity != 0)
            .Select(g => WithQuantity(g.Value.Template, g.Value.Quantity))
            .ToList();

    /// <summary>All positions of <paramref name="lines"/> with reversed sign (cancellation).</summary>
    public static IReadOnlyList<CartLine> Reverse(IReadOnlyList<CartLine> lines) =>
        lines.Select(l => WithQuantity(l, -l.Quantity)).ToList();

    public static CartLine WithQuantity(CartLine line, decimal quantity) => new()
    {
        ProductId = line.ProductId,
        ProductName = line.ProductName,
        VariantName = line.VariantName,
        Barcode = line.Barcode,
        Quantity = quantity,
        UnitPriceCents = line.UnitPriceCents,
        ListUnitPriceCents = line.EffectiveListUnitPriceCents,
        VatRate = line.VatRate,
        VatAllocations = line.VatAllocations.ToArray(),
        ImHausApplicable = line.ImHausApplicable,
        PfandCents = line.PfandCents,
        PromotionId = line.PromotionId,
        PromotionName = line.PromotionName,
        PromotionPercent = line.PromotionPercent,
        PromotionDiscountUnitCents = line.PromotionDiscountUnitCents,
        PromotionStartDate = line.PromotionStartDate,
        PromotionEndDate = line.PromotionEndDate,
    };

    // A position is the same article at the same price and rate; the VAT rate
    // is part of it, so switching Im Haus/Außer Haus changes the position.
    private readonly record struct Key(long ProductId, string Name, string Variant, long UnitPrice, decimal Vat, long Pfand, long PromotionId);

    private static Dictionary<Key, (CartLine Template, decimal Quantity)> Group(IEnumerable<CartLine> lines)
    {
        // Insertion order of Dictionary is kept as long as nothing is removed.
        var groups = new Dictionary<Key, (CartLine Template, decimal Quantity)>();
        foreach (var line in lines)
        {
            var key = new Key(line.ProductId, line.ProductName, line.VariantName, line.UnitPriceCents, line.VatRate, line.PfandCents, line.PromotionId);
            groups[key] = groups.TryGetValue(key, out var existing)
                ? (existing.Template, existing.Quantity + line.Quantity)
                : (line, line.Quantity);
        }

        return groups;
    }
}
