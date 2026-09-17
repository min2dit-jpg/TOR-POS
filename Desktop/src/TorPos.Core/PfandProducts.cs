namespace TorPos.Core;

/// <summary>
/// R148/R149: the deposit positions of the PFAND / LEERGUT key. They are not
/// catalogue articles; their technical product ids identify them as deposit.
///
/// R149: the key takes back empties (Leergut). The customer receives the deposit,
/// money leaves the till - a position with a negative amount, never a sale.
/// Deposit charged when a drink is sold is set on the article itself (PfandCents).
///
/// DSFinV-K Anhang C: "PfandRueckzahlung" documents "alle Rückgaben von
/// Pfandgegenständen sowie die Verrechnung des Pfandbetrages oder die Auszahlung
/// an den Kunden". The rate: a bottle is a Warenumschließung and shares the fate
/// of the main supply (milk 7 %, its bottle deposit 7 %); a crate is a
/// Transporthilfsmittel, a supply of its own at the general rate (§ 12 Abs. 1
/// UStG). The refund reduces the consideration at that rate.
/// </summary>
public static class PfandProducts
{
    public const long Bottle8 = -8008;
    public const long Bottle15 = -8015;
    public const long Bottle25 = -8025;
    public const long CrateEmpty = -8150;
    public const long CrateFull = -8330;

    public const decimal GeneralRate = 19m;
    public const decimal ReducedRate = 7m;

    public static bool IsDeposit(long productId) =>
        productId is Bottle8 or Bottle15 or Bottle25 or CrateEmpty or CrateFull;

    /// <summary>A crate is a Transporthilfsmittel; its deposit never follows the drink.</summary>
    public static bool IsCrate(long productId) => productId is CrateEmpty or CrateFull;

    /// <summary>
    /// The VAT rate of returned deposit: a crate always the general rate; a bottle
    /// the rate of the drink it held - reduced for milk and milk drinks.
    /// </summary>
    public static decimal RateFor(long productId, bool reducedRateGoods) =>
        !IsCrate(productId) && reducedRateGoods ? ReducedRate : GeneralRate;

    /// <summary>R149: a position of returned deposit (negative amount).</summary>
    public static bool IsDepositReturn(CartLine line) =>
        IsDeposit(line.ProductId) && line.UnitPriceCents < 0;
}
