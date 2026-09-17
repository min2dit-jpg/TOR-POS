namespace TorPos.Core;

/// <summary>
/// R148: the deposit positions added with the PFAND / LEERGUT key. They are not
/// catalogue articles; their technical product ids identify them as deposit.
///
/// DSFinV-K Anhang C: deposit received is its own business case "Pfand" - a
/// bottle deposit as part of the delivery it belongs to (Warenumschließung), a
/// crate as a delivery of its own (Transporthilfsmittel, general rate). Until
/// R148 these positions were exported as "Umsatz".
/// </summary>
public static class PfandProducts
{
    public const long Bottle8 = -8008;
    public const long Bottle15 = -8015;
    public const long Bottle25 = -8025;
    public const long CrateEmpty = -8150;
    public const long CrateFull = -8330;

    public static bool IsDeposit(long productId) =>
        productId is Bottle8 or Bottle15 or Bottle25 or CrateEmpty or CrateFull;
}
