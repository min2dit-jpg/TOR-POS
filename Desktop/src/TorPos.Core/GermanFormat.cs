using System.Globalization;

namespace TorPos.Core;

/// <summary>
/// R123: number formatting for everything TOR POS prints or hands out as a
/// German document - the Kassenbon, the Küchenbon, the digital receipt, and the
/// management reports.
///
/// PR #1 (fix/report-currency-culture) found that the STORNO report wrote
/// "5.00 EUR" instead of "5,00 EUR" whenever the Windows account ran under an
/// English culture: `$"{cents / 100m:0.00}"` formats with the CURRENT culture,
/// and nothing in TOR POS sets one. Reviewing that PR turned up the same
/// pattern in more places, the most serious being the printed receipt itself
/// (StarMcPrint3PrinterService.Money) - a legal German Beleg printing its
/// amounts with a decimal point. Quantities ("1.5 x") and VAT rates ("5.5 %")
/// had the same problem on the receipt, the kitchen ticket and the digital
/// receipt.
///
/// R49 already decided that printed and fiscal output stays German whatever
/// the UI language is; this class is what makes that true for numbers, not
/// just for words. Parsing was checked at the same time and is already
/// culture-safe (explicit InvariantCulture with ',' normalized, or
/// Formatting.TryParseMoney with de-DE).
/// </summary>
public static class GermanFormat
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>"1234,50" - no currency, no thousands separator.</summary>
    public static string Amount(long cents) =>
        (cents / 100m).ToString("0.00", Culture);

    /// <summary>"1234,50 EUR" - the style used on receipts and in reports.</summary>
    public static string Eur(long cents) => Amount(cents) + " EUR";

    public static string Number(decimal value, string format) =>
        value.ToString(format, Culture);

    public static string Number(double value, string format) =>
        value.ToString(format, Culture);

    /// <summary>
    /// Formats a whole interpolated line under de-DE, so a line with several
    /// placeholders does not need a helper call per value:
    /// <c>GermanFormat.Line($"MwSt {rate:0.##}% · {qty:0.###} Stk.")</c>.
    /// </summary>
    public static string Line(FormattableString text) =>
        text.ToString(Culture);
}
