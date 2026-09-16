namespace TorPos.Core;

/// <summary>
/// R126: how much text the till's header status area may use.
///
/// The header held the menu, every status badge and the essential buttons in
/// one row that could not shrink. On the HP till (and at 1366px on a laptop) the
/// buttons that finish a sale correctly - AUSSER HAUS for the Im-Haus VAT rate,
/// GEMISCHT for split payment - and ABMELDEN were pushed off the screen. The
/// buttons now have their own column; this decides how far the status texts
/// shorten so they fit into what is left.
/// </summary>
public enum HeaderDensity
{
    /// <summary>Full sentences, company name shown.</summary>
    Full,
    /// <summary>Short labels ("TEST · KEINE LIZENZ", "admin").</summary>
    Compact,
    /// <summary>Single words; the edition badge and the user badge are hidden.</summary>
    Minimal
}

/// <summary>One header status text in its three lengths.</summary>
public readonly record struct HeaderLabel(string Full, string Compact, string Minimal)
{
    public string For(HeaderDensity density) => density switch
    {
        HeaderDensity.Full => Full,
        HeaderDensity.Compact => Compact,
        _ => Minimal
    };
}

public static class HeaderDensityPolicy
{
    /// <summary>
    /// Full texts are only worth it while the company name next to the logo
    /// still gets readable room; otherwise the shorter texts are the better use
    /// of the space.
    /// </summary>
    public const double MinimumCompanyWidthForFull = 160;

    /// <summary>Below this the company name would be a few letters and an ellipsis.</summary>
    public const double MinimumCompanyWidthToShow = 70;

    /// <param name="hostWidth">Width shared by company name, edition, menu and status badges.</param>
    /// <param name="contentWidth">Width the edition badge, menu and status badges need at a given density.</param>
    public static HeaderDensity Choose(double hostWidth, Func<HeaderDensity, double> contentWidth)
    {
        if (hostWidth - contentWidth(HeaderDensity.Full) >= MinimumCompanyWidthForFull)
            return HeaderDensity.Full;
        if (hostWidth - contentWidth(HeaderDensity.Compact) >= 0)
            return HeaderDensity.Compact;
        return HeaderDensity.Minimal;
    }

    public static bool ShowCompanyName(double hostWidth, double contentWidth) =>
        hostWidth - contentWidth >= MinimumCompanyWidthToShow;
}
