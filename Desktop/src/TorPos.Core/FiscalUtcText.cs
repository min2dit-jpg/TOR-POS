using System.Globalization;

namespace TorPos.Core;

/// <summary>
/// Review §6 (Z period boundaries): every period query compares the generated
/// column created_at_utc = strftime('%Y-%m-%dT%H:%M:%fZ', created_at) with a
/// bound written by .NET. SQLite ROUNDS the fraction to milliseconds
/// (12.3456 -> 12.346), while the old "fff" formatting TRUNCATED it (12.345).
/// A Bon committed in the last half millisecond before a period end then
/// compared as later than that end and fell out of the X/Z it belonged to.
/// This writes a bound exactly as SQLite writes the column, so a Bon before a
/// bound always compares at or before it.
/// </summary>
public static class FiscalUtcText
{
    public static string Of(DateTimeOffset value) => Format(Rounded(value));

    /// <summary>
    /// The first column value AFTER <paramref name="value"/>: for an exclusive
    /// lower bound (a Z period starts after the previous closing) used with
    /// "&gt;=", so it selects exactly what "&gt; Of(value)" selects - the rule
    /// the DSFinV-K export and the DATEV Kassenbuch apply to the same period.
    /// </summary>
    public static string After(DateTimeOffset value)
    {
        var rounded = Rounded(value);
        return Format(rounded < DateTime.MaxValue.AddMilliseconds(-1) ? rounded.AddMilliseconds(1) : rounded);
    }

    /// <summary>
    /// SQLite's own arithmetic (date.c): the fraction digits are accumulated as
    /// a double (ms = ms*10 + d; scale *= 10; ms /= scale), clamped to 0.999,
    /// added to the whole seconds, and the milliseconds taken as (int64)(s*1000 + 0.5). Mirrored
    /// step by step, because a mathematically exact half-millisecond rounding
    /// differs from it at the last bit of the double.
    /// </summary>
    private static DateTime Rounded(DateTimeOffset value)
    {
        var utc = value.UtcDateTime;
        var fraction = (utc.Ticks % TimeSpan.TicksPerSecond).ToString("0000000", CultureInfo.InvariantCulture);
        double ms = 0.0, scale = 1.0;
        foreach (var digit in fraction)
        {
            ms = ms * 10.0 + (digit - '0');
            scale *= 10.0;
        }
        ms /= scale;
        // SQLite clamps the fraction so .9995 and above never rolls into the
        // next second (sqlite.org/forum/forumpost/766a2c9231).
        if (ms > 0.999) ms = 0.999;
        var seconds = utc.Second + ms;
        var wholeMs = (long)(seconds * 1000 + 0.5);
        var minute = new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc);
        return minute.AddMilliseconds(wholeMs);
    }

    private static string Format(DateTime utc) =>
        utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
