namespace TorPos.Core;

/// <summary>
/// R172 fixed-point storage for commercial quantities.
/// One unit = 1000 milli-units. For weighted articles whose commercial unit is
/// kg this means one milli-unit is exactly one gram.
/// </summary>
public static class QuantityStorage
{
    public const long Scale = 1000;

    public static long ToMilli(decimal quantity)
    {
        var scaled = checked(quantity * Scale);
        if (decimal.Truncate(scaled) != scaled)
            throw new InvalidOperationException(
                "Mengen dürfen höchstens drei Nachkommastellen haben.");

        return checked(decimal.ToInt64(scaled));
    }

    public static decimal FromMilli(long milli)
        => milli / (decimal)Scale;

    public static long FromLegacyReal(double quantity)
        => checked((long)Math.Round(
            quantity * Scale,
            MidpointRounding.AwayFromZero));
}
