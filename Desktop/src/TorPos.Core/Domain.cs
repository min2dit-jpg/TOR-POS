namespace TorPos.Core;

public sealed record ProductGroup(
    long Id,
    string Name,
    int SortOrder = 0);

public sealed record Category(
    long Id,
    long GroupId,
    string Name,
    decimal VatRate = 19m,
    int SortOrder = 0,
    string TileColor = "#17466A",
    string KitchenStation = "",
    // R97: whether the Im-Haus/Außer-Haus VAT rule (§12 UStG, see
    // ImHausVat) applies to this Warengruppe at all. Getränke are always
    // 19% anyway - ImHausVat.Effective already leaves a 19% rate
    // untouched regardless of this flag - but admins asked for an
    // explicit, visible per-Warengruppe switch instead of relying on an
    // invisible "only 7% rates change" rule. Default true preserves
    // existing behavior for every category that predates this field.
    bool ImHausApplicable = true);

/// <summary>
/// Fixed kitchen-station codes a Warengruppe can be routed to. "" means
/// "no station" - those lines print on the default Küchendrucker.
/// </summary>
public static class KitchenStations
{
    public const string None = "";
    public const string Grill = "GRILL";
    public const string Fritteuse = "FRITTEUSE";
    public const string Getraenke = "GETRAENKE";

    public static readonly IReadOnlyList<string> All = new[] { None, Grill, Fritteuse, Getraenke };

    public static string Normalize(string? station)
    {
        var value = (station ?? "").Trim().ToUpperInvariant();
        return All.Contains(value) ? value : None;
    }

    public static string DisplayName(string station) => station switch
    {
        Grill => "Grill",
        Fritteuse => "Fritteuse",
        Getraenke => "Getränke",
        _ => "(Standard-Küchendrucker)"
    };

    /// <summary>Settings-key prefix used to store this station's own printer, e.g. "device.kitchen_printer.station.grill".</summary>
    public static string SettingsPrefix(string station) => $"device.kitchen_printer.station.{station.ToLowerInvariant()}";
}

public sealed record ProductVariant(
    long Id,
    long ProductId,
    string Name,
    long PriceCents,
    int SortOrder = 0,
    bool IsActive = true);

public sealed record ProductComboItem(
    long ProductId,
    long ComponentProductId,
    string ComponentName,
    decimal Quantity,
    int SortOrder = 0,
    string ChoiceGroup = "")
{
    public bool IsChoice => !string.IsNullOrWhiteSpace(ChoiceGroup);
}

public sealed record MenuComponentSnapshot(
    long ProductId,
    string Name,
    decimal Quantity,
    long MarketPriceCents,
    decimal VatRate,
    bool ImHausApplicable,
    string ChoiceGroup = "");

public sealed record ExtraItem(
    long Id,
    long CategoryId,
    string Name,
    long PriceCents,
    decimal VatRate,
    int SortOrder = 0,
    bool IsActive = true);

public enum PromotionScope
{
    All,
    Category,
    Product
}

public sealed record PromotionCampaign(
    long Id,
    string Name,
    int DiscountPercent,
    DateOnly StartDate,
    DateOnly EndDate,
    PromotionScope Scope,
    long TargetId,
    string TargetName,
    bool IsEnabled,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string StatusFor(DateOnly day)
    {
        if (!IsEnabled)
            return "DEAKTIVIERT";

        if (day < StartDate)
            return "GEPLANT";

        if (day > EndDate)
            return "ABGELAUFEN";

        return "AKTIV";
    }
}

public sealed record PromotionSnapshot(
    long PromotionId,
    string Name,
    int DiscountPercent,
    string StartDate,
    string EndDate,
    PromotionScope Scope,
    long TargetId);

public sealed class Product
{
    public long Id { get; init; }
    public long CategoryId { get; set; }
    public string Name { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Barcode { get; set; } = "";
    public long BasePriceCents { get; set; }
    public decimal VatRate { get; set; } = 19m;
    // R97: mirrors Category.ImHausApplicable - always kept in sync with
    // the product's Warengruppe (same as VatRate itself, "Warengruppe is
    // the VAT master").
    public bool ImHausApplicable { get; set; } = true;
    public long PfandCents { get; set; }
    public string Unit { get; set; } = "Stück";

    // R170: weighted articles are normalized to kg. The cashier may enter
    // grams or kilograms, but stock, returns and line quantity all use kg.
    // "kg" is deliberately the explicit opt-in so legacy articles using
    // other units never become weighed articles by accident.
    public bool IsWeighted =>
        string.Equals(Unit, "kg", StringComparison.OrdinalIgnoreCase);

    public string ImagePath { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public decimal StockQuantity { get; set; }
    public decimal MinStockQuantity { get; set; }
    public long PurchasePriceCents { get; set; }
    public IReadOnlyList<ProductVariant> Variants { get; set; } = Array.Empty<ProductVariant>();
    public IReadOnlyList<ProductComboItem> ComboItems { get; set; } = Array.Empty<ProductComboItem>();

    public bool IsCombo => ComboItems.Count > 0;

    public long EffectivePriceCents(ProductVariant? variant) =>
        (variant?.PriceCents ?? BasePriceCents) + PfandCents;
}

public sealed class CartLine
{
    public CartLine()
    {
    }

    /// <summary>
    /// Snapshot copy constructor. CartLine grows over time; copying through one
    /// constructor prevents new fiscal fields (for example Unit) from being
    /// silently lost in menu allocation / Bestellung delta paths.
    /// </summary>
    public CartLine(CartLine source)
    {
        ArgumentNullException.ThrowIfNull(source);

        SaleItemId = source.SaleItemId;
        ProductId = source.ProductId;
        ProductName = source.ProductName;
        VariantName = source.VariantName;
        Barcode = source.Barcode;
        Quantity = source.Quantity;
        Unit = source.Unit;
        UnitPriceCents = source.UnitPriceCents;
        ListUnitPriceCents = source.ListUnitPriceCents;
        VatRate = source.VatRate;
        VatAllocations = source.VatAllocations.ToArray();
        MenuComponents = source.MenuComponents.ToArray();
        ImHausApplicable = source.ImHausApplicable;
        PfandCents = source.PfandCents;
        PromotionId = source.PromotionId;
        PromotionName = source.PromotionName;
        PromotionPercent = source.PromotionPercent;
        PromotionDiscountUnitCents = source.PromotionDiscountUnitCents;
        PromotionStartDate = source.PromotionStartDate;
        PromotionEndDate = source.PromotionEndDate;
        PersistedLineTotalCents = source.PersistedLineTotalCents;
    }

    /// <summary>sale_items.id once persisted; 0 for a line still only in the cart. Used to target a specific line for partial Retoure.</summary>
    public long SaleItemId { get; init; }
    public long ProductId { get; init; }
    public string ProductName { get; init; } = "";
    public string VariantName { get; init; } = "";
    public string Barcode { get; init; } = "";
    public decimal Quantity { get; set; } = 1m;

    // R170: commercial unit snapshot used by cashier/receipt formatting.
    // For weighed sales this is kg and Quantity is the exact sold kg amount.
    public string Unit { get; init; } = "Stück";
    public bool IsWeighted =>
        string.Equals(Unit, "kg", StringComparison.OrdinalIgnoreCase);

    // Actual unit price after Angebot, including Pfand where applicable.
    public long UnitPriceCents { get; init; }

    // Immutable price snapshot before Angebot. For legacy rows 0 means
    // "same as UnitPriceCents + PromotionDiscountUnitCents".
    public long ListUnitPriceCents { get; init; }

    public decimal VatRate { get; init; }

    // R151: A menu/combo remains ONE commercial receipt line, but can carry
    // multiple hidden VAT buckets. GrossCents is the allocation for ONE sold
    // menu unit. Normal articles leave this empty and continue to use VatRate.
    // The customer-facing receipt never expands these allocations into
    // component lines.
    public MenuVatAllocation[] VatAllocations { get; init; } = Array.Empty<MenuVatAllocation>();

    // R153: exact menu ingredients selected for THIS cart/sale line. Fixed
    // components and cashier choices are snapshotted here so stock reversal,
    // VAT allocation and historical replay never depend on a later recipe edit.
    // Customer receipts deliberately do not render these component names.
    public MenuComponentSnapshot[] MenuComponents { get; init; } = Array.Empty<MenuComponentSnapshot>();

    public bool HasVatAllocations => VatAllocations.Length > 0;
    public bool HasMenuComponents => MenuComponents.Length > 0;

    // R97: mirrors Product.ImHausApplicable at the moment this line was
    // added to the cart.
    public bool ImHausApplicable { get; init; } = true;
    public long PfandCents { get; init; }

    public long PromotionId { get; init; }
    public string PromotionName { get; init; } = "";
    public int PromotionPercent { get; init; }
    public long PromotionDiscountUnitCents { get; init; }
    public string PromotionStartDate { get; init; } = "";
    public string PromotionEndDate { get; init; } = "";

    public bool HasPromotion =>
        PromotionId > 0 &&
        PromotionPercent > 0 &&
        (PromotionDiscountUnitCents > 0 || IsWeighted);

    public long EffectiveListUnitPriceCents =>
        ListUnitPriceCents > 0
            ? ListUnitPriceCents
            : UnitPriceCents + PromotionDiscountUnitCents;

    public long ListLineTotalCentsFor(decimal quantity) =>
        (long)Math.Round(
            quantity * EffectiveListUnitPriceCents,
            MidpointRounding.AwayFromZero);

    public long PromotionDiscountCentsFor(decimal quantity)
    {
        if (!HasPromotion || quantity == 0m)
            return 0L;

        if (!IsWeighted)
        {
            return (long)Math.Round(
                quantity * PromotionDiscountUnitCents,
                MidpointRounding.AwayFromZero);
        }

        // R174: weighted sales need line-level allocation. A per-kg discount
        // stored as whole cents can create half-cent intermediate values
        // (e.g. 0.500 kg × 19.90 EUR/kg × 10%). Calculate the promotion from
        // the immutable list price + percentage and round only once at line
        // level. Negative quantities are fiscal reversal/delta lines and must
        // mirror the positive amount exactly instead of collapsing to zero.
        var sign = quantity < 0m ? -1L : 1L;
        var absoluteQuantity = Math.Abs(quantity);
        var merchandiseUnitCents =
            Math.Max(0L, EffectiveListUnitPriceCents - PfandCents);
        var discount =
            (long)Math.Round(
                absoluteQuantity * merchandiseUnitCents *
                (Math.Clamp(PromotionPercent, 0, 100) / 100m),
                MidpointRounding.AwayFromZero);
        var listTotal =
            (long)Math.Round(
                absoluteQuantity * EffectiveListUnitPriceCents,
                MidpointRounding.AwayFromZero);

        return sign * Math.Clamp(
            discount,
            0L,
            Math.Max(0L, listTotal));
    }

    public long LineTotalCentsFor(decimal quantity)
    {
        if (quantity == 0m)
            return 0L;

        if (IsWeighted && HasPromotion)
        {
            var total =
                ListLineTotalCentsFor(quantity) -
                PromotionDiscountCentsFor(quantity);

            return quantity < 0m
                ? Math.Min(0L, total)
                : Math.Max(0L, total);
        }

        return (long)Math.Round(
            quantity * UnitPriceCents,
            MidpointRounding.AwayFromZero);
    }

    // R176: partial returns must allocate from the cumulative original line,
    // not round every fragment independently. Otherwise 0.333 kg + 0.333 kg +
    // 0.334 kg of one promoted line can differ by a cent from returning 1.000 kg.
    // The slice is F(already + quantity) - F(already), so all slices telescope
    // exactly to the original line totals and the last return absorbs remainder cents.
    public long LineTotalCentsSlice(decimal alreadyReturned, decimal quantity) =>
        LineTotalCentsFor(alreadyReturned + quantity) -
        LineTotalCentsFor(alreadyReturned);

    public long ListLineTotalCentsSlice(decimal alreadyReturned, decimal quantity) =>
        ListLineTotalCentsFor(alreadyReturned + quantity) -
        ListLineTotalCentsFor(alreadyReturned);

    public long PromotionDiscountCentsSlice(decimal alreadyReturned, decimal quantity) =>
        PromotionDiscountCentsFor(alreadyReturned + quantity) -
        PromotionDiscountCentsFor(alreadyReturned);

    /// <summary>
    /// Immutable gross total read from a persisted fiscal/order line.
    /// Null for a live cart line. Reloaded records use the stored cents as the
    /// source of truth instead of recomputing quantity × current formula.
    /// Quantity-changing copies must clear this value.
    /// </summary>
    public long? PersistedLineTotalCents { get; init; }

    public long LineTotalCents =>
        PersistedLineTotalCents ?? LineTotalCentsFor(Quantity);

    public long ListLineTotalCents =>
        ListLineTotalCentsFor(Quantity);

    public long PromotionDiscountCents =>
        PromotionDiscountCentsFor(Quantity);
}

/// <summary>
/// German Im-Haus/Außer-Haus VAT rule for food service (§12 UStG): food
/// consumed on the premises is a "Bewirtungsleistung" taxed at the
/// standard rate, while the same item taken away is a reduced-rate goods
/// sale. Drinks are already standard-rate regardless of location and are
/// deliberately left untouched here. The menu/gross price a customer pays
/// never changes - only the internal net/VAT split does (CartLine.LineTotalCents
/// depends solely on Quantity*UnitPriceCents, never on VatRate).
/// </summary>
public static class ImHausVat
{
    public const decimal ReducedRate = 7m;
    public const decimal StandardRate = 19m;

    // R97: categoryApplies lets an admin opt a specific Warengruppe out of
    // this rule entirely (Category.ImHausApplicable/CartLine.ImHausApplicable).
    // Defaults to true so existing callers keep the original R95 behavior.
    public static decimal Effective(decimal baseRate, bool imHaus, bool categoryApplies = true) =>
        imHaus && categoryApplies && baseRate == ReducedRate ? StandardRate : baseRate;
}

// R101: Mixed means the sale's total was split across cash and a card
// terminal charge in one checkout. The actual split lives on
// CheckoutSnapshot.CashPortionCents/Sale.CashPortionCents+CardPortionCents,
// not on this enum value alone.
public enum PaymentMethod { Cash, Card, Mixed }

public sealed class Sale
{
    public long Id { get; set; }
    public long ReceiptNumber { get; set; }
    /// <summary>IMBISS Abhol-/Bestellnummer. Rein operativ; niemals Ersatz für die fiskale Bonnummer.</summary>
    public long PickupNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public PaymentMethod PaymentMethod { get; set; }
    // R101: always populated for every sale (not just Mixed) so reports can
    // sum these two columns directly instead of branching on PaymentMethod -
    // Cash=(Total,0), Card=(0,Total), Mixed=(X,Total-X).
    public long CashPortionCents { get; set; }
    public long CardPortionCents { get; set; }

    // R102: a historical pre-R101 sale reads 0/0 for the two properties
    // above (sales is append-only, so it can never be backfilled - see
    // EnsureColumnAsync's comment in Infrastructure.cs). These derive the
    // correct split from PaymentMethod+TotalCents in that case, exactly
    // like CheckoutSnapshot's Effective*PortionCents - always use these
    // instead of the raw properties whenever the split is safety-relevant
    // (e.g. deciding how much of a Storno/Retoure needs a card refund).
    public long EffectiveCashPortionCents => CashPortionCents != 0 || CardPortionCents != 0
        ? CashPortionCents
        : PaymentMethod == PaymentMethod.Cash ? TotalCents : 0;
    public long EffectiveCardPortionCents => TotalCents - EffectiveCashPortionCents;
    // Manual cashier discount only. Angebot discounts live on sale lines.
    public long DiscountCents { get; set; }
    public long ListSubtotalCents { get; set; }
    public long PromotionDiscountCents { get; set; }
    public long TotalCents { get; set; }
    public string TransactionType { get; set; } = "SALE";

    /// <summary>R133: Im Haus (true) / Außer Haus (false); null for sales from before R133.</summary>
    public bool? ImHaus { get; set; }
    public long? OriginalSaleId { get; set; }
    /// <summary>Receipt number of the original sale a STORNO/RETURN references, if any. Never set for TransactionType="SALE".</summary>
    public long? OriginalReceiptNumber { get; set; }
    public IReadOnlyList<CartLine> Lines { get; set; } = Array.Empty<CartLine>();
    public string FiscalStatus { get; set; } = "TEST_TSE_NOT_CONNECTED";
    public string OperatorName { get; set; } = "";

    // R78: TSE-Beleg-Signatur (§6 KassenSichV). Leer/TseOutage=true, solange
    // kein TSE aktiv ist oder die Signierung fehlgeschlagen ist - eine
    // TSE-Störung allein darf einen bereits abgeschlossenen Verkauf nie
    // rückgängig machen, siehe TseFailSafeService.
    public string TseClientId { get; set; } = "";
    public string TseTransactionNumber { get; set; } = "";
    public string TseSignatureCounter { get; set; } = "";
    public string TseSerialNumber { get; set; } = "";
    public string TseSignature { get; set; } = "";
    public DateTimeOffset? TseLogTime { get; set; }
    public bool TseOutage { get; set; }

    /// <summary>R136: when the Vorgang began at the till (first position); null for sales from before R136.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>R136: TSE log time of StartTransaction; null before R136 or during an outage.</summary>
    public DateTimeOffset? TseStartLogTime { get; set; }

    /// <summary>
    /// R137: for a sale that paid a secured order, the start of the order's
    /// first Bestellung transaction. DSFinV-K 2.7.2 lets the Kassenbeleg start
    /// with the payment only if this time is also printed on the receipt.
    /// </summary>
    public DateTimeOffset? OrderStartedAt { get; set; }

    /// <summary>R143: positions cancelled during capture, before payment (DSFinV-K 4.2.3).</summary>
    public IReadOnlyList<CartLine> CancelledLines { get; set; } = Array.Empty<CartLine>();
}

/// <summary>
/// Result of attempting to fiscally sign one completed sale as a TSE
/// "Beleg" transaction. <see cref="Signed"/> false means TSE-Ausfall:
/// the sale itself already exists and stays valid, but was not TSE-signed.
/// </summary>
public sealed record SaleTseResult(
    bool Signed,
    string ClientId,
    string TransactionNumber,
    string SignatureCounter,
    string SerialNumber,
    string Signature,
    DateTimeOffset? LogTime,
    string OutageMessage,
    DateTimeOffset? StartLogTime = null)
{
    /// <summary>R136: <paramref name="startLogTime"/> is the TSE log time of StartTransaction (TSE_TA_START, Vorgangsbeginn on the receipt).</summary>
    public static SaleTseResult SignedResult(
        string clientId,
        string transactionNumber,
        string signatureCounter,
        string serialNumber,
        string signature,
        DateTimeOffset? logTime,
        DateTimeOffset? startLogTime = null) =>
        new(true, clientId, transactionNumber, signatureCounter, serialNumber, signature, logTime, "", startLogTime);

    /// <summary>
    /// KassenSichV §2 fail-closed factory for a TSE operation that reported
    /// success. A transport/API success is not treated as a complete fiscal
    /// signature when mandatory TSE-returned fields are missing or malformed.
    /// </summary>
    public static SaleTseResult FromSuccessfulTse(
        string clientId,
        string transactionNumber,
        string signatureCounter,
        string serialNumber,
        string signature,
        DateTimeOffset? logTime,
        DateTimeOffset? startLogTime = null)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(clientId)) missing.Add("Client-ID");
        if (!ulong.TryParse(transactionNumber, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
            missing.Add("Transaktionsnummer");
        if (!ulong.TryParse(signatureCounter, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
            missing.Add("Signaturzähler");
        if (string.IsNullOrWhiteSpace(serialNumber)) missing.Add("TSE-Seriennummer");
        if (string.IsNullOrWhiteSpace(signature)) missing.Add("Prüfwert/Signatur");
        if (startLogTime is null) missing.Add("Vorgangsbeginn");
        if (logTime is null) missing.Add("Vorgangsende");

        return missing.Count == 0
            ? SignedResult(clientId.Trim(), transactionNumber, signatureCounter, serialNumber.Trim(), signature, logTime, startLogTime)
            : Outage("Unvollständiges TSE-Ergebnis trotz Success: " + string.Join(", ", missing));
    }

    public static SaleTseResult Outage(string message) =>
        new(false, "", "", "", "", "", null, message);
}


public sealed class ParkedReceipt
{
    public string OrderNote { get; set; } = "";
    public long Id { get; set; }
    public long ParkNumber { get; set; }
    /// <summary>Optionale IMBISS-Abholnummer, die bereits bei Bestellannahme vergeben werden kann.</summary>
    public long PickupNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public long DiscountCents { get; set; }
    public long TotalCents { get; set; }
    public IReadOnlyList<CartLine> Lines { get; set; } = Array.Empty<CartLine>();

    // R95 follow-up: the Im-Haus/Außer-Haus choice made before parking, so
    // reopening this order later (for editing or for payment) can restore
    // the same choice instead of silently reverting to Außer Haus.
    public bool ImHaus { get; set; }

    public string DisplayNumber => $"P{ParkNumber:000000}";
    public decimal QuantityTotal => Lines.Sum(x => x.Quantity);

    // R83: TSE-Signatur der Bestellannahme (eigener "Bestellung-V1"-TSE-
    // Vorgang, getrennt vom späteren Kassenbeleg-V1 bei Zahlung). Wie bei
    // Sale: TseOutage=true bedeutet nur, dass die Signierung fehlgeschlagen
    // ist - die Bestellung bleibt trotzdem gültig angenommen.
    public string TseClientId { get; set; } = "";
    public string TseTransactionNumber { get; set; } = "";
    public string TseSignatureCounter { get; set; } = "";
    public string TseSerialNumber { get; set; } = "";
    public string TseSignature { get; set; } = "";
    public DateTimeOffset? TseLogTime { get; set; }
    public bool TseOutage { get; set; }

    /// <summary>R136: start of the Vorgang that became this order, and the TSE start log time.</summary>
    public DateTimeOffset? VorgangStartedAt { get; set; }
    public DateTimeOffset? TseStartLogTime { get; set; }
}

public sealed record DailyCloseCheck(
    bool Allowed,
    int OpenParkedReceipts,
    string Message);
