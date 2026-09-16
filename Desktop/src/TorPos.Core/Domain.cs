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
    int SortOrder = 0);

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
    /// <summary>sale_items.id once persisted; 0 for a line still only in the cart. Used to target a specific line for partial Retoure.</summary>
    public long SaleItemId { get; init; }
    public long ProductId { get; init; }
    public string ProductName { get; init; } = "";
    public string VariantName { get; init; } = "";
    public string Barcode { get; init; } = "";
    public decimal Quantity { get; set; } = 1m;

    // Actual unit price after Angebot, including Pfand where applicable.
    public long UnitPriceCents { get; init; }

    // Immutable price snapshot before Angebot. For legacy rows 0 means
    // "same as UnitPriceCents + PromotionDiscountUnitCents".
    public long ListUnitPriceCents { get; init; }

    public decimal VatRate { get; init; }
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
        PromotionDiscountUnitCents > 0;

    public long EffectiveListUnitPriceCents =>
        ListUnitPriceCents > 0
            ? ListUnitPriceCents
            : UnitPriceCents + PromotionDiscountUnitCents;

    public long LineTotalCents =>
        (long)Math.Round(
            Quantity * UnitPriceCents,
            MidpointRounding.AwayFromZero);

    public long ListLineTotalCents =>
        (long)Math.Round(
            Quantity * EffectiveListUnitPriceCents,
            MidpointRounding.AwayFromZero);

    public long PromotionDiscountCents =>
        (long)Math.Round(
            Quantity * PromotionDiscountUnitCents,
            MidpointRounding.AwayFromZero);
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
    string OutageMessage)
{
    public static SaleTseResult SignedResult(
        string clientId,
        string transactionNumber,
        string signatureCounter,
        string serialNumber,
        string signature,
        DateTimeOffset? logTime) =>
        new(true, clientId, transactionNumber, signatureCounter, serialNumber, signature, logTime, "");

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
}

public sealed record DailyCloseCheck(
    bool Allowed,
    int OpenParkedReceipts,
    string Message);
