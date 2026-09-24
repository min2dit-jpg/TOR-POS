namespace TorPos.Core;

/// <summary>
/// Picks the monitor for a customer-facing fullscreen window (Kundendisplay,
/// Bestellmonitor).
///
/// Windows does not report monitors in a fixed order, so "the second entry of
/// the list" can be the primary screen the till itself runs on - the customer
/// window then covers the cashier's screen. The rules here do not depend on
/// that order:
///
/// - Setting 0 (automatic): never the screen the till window is on, and never
///   the primary screen while another one exists. No second screen means no
///   target at all.
/// - Setting 1-4: a stable numbering - 1 is the Windows primary screen, 2 and
///   up are the remaining screens from left to right (then top to bottom).
/// </summary>
public static class DisplayScreenSelection
{
    /// <summary>
    /// The stable order behind the settings numbers 1-4: primary first, then
    /// left to right, then top to bottom. Returns indexes into
    /// <paramref name="screens"/>.
    /// </summary>
    public static IReadOnlyList<int> StableOrder(IReadOnlyList<DisplayScreenInfo> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);
        return Enumerable.Range(0, screens.Count)
            .OrderByDescending(i => screens[i].IsPrimary)
            .ThenBy(i => screens[i].X)
            .ThenBy(i => screens[i].Y)
            .ToArray();
    }

    /// <summary>
    /// Index into <paramref name="screens"/> for the customer window, or null
    /// when the automatic setting finds no screen apart from the till's.
    /// </summary>
    /// <param name="configured">0 = automatic, 1-4 = fixed screen number.</param>
    /// <param name="tillScreenIndex">Index of the screen showing the till window, if known.</param>
    public static int? Choose(
        IReadOnlyList<DisplayScreenInfo> screens,
        int configured,
        int? tillScreenIndex)
    {
        ArgumentNullException.ThrowIfNull(screens);
        if (screens.Count == 0)
            return null;

        var ordered = StableOrder(screens);
        if (configured >= 1)
            return ordered[Math.Min(configured, ordered.Count) - 1];

        var candidates = ordered
            .Where(i => i != tillScreenIndex)
            .ToArray();
        if (candidates.Length == 0)
            return null;

        // Prefer a non-primary screen; the primary one is where Windows puts
        // the taskbar and, normally, the till.
        foreach (var index in candidates)
        {
            if (!screens[index].IsPrimary)
                return index;
        }

        return tillScreenIndex is null ? null : candidates[0];
    }
}

public sealed record DisplayScreenInfo(bool IsPrimary, int X, int Y);
