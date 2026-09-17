namespace TorPos.Core;

public enum TseVorgangActionKind
{
    None,
    Start,
    Abort
}

public sealed record TseVorgangAction(
    TseVorgangActionKind Kind,
    string VorgangId,
    DateTimeOffset StartedAt,
    CartLine[] Lines,
    long DiscountCents)
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
/// The Vorgang has its own id, separate from the checkout operation id, which
/// changes during one customer (a declined card, a reviewed payment).
/// </summary>
public sealed class TseVorgangCartTracker
{
    private CartLine[] _lines = Array.Empty<CartLine>();
    private long _discountCents;
    private string? _baseline;

    public string? VorgangId { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>
    /// Call after every change of the cart. <paramref name="fiscal"/> is true
    /// only on a till that records the Vorgang fiscally (real booking or
    /// recorded training); otherwise nothing is ever started.
    /// </summary>
    public TseVorgangAction OnCartChanged(IReadOnlyList<CartLine> cart, long discountCents, bool imHaus, bool fiscal, DateTimeOffset now)
    {
        if (cart.Count > 0)
        {
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
            _baseline = null;
            return TseVorgangAction.None;
        }

        var aborted = new TseVorgangAction(TseVorgangActionKind.Abort, open, StartedAt ?? now, _lines, _discountCents);
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
        if (VorgangId is null)
            _baseline = Signature(cart, discountCents);
    }

    /// <summary>Continues a Vorgang started earlier (a recalled parked receipt, a recovered cart).</summary>
    public void Adopt(string vorgangId, DateTimeOffset startedAt, IReadOnlyList<CartLine> cart, long discountCents, bool imHaus)
    {
        VorgangId = vorgangId;
        StartedAt = startedAt;
        _lines = CheckoutSnapshot.CopyLines(cart, imHaus);
        _discountCents = discountCents;
    }

    private void Clear()
    {
        VorgangId = null;
        StartedAt = null;
        _lines = Array.Empty<CartLine>();
        _discountCents = 0;
        _baseline = null;
    }

    private static string Signature(IReadOnlyList<CartLine> cart, long discountCents) =>
        string.Join("|", cart.Select(l => string.Join(";",
                l.ProductId, l.ProductName, l.VariantName,
                l.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                l.UnitPriceCents, l.VatRate.ToString(System.Globalization.CultureInfo.InvariantCulture), l.PfandCents, l.PromotionId)))
        + "#" + discountCents;
}
