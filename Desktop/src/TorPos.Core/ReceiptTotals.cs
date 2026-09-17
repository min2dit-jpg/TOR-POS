namespace TorPos.Core;

/// <summary>
/// R149: the total of a receipt from its positions and the manual discount.
///
/// A discount never makes a purchase negative, so a positive subtotal is cut
/// at zero as before. Returned deposit (<see cref="PfandProducts"/>) can make
/// the subtotal itself negative - money is paid out - and that amount is kept
/// as it is. A manual discount is not combined with returned deposit.
/// </summary>
public static class ReceiptTotals
{
    public static long Total(long subtotalCents, long discountCents) =>
        subtotalCents < 0
            ? subtotalCents - Math.Max(0, discountCents)
            : Math.Max(0, subtotalCents - discountCents);
}
