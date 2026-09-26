using System.Collections.Concurrent;
using System.Diagnostics;

namespace TorPos.Core;

public interface IProductRepository
{
    Task<IReadOnlyList<ProductGroup>> GetGroupsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default);

    Task<long> SaveGroupAsync(
        ProductGroup group,
        CancellationToken ct = default);

    Task<long> SaveCategoryAsync(
        Category category,
        CancellationToken ct = default);

    // R64 FIX1: Reihenfolge atomar aktualisieren, ohne Name/MwSt./Farbe
    // oder andere Warengruppen-Stammdaten erneut zu speichern.
    Task ReorderCategoriesAsync(
        IReadOnlyList<long> orderedCategoryIds,
        CancellationToken ct = default);

    Task DeactivateGroupAsync(long groupId, string actor, CancellationToken ct = default);
    Task DeactivateCategoryAsync(long categoryId, string actor, CancellationToken ct = default);
    Task DeactivateProductAsync(long productId, string actor, CancellationToken ct = default);

    Task<IReadOnlyList<Product>> GetActiveProductsAsync(CancellationToken ct = default);
    Task<Product?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<long> SaveAsync(Product product, CancellationToken ct = default);
    Task<long> SaveWithStockAsync(Product product, decimal? count, decimal expected, string actor, CancellationToken ct = default, IReadOnlyList<ProductVariant>? variants = null, IReadOnlyList<ProductComboItem>? comboItems = null);
    Task ReplaceVariantsAsync(long productId, IReadOnlyList<ProductVariant> variants, CancellationToken ct = default);
    Task ReplaceComboItemsAsync(long productId, IReadOnlyList<ProductComboItem> items, CancellationToken ct = default);
    Task<IReadOnlyList<ProductComboItem>> GetComboItemsAsync(long productId, CancellationToken ct = default);
    Task<IReadOnlyList<ExtraItem>> GetExtrasAsync(CancellationToken ct = default);
    Task<long> SaveExtraAsync(ExtraItem extra, CancellationToken ct = default);
}

public interface IProductCatalog
{
    IReadOnlyList<ProductGroup> Groups { get; }
    IReadOnlyList<Category> Categories { get; }
    IReadOnlyList<Product> Products { get; }
    ValueTask ReloadAsync(CancellationToken ct = default);
    bool TryGetByBarcode(string barcode, out Product? product);
    IReadOnlyList<Product> GetByCategory(long categoryId);
}

public interface ISaleRepository
{
    Task<Sale> CommitAsync(CheckoutSnapshot snapshot, CancellationToken ct = default);

    // O-8: the start of the open Z period (the last Tagesabschluss), or null
    // before the first one. Storno/Teilretoure are allowed for Bons after it.
    Task<DateTimeOffset?> GetOpenZPeriodStartAsync(CancellationToken ct = default) =>
        Task.FromResult<DateTimeOffset?>(null);

    Task<Sale?> GetLastAsync(
        CancellationToken ct = default);

    Task<Sale?> GetByIdAsync(
        long id,
        CancellationToken ct = default);

    Task<IReadOnlyList<Sale>> SearchHistoryAsync(DateOnly from, DateOnly to,
        long? receiptNumber = null, PaymentMethod? method = null, CancellationToken ct = default);

    // R122 (audit finding İ6): GetDailySinceLastZAsync was removed here. It was
    // declared and implemented but never called from anywhere, and it carried
    // the very bug R117 had to fix in GetOpenPeriodAsync - it started the
    // period at max(midnight, last closing), so a shift running past midnight
    // would have lost the sales between the last closing and 00:00. Leaving a
    // dead method with a known-wrong period rule in an interface is an
    // invitation for a future caller to pick it up. The correct "since the last
    // closing" logic lives in BusinessManagementService.GetOpenPeriodAsync.

    Task RecordDailyClosingAsync(
        string operatorName,
        CancellationToken ct = default);

    // R78: persists the outcome of fiscally signing an already-committed
    // sale as a TSE Beleg (or the fact that TSE signing failed/outage).
    // Never used to create or roll back a sale - only to attach its
    // fiscal signature after the fact.
    Task RecordTseResultAsync(
        long saleId,
        SaleTseResult result,
        CancellationToken ct = default);

    // R79: books a full BON STORNO as its own new, immutable sale row
    // (transaction_type='STORNO', original_sale_id set) - never updates or
    // deletes the original sale. Throws if the original sale is not
    // eligible (already a STORNO, already storno'd) or if fiscal
    // production booking is not released, exactly like
    // ISaleRepository.CommitAsync. R102: a card-paid (or Mixed) original's
    // card portion needs its terminal refund CONFIRMED BEFORE this is
    // called - see CheckoutApplicationService.RefundStornoCardPortionAsync
    // - and cardRefundEvidence must carry non-empty confirmation details
    // (e.g. the terminal's outcome/trace) whenever the original has a
    // nonzero CardPortionCents, or this throws rather than silently
    // recording a Storno with no matching money movement.
    Task<Sale> RecordStornoAsync(
        long originalSaleId,
        string actor,
        string reason,
        string cardRefundEvidence = "",
        CancellationToken ct = default);

    // R82: partial Retoure - returns specific quantities of specific lines
    // from an already-completed sale, as its own new immutable sale
    // (transaction_type='RETURN'). Same rules as RecordStornoAsync: never
    // touches the original sale, gated by FiscalRelease.RequireProduction(),
    // and (R102) requires cardRefundEvidence whenever the reversed lines'
    // own card portion is nonzero. (R106) The original sale's whole-Bon
    // manual discount IS prorated onto the returned lines' own share of
    // the original subtotal - crediting the full undiscounted price would
    // refund more than the customer actually paid for that item.
    Task<Sale> RecordReturnAsync(
        long originalSaleId,
        IReadOnlyList<ReturnLineRequest> lines,
        string actor,
        string reason,
        string cardRefundEvidence = "",
        CancellationToken ct = default);

    // R176: quote and commit use the same cumulative line-allocation rule.
    // This is critical before a card refund is sent: repeatedly returning
    // fractions of one weighted/promotion line must refund exactly the cents
    // that RecordReturnAsync will persist, including the final remainder cent.
    Task<ReturnQuote> QuoteReturnAsync(
        long originalSaleId,
        IReadOnlyList<ReturnLineRequest> lines,
        CancellationToken ct = default);

    // R107: MUST be checked BEFORE ever calling the card terminal for a
    // refund - not just relied upon as RecordStornoAsync/RecordReturnAsync's
    // own internal (authoritative, transactional) gate. Without this
    // pre-check, a cashier retrying BON STORNO on an already-fully-storno'd
    // sale (or Teilretoure on an already-storno'd one) would have the
    // terminal charge/refund a SECOND time before the DB ever rejects the
    // write - a real, confirmed phantom-refund risk found by the user's
    // own source review. Returns null when the reversal may proceed, or a
    // human-readable reason it may not. forFullStorno=true also blocks
    // when ANY prior Teilretoure already exists against this original
    // (ambiguous how much would be left to storno - RecordStornoAsync
    // enforces this same rule); forFullStorno=false blocks only when the
    // original was already fully storno'd (multiple Teilretouren against
    // the same original are normal and unaffected).
    Task<string?> CheckReversalAllowedAsync(
        long originalSaleId,
        bool forFullStorno,
        CancellationToken ct = default);
}

/// <summary>One requested return line: a specific original sale_items row and how much of it to return (must be > 0 and not exceed what that line has left after any earlier partial returns).</summary>
public sealed record ReturnLineRequest(long SaleItemId, decimal Quantity);

public sealed record ReturnQuote(
    long RawTotalCents,
    long DiscountCents,
    long TotalCents,
    long CashPortionCents,
    long CardPortionCents);

/// <summary>A card refund attempt (BON STORNO/Teilretoure) whose terminal outcome came back Unknown/ambiguous and has not yet been manually resolved.</summary>
public sealed record CardRefundAttempt(
    string Id,
    long OriginalSaleId,
    long OriginalReceiptNumber,
    string Kind,
    long AmountCents,
    DateTimeOffset CreatedAt);

// R106: durable lock against a duplicate card refund. Mirrors
// checkout_operations'/ICheckoutJournal's "durable, must-be-reconciled"
// property for the reverse (refund) direction: once a refund attempt for
// a given original sale comes back Unknown, no further attempt against
// that SAME original sale may proceed until an admin explicitly resolves
// it here (after manually checking with the terminal/bank whether the
// refund actually went through). A Cash-only Storno/Retoure never touches
// this at all - there is no terminal call to go ambiguous.
public interface ICardRefundLockRepository
{
    Task<bool> HasUnresolvedAsync(long originalSaleId, CancellationToken ct = default);

    /// <summary>Records a new attempt as unresolved BEFORE the terminal call - pessimistic by design, so a crash between this call and the terminal's response still leaves the sale locked.</summary>
    Task<string> BeginAsync(long originalSaleId, string kind, long amountCents, CancellationToken ct = default);

    /// <summary>Call only for a DEFINITE outcome (Approved/Declined/Cancelled/NotSent) - removes the lock. Never call this for Unknown; leaving the row in place IS the lock.</summary>
    Task ClearAsync(string attemptId, CancellationToken ct = default);

    Task<IReadOnlyList<CardRefundAttempt>> GetUnresolvedAsync(CancellationToken ct = default);

    /// <summary>Admin action: marks an unresolved attempt resolved after manually verifying the real-world outcome with the terminal/bank. Does not itself touch any sale row.</summary>
    Task ResolveAsync(string attemptId, string actor, string note, CancellationToken ct = default);
}


public interface IParkedReceiptRepository
{
    Task<ParkedReceipt> ParkAsync(
        IReadOnlyList<CartLine> lines,
        long discountCents,
        string createdBy,
        bool assignPickupNumber = false,
        CancellationToken ct = default,
        bool training = false, bool orderPrint = false, bool imHaus = false);

    Task UpdateAsync(
        long parkedReceiptId,
        IReadOnlyList<CartLine> lines,
        long discountCents,
        CancellationToken ct = default, bool orderPrint = false, string actor = "SYSTEM", bool imHaus = false);

    Task<IReadOnlyList<ParkedReceipt>> GetOpenAsync(
        CancellationToken ct = default,
        bool training = false);

    Task<int> GetOpenCountAsync(
        CancellationToken ct = default,
        bool training = false);

    Task<ParkedReceipt?> GetOpenByIdAsync(
        long id,
        CancellationToken ct = default,
        bool training = false);

    Task MarkCashedAsync(
        long id,
        long saleId,
        CancellationToken ct = default);

    Task CancelAsync(
        long id,
        CancellationToken ct = default, bool orderPrint = false, string actor = "SYSTEM");

    // R83: persists the outcome of fiscally signing an IMBISS ORDER's
    // acceptance as its own "Bestellung-V1" TSE Vorgang, separate from the
    // eventual Kassenbeleg-V1 at payment (see SaleFiscalSigningService for
    // that one). Unlike sales, parked_receipts has no immutability trigger,
    // so this is a plain UPDATE - not an append-only insert like
    // ISaleRepository.RecordTseResultAsync.
    Task RecordTseResultAsync(
        long parkedReceiptId,
        SaleTseResult result,
        CancellationToken ct = default);
}

public interface IDailyClosingGuard
{
    Task<DailyCloseCheck> CheckAsync(
        CancellationToken ct = default);
}

public sealed class SaleEngine
{
    private readonly List<CartLine> _cart = new();
    public bool IsReadOnly { get; set; }
    public IReadOnlyList<CartLine> Cart => _cart;
    public long DiscountCents { get; private set; }
    public long ListSubtotalCents => _cart.Sum(x => x.ListLineTotalCents);
    public long PromotionDiscountCents => _cart.Sum(x => x.PromotionDiscountCents);
    public long SubtotalCents => _cart.Sum(x => x.LineTotalCents);
    public long TotalCents => ReceiptTotals.Total(SubtotalCents, DiscountCents);

    /// <summary>R149: the cart holds returned deposit (Leergut).</summary>
    public bool HasDepositReturns => _cart.Any(PfandProducts.IsDepositReturn);

    /// <summary>
    /// R149: returned empties - a position with a negative amount at the rate of
    /// the deposit (PfandProducts.RateFor). Not combined with a manual discount.
    /// </summary>
    public bool AddDepositReturn(long productId, string name, long depositCents, decimal vatRate, decimal quantity = 1m)
    {
        if (IsReadOnly || quantity <= 0m || depositCents <= 0 || DiscountCents > 0 || !PfandProducts.IsDeposit(productId))
            return false;

        var price = -depositCents;
        var existing = _cart.FirstOrDefault(x => x.ProductId == productId && x.UnitPriceCents == price && x.VatRate == vatRate);
        if (existing is not null)
        {
            existing.Quantity += quantity;
            return true;
        }

        _cart.Add(new CartLine
        {
            ProductId = productId,
            ProductName = name,
            Quantity = quantity,
            Unit = "Stück",
            UnitPriceCents = price,
            ListUnitPriceCents = price,
            VatRate = vatRate,
            // The rate of returned deposit follows the goods, not Im Haus.
            ImHausApplicable = false,
        });
        return true;
    }

    public void Add(
        Product product,
        ProductVariant? variant = null,
        decimal quantity = 1m,
        PromotionSnapshot? promotion = null,
        IReadOnlyList<MenuComponentSnapshot>? menuComponents = null,
        long? unitPriceOverrideCents = null)
    {
        if (IsReadOnly || quantity <= 0m)
            return;

        if (product.IsWeighted && (variant is not null || product.IsCombo))
            throw new InvalidOperationException("Gewichtsartikel unterstützen keine Varianten oder Menüs.");

        var merchandisePrice =
            unitPriceOverrideCents ??
            variant?.PriceCents ??
            product.BasePriceCents;

        if (merchandisePrice < 0)
            throw new InvalidOperationException("Verkaufspreis darf nicht negativ sein.");
        var listPrice = merchandisePrice + product.PfandCents;
        var promotionDiscountUnit = 0L;

        if (promotion is not null)
        {
            var percent = Math.Clamp(promotion.DiscountPercent, 0, 100);

            // R71: Pfand is explicitly excluded from Angebot.
            promotionDiscountUnit =
                (long)Math.Round(
                    merchandisePrice * (percent / 100m),
                    MidpointRounding.AwayFromZero);

            promotionDiscountUnit =
                Math.Clamp(
                    promotionDiscountUnit,
                    0L,
                    Math.Max(0L, merchandisePrice));
        }

        var actualPrice = Math.Max(
            product.PfandCents,
            listPrice - promotionDiscountUnit);

        var variantName = variant?.Name ?? "";
        var promotionId = promotion?.PromotionId ?? 0;

        var componentSnapshot = (menuComponents ?? Array.Empty<MenuComponentSnapshot>()).ToArray();

        var existing = _cart.FirstOrDefault(x =>
            x.ProductId == product.Id &&
            x.VariantName == variantName &&
            x.UnitPriceCents == actualPrice &&
            x.ListUnitPriceCents == listPrice &&
            x.PromotionId == promotionId &&
            SameMenuComponents(x.MenuComponents, componentSnapshot));

        if (existing is not null)
        {
            existing.Quantity += quantity;
            return;
        }

        _cart.Add(new CartLine
        {
            ProductId = product.Id,
            ProductName = product.Name,
            VariantName = variantName,
            Barcode = product.Barcode,
            Quantity = quantity,
            Unit = product.IsWeighted ? "kg" : (string.IsNullOrWhiteSpace(product.Unit) ? "Stück" : product.Unit),
            UnitPriceCents = actualPrice,
            ListUnitPriceCents = listPrice,
            VatRate = product.VatRate,
            MenuComponents = componentSnapshot,
            ImHausApplicable = product.ImHausApplicable,
            PfandCents = product.PfandCents,
            PromotionId = promotion?.PromotionId ?? 0,
            PromotionName = promotion?.Name ?? "",
            PromotionPercent = promotion?.DiscountPercent ?? 0,
            PromotionDiscountUnitCents = promotionDiscountUnit,
            PromotionStartDate = promotion?.StartDate ?? "",
            PromotionEndDate = promotion?.EndDate ?? ""
        });
    }

    public void ChangeQuantity(int index, decimal delta)
    {
        if (IsReadOnly) return;
        if (index < 0 || index >= _cart.Count) return;
        _cart[index].Quantity += delta;
        if (_cart[index].Quantity <= 0m) _cart.RemoveAt(index);
    }

    public void SetQuantity(int index, decimal quantity)
    {
        if (IsReadOnly) return;
        if (index < 0 || index >= _cart.Count)
            return;

        if (quantity <= 0m)
        {
            _cart.RemoveAt(index);
            return;
        }

        _cart[index].Quantity = quantity;
    }

    public void RemoveAt(int index)
    {
        if (IsReadOnly) return;
        if (index >= 0 && index < _cart.Count) _cart.RemoveAt(index);
    }

    // R149: no manual discount on a receipt with returned deposit.
    public void SetDiscount(long cents) { if (!IsReadOnly && !(cents > 0 && HasDepositReturns)) DiscountCents = Math.Max(0, cents); }

    public void Restore(IReadOnlyList<CartLine> lines, long discountCents)
    {
        if (IsReadOnly) return;
        _cart.Clear();

        foreach (var line in lines)
        {
            _cart.Add(new CartLine
            {
                ProductId = line.ProductId,
                ProductName = line.ProductName,
                VariantName = line.VariantName,
                Barcode = line.Barcode,
                Quantity = line.Quantity,
                Unit = line.Unit,
                UnitPriceCents = line.UnitPriceCents,
                ListUnitPriceCents = line.EffectiveListUnitPriceCents,
                VatRate = line.VatRate,
                VatAllocations = line.VatAllocations.ToArray(),
                MenuComponents = line.MenuComponents.ToArray(),
                ImHausApplicable = line.ImHausApplicable,
                PfandCents = line.PfandCents,
                PromotionId = line.PromotionId,
                PromotionName = line.PromotionName,
                PromotionPercent = line.PromotionPercent,
                PromotionDiscountUnitCents = line.PromotionDiscountUnitCents,
                PromotionStartDate = line.PromotionStartDate,
                PromotionEndDate = line.PromotionEndDate
            });
        }

        DiscountCents = Math.Max(0, discountCents);
    }

    private static bool SameMenuComponents(
        IReadOnlyList<MenuComponentSnapshot> left,
        IReadOnlyList<MenuComponentSnapshot> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            var a = left[i];
            var b = right[i];
            if (a.ProductId != b.ProductId ||
                a.Quantity != b.Quantity ||
                !string.Equals(a.ChoiceGroup, b.ChoiceGroup, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    public void Clear()
    {
        if (IsReadOnly) return;
        _cart.Clear();
        DiscountCents = 0;
    }
}

public sealed class PerformanceCounters
{
    public const double SlowThresholdMs = 500d;
    private const int MaxRecentSamples = 120;

    private readonly ConcurrentDictionary<string, Metric> _metrics = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<RecentSample> _recent = new();

    public Scope Measure(string name) => new(this, name, Stopwatch.GetTimestamp());

    // R67: public manual recording is useful for already timed external/device
    // operations and makes deterministic safety tests possible.
    public void RecordElapsed(string name, double ms)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Performance metric name is required.", nameof(name));

        if (double.IsNaN(ms) || double.IsInfinity(ms) || ms < 0)
            throw new ArgumentOutOfRangeException(nameof(ms));

        Record(name.Trim(), ms);
    }

    private void Record(string name, double ms)
    {
        var slow = ms >= SlowThresholdMs ? 1L : 0L;

        _metrics.AddOrUpdate(
            name,
            _ => new Metric(1, ms, ms, ms, slow),
            (_, old) => new Metric(
                old.Count + 1,
                old.TotalMs + ms,
                Math.Max(old.MaxMs, ms),
                ms,
                old.SlowCount + slow));

        _recent.Enqueue(new RecentSample(DateTimeOffset.Now, name, ms, slow > 0));
        while (_recent.Count > MaxRecentSamples && _recent.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyDictionary<string, Snapshot> SnapshotAll() =>
        _metrics.ToDictionary(
            x => x.Key,
            x => new Snapshot(
                x.Value.Count,
                x.Value.TotalMs / Math.Max(1, x.Value.Count),
                x.Value.MaxMs,
                x.Value.LastMs,
                x.Value.SlowCount));

    public IReadOnlyList<RecentSample> Recent(int max = 30)
    {
        var take = Math.Clamp(max, 1, MaxRecentSamples);
        return _recent.ToArray()
            .OrderByDescending(x => x.Timestamp)
            .Take(take)
            .ToArray();
    }

    public void Reset()
    {
        _metrics.Clear();
        while (_recent.TryDequeue(out _))
        {
        }
    }

    public readonly struct Scope : IDisposable
    {
        private readonly PerformanceCounters _owner;
        private readonly string _name;
        private readonly long _start;

        internal Scope(PerformanceCounters owner, string name, long start)
        {
            _owner = owner; _name = name; _start = start;
        }

        public void Dispose() =>
            _owner.Record(_name, Stopwatch.GetElapsedTime(_start).TotalMilliseconds);
    }

    private readonly record struct Metric(
        long Count,
        double TotalMs,
        double MaxMs,
        double LastMs,
        long SlowCount);

    public readonly record struct Snapshot(
        long Count,
        double AverageMs,
        double MaxMs,
        double LastMs,
        long SlowCount);

    public readonly record struct RecentSample(
        DateTimeOffset Timestamp,
        string Name,
        double Milliseconds,
        bool IsSlow);
}


public interface ISettingsRepository
{
    Task<IReadOnlyDictionary<string,string>> LoadAllAsync(CancellationToken ct = default);
    Task<string> GetAsync(string key, string defaultValue = "", CancellationToken ct = default);
    Task SaveManyAsync(IReadOnlyDictionary<string,string> values, CancellationToken ct = default);
}
