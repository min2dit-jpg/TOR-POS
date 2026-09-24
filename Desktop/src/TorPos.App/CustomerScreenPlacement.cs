using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using TorPos.Core;

namespace TorPos.App;

/// <summary>
/// Places a customer-facing fullscreen window (Kundendisplay, Bestellmonitor)
/// on its monitor using <see cref="DisplayScreenSelection"/>, so it never
/// lands on the screen the till itself is shown on just because Windows
/// listed the monitors in another order.
/// </summary>
internal static class CustomerScreenPlacement
{
    public static Screen? Find(TopLevel window, int configured, PixelRect? tillBounds)
    {
        var screens = window.Screens?.All;
        if (screens is null || screens.Count == 0)
            return null;

        var infos = screens
            .Select(s => new DisplayScreenInfo(s.IsPrimary, s.Bounds.X, s.Bounds.Y))
            .ToArray();

        int? tillIndex = null;
        if (tillBounds is { } till)
        {
            var center = new PixelPoint(till.X + till.Width / 2, till.Y + till.Height / 2);
            for (var i = 0; i < screens.Count; i++)
            {
                if (screens[i].Bounds.Contains(center))
                {
                    tillIndex = i;
                    break;
                }
            }
        }

        var index = DisplayScreenSelection.Choose(infos, configured, tillIndex);
        return index is null ? null : screens[index.Value];
    }

    /// <summary>
    /// Moves the window onto its monitor. Returns false when the automatic
    /// setting finds no monitor apart from the till's; the caller then must
    /// not go fullscreen, or it would cover the cashier's screen.
    /// </summary>
    public static bool MoveTo(Window window, int configured, PixelRect? tillBounds)
    {
        var target = Find(window, configured, tillBounds);
        if (target is null)
            return false;

        window.Position = new PixelPoint(target.Bounds.X, target.Bounds.Y);
        return true;
    }
}
