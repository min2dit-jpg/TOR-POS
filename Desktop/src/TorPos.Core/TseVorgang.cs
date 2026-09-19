namespace TorPos.Core;

public enum TseVorgangActionKind
{
    None,
    Start,
    Abort
}

/// <param name="CancelledLines">R143: positions cancelled during capture before the abort.</param>
public sealed record TseVorgangAction(
    TseVorgangActionKind Kind,
    string VorgangId,
    DateTimeOffset StartedAt,
    CartLine[] Lines,
    long DiscountCents,
    CartLine[]? CancelledLines = null)
{
    public static readonly TseVorgangAction None =
        new(TseVorgangActionKind.None, "", default, Array.Empty<CartLine>(), 0);
}

/// <summary>
/// R136: when a Vorgang begins and ends, seen from the cart.
///
/// AEAO zu § 146a Nr. 2.2.2: "Das Aufzeichnungssystem muss unmittelbar mit
/// Beginn eines aufzuzeichnenden Vorgangs die Protokollierung des Vorgangs in
/// der TSE starten"; Nr. 2.2.3.3: the decisive time is when the system starts
/// the Vorgang, and before a receipt is issued or at a closing it must be ended.
/// Until R136 TOR started and finished the transaction together after payment.
///
/// The Vorgang begins with the first position in an empty cart. It ends
/// explicitly with payment, order acceptance or parking (<see cref="Release"/>),
/// or - when the cart becomes empty without any of those - as an aborted
/// Vorgang (AVBelegabbruch, AEAO Nr. 1.11.1 "Belegabbrüche").
///
/// R143: it also collects the positions cancelled during capture - a removed
/// line (SOFORT STORNO), a lowered quantity (-1, MENGE ×) - so the receipt can
/// document them (DSFinV-K 4.2.3).
///
/// The Vorgang has its own id, separate from the checkout operation id, which
/// changes during one customer (a declined card, a reviewed payment).
/// </summary>
public sealed class TseVorgangCartTracker
{
    private CartLine[] _lines = Array.Empty<CartLine>();
    private long _discountCents;
    private string? _baseline;
    private Dictionary<PositionKey, (CartLine Line, decimal Quantity)> _captured = new();
    private readonly List<CartLine> _cancelled = new();

    public string? VorgangId { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>R143: positions cancelled during capture in this Vorgang (copies, positive quantities).</summary>
    public IReadOnlyList<CartLine> CancelledLines => _cancelled;

    /// <summary>
    /// Call after every change of the cart. <paramref name="fiscal"/> is true
    /// only on a till that records the Vorgang fiscally (real booking or
    /// recorded training); otherwise nothing is ever started.
    /// </summary>
    public TseVorgangAction OnCartChanged(IReadOnlyList<CartLine> cart, long discountCents, bool imHaus, bool fiscal, DateTimeOffset now)
    {
        if (cart.Count > 0)
        {
            CollectCancellations(cart, imHaus);

            // A copy: cart lines are mutable, and an abort documents the
            // positions as they were when the cart was emptied.
            _lines = CheckoutSnapshot.CopyLines(cart, imHaus);
            _discountCents = discountCents;

            if (VorgangId is not null || !fiscal)
                return TseVorgangAction.None;

            // R137: a recalled order that is only looked at or paid unchanged
            // begins no Vorgang here (DSFinV-K 2.7.2: the Kassenbeleg may start
            // with the payment); the first change does.
            if (_baseline is not null && _baseline == Signature(cart, discountCents))
                return TseVorgangAction.None;
            _baseline = null;

            VorgangId = Guid.NewGuid().ToString("N");
            StartedAt = now;
            return new TseVorgangAction(TseVorgangActionKind.Start, VorgangId, now, _lines, discountCents);
        }

        if (VorgangId is not { } open)
        {
            Clear();
            return TseVorgangAction.None;
        }

        var aborted = new TseVorgangAction(TseVorgangActionKind.Abort, open, StartedAt ?? now, _lines, _discountCents, _cancelled.ToArray());
        Clear();
        return aborted;
    }

    /// <summary>The Vorgang ends by payment, order acceptance or parking; the next empty cart is not an abort.</summary>
    public string? Release()
    {
        var id = VorgangId;
        Clear();
        return id;
    }

    /// <summary>
    /// R137: the cart now holds a recalled order whose positions are already
    /// secured. As long as it stays exactly like this, no Vorgang is started.
    /// </summary>
    public void SetBaseline(IReadOnlyList<CartLine> cart, long discountCents)
    {
        if (VorgangId is not null)
            return;
        _baseline = Signature(cart, discountCents);
        _captured = Capture(cart);
        _cancelled.Clear();
    }

    /// <summary>Continues a Vorgang started earlier (a recalled parked receipt, a recovered cart).</summary>
    public void Adopt(string vorgangId, DateTimeOffset startedAt, IReadOnlyList<CartLine> cart, long discountCents, bool imHaus,
        IReadOnlyList<CartLine>? cancelled = null)
    {
        VorgangId = vorgangId;
        StartedAt = startedAt;
        _lines = CheckoutSnapshot.CopyLines(cart, imHaus);
        _discountCents = discountCents;
        _captured = Capture(cart);
        _cancelled.Clear();
        if (cancelled is not null)
            _cancelled.AddRange(CheckoutSnapshot.CopyLines(cancelled, imHaus: false));
    }

    /// <summary>
    /// R143: DSFinV-K 4.2.3 - a position cancelled during capture is documented
    /// by "ein zusätzlicher Positionsdatensatz …, bei dem MENGE mit negiertem
    /// Vorzeichen dargestellt wird". A cancelled position therefore appears as the
    /// captured position and its cancellation: +quantity and -quantity, which add
    /// up to nothing.
    /// </summary>
    public static IReadOnlyList<CartLine> CancellationPairs(IEnumerable<CartLine>? cancelled) =>
        (cancelled ?? Array.Empty<CartLine>())
            .Where(l => l.Quantity != 0)
            .SelectMany(l => new[]
            {
                OrderBestellungDelta.WithQuantity(l, l.Quantity),
                OrderBestellungDelta.WithQuantity(l, -l.Quantity)
            })
            .ToList();

    private void CollectCancellations(IReadOnlyList<CartLine> cart, bool imHaus)
    {
        var now = Capture(cart);
        foreach (var (key, (line, quantity)) in _captured)
        {
            var remaining = now.TryGetValue(key, out var current) ? current.Quantity : 0m;
            if (remaining < quantity)
            {
                var copy = CheckoutSnapshot.CopyLines(new[] { line }, imHaus)[0];
                _cancelled.Add(OrderBestellungDelta.WithQuantity(copy, quantity - remaining));
            }
        }

        _captured = now;
    }

    // Im Haus changes the rate, not the position, so the rate is not part of it.
    // R153: two identical menu names with different chosen articles are distinct
    // positions for capture/cancellation even though the customer receipt text is the same.
    private readonly record struct PositionKey(
        long ProductId,
        string Name,
        string Variant,
        long UnitPrice,
        long Pfand,
        long PromotionId,
        string MenuSelection);

    private static Dictionary<PositionKey, (CartLine Line, decimal Quantity)> Capture(IReadOnlyList<CartLine> cart)
    {
        var captured = new Dictionary<PositionKey, (CartLine Line, decimal Quantity)>();
        foreach (var line in cart)
        {
            var key = new PositionKey(
                line.ProductId,
                line.ProductName,
                line.VariantName,
                line.UnitPriceCents,
                line.PfandCents,
                line.PromotionId,
                MenuSelectionKey(line));
            captured[key] = captured.TryGetValue(key, out var existing)
                ? (existing.Line, existing.Quantity + line.Quantity)
                : (OrderBestellungDelta.WithQuantity(line, line.Quantity), line.Quantity);
        }

        return captured;
    }

    private void Clear()
    {
        VorgangId = null;
        StartedAt = null;
        _lines = Array.Empty<CartLine>();
        _discountCents = 0;
        _baseline = null;
        _captured = new();
        _cancelled.Clear();
    }

    private static string MenuSelectionKey(CartLine line) =>
        string.Join(
            ",",
            line.MenuComponents.Select(x =>
                $"{x.ProductId}:{x.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{x.ChoiceGroup}"));

    private static string Signature(IReadOnlyList<CartLine> cart, long discountCents) =>
        string.Join("|", cart.Select(l => string.Join(";",
                l.ProductId, l.ProductName, l.VariantName,
                l.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                l.UnitPriceCents, l.VatRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                l.PfandCents, l.PromotionId, MenuSelectionKey(l))))
        + "#" + discountCents;
}
