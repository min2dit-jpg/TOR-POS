namespace TorPos.Core;

/// <summary>
/// R170: one canonical weight model for checkout. Input may be grams or kg,
/// but every commercial quantity is normalized to kilograms before it reaches
/// SaleEngine, stock, returns or fiscal records.
/// </summary>
public enum WeightInputUnit
{
    Gram,
    Kilogram
}

public static class WeightedSales
{
    public const decimal MaxKilogramsPerLine = 9999m;

    public static decimal ToKilograms(decimal value, WeightInputUnit unit)
    {
        if (value <= 0m)
            throw new ArgumentOutOfRangeException(nameof(value), "Gewicht muss größer als 0 sein.");

        var kg = unit == WeightInputUnit.Gram
            ? value / 1000m
            : value;

        kg = decimal.Round(kg, 3, MidpointRounding.AwayFromZero);
        if (kg <= 0m || kg > MaxKilogramsPerLine)
            throw new ArgumentOutOfRangeException(nameof(value), "Gewicht liegt außerhalb des erlaubten Bereichs.");

        return kg;
    }

    public static long TotalCents(long pricePerKgCents, decimal kilograms)
    {
        if (pricePerKgCents < 0)
            throw new ArgumentOutOfRangeException(nameof(pricePerKgCents));
        if (kilograms <= 0m)
            throw new ArgumentOutOfRangeException(nameof(kilograms));

        return (long)Math.Round(
            kilograms * pricePerKgCents,
            MidpointRounding.AwayFromZero);
    }

    public static string QuantityLabel(decimal kilograms) =>
        $"{GermanFormat.Number(kilograms, "0.###")} kg";
}
