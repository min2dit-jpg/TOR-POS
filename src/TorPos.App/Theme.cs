using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TorPos.App;

/// <summary>
/// R110: the single source of truth for this app's dark navy theme. Before
/// this, every one of the ~40 code-behind windows hardcoded its own literal
/// hex colors and corner radii - the same conceptual "card" container ended
/// up with corner radius 7, 8, 9, 10 and 12 in different windows, and the
/// same "secondary panel border" role used four different hex values across
/// files. Same color scheme as before (this is a consolidation pass, not a
/// new palette) - just one place to read/change it, and a few reusable
/// builders for the patterns that were copy-pasted near-identically into
/// every window's constructor.
/// </summary>
// R110: named "AppTheme", not "Theme" - Avalonia's StyledElement already
// has an inherited instance member called "Theme" (its own ControlTheme
// mechanism), which every Window/Control in this codebase derives from.
// A bare "Theme.X" reference inside any window's instance code resolves to
// that inherited member first, not this static class - confirmed the hard
// way on the first build attempt.
public static class AppTheme
{
    public static readonly IBrush BgPrimary = new SolidColorBrush(Color.Parse("#0D1420"));
    public static readonly IBrush SurfacePanel = new SolidColorBrush(Color.Parse("#111B2A"));
    public static readonly IBrush PanelBorder = new SolidColorBrush(Color.Parse("#28445D"));
    // Slightly lighter navy fill used specifically for the small inline
    // "info card" text blocks (SettingsWindow's InfoCard helper) - a
    // deliberately different shade from SurfacePanel, not a duplicate.
    public static readonly IBrush InfoCardBg = new SolidColorBrush(Color.Parse("#15374A"));

    public static readonly IBrush AccentTeal = new SolidColorBrush(Color.Parse("#53E0C0"));
    public static readonly IBrush AccentBlue = new SolidColorBrush(Color.Parse("#9CC3FF"));
    public static readonly IBrush TextPrimary = new SolidColorBrush(Color.Parse("#F7FBFF"));
    public static readonly IBrush TextMuted = new SolidColorBrush(Color.Parse("#8EA8BE"));

    public static readonly IBrush WarningAmber = new SolidColorBrush(Color.Parse("#FFD166"));
    public static readonly IBrush WarningAmberBg = new SolidColorBrush(Color.Parse("#493A12"));

    public static readonly IBrush SuccessGreen = new SolidColorBrush(Color.Parse("#0E9F47"));
    public static readonly IBrush SuccessGreenBorder = new SolidColorBrush(Color.Parse("#4DDA82"));

    public static readonly IBrush InfoBlue = new SolidColorBrush(Color.Parse("#1766D1"));
    public static readonly IBrush InfoBlueBorder = new SolidColorBrush(Color.Parse("#65A0FF"));

    public static readonly IBrush DangerRed = new SolidColorBrush(Color.Parse("#6D2734"));
    public static readonly IBrush DangerRedBorder = new SolidColorBrush(Color.Parse("#8F2D2D"));

    // The standard "neutral" button chrome already established by
    // App.axaml's Button.action style - reused here so one-off buttons
    // built in code-behind (outside that style's selector) match it
    // instead of drifting to a slightly different navy.
    public static readonly IBrush ButtonNeutral = new SolidColorBrush(Color.Parse("#1C3852"));
    public static readonly IBrush ButtonNeutralBorder = new SolidColorBrush(Color.Parse("#5C7690"));

    public const double CardRadius = 10;
    public const double PillRadius = 8;

    /// <summary>Standard section/panel container - the "card" every window built by hand.</summary>
    public static Border Card(Control content, Thickness? padding = null) => new()
    {
        Background = SurfacePanel,
        BorderBrush = PanelBorder,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(CardRadius),
        Padding = padding ?? new Thickness(14),
        Child = content
    };

    /// <summary>Warning banner (amber) - the "attention needed" box repeated across windows.</summary>
    public static Border WarningBanner(string text) => new()
    {
        Background = WarningAmberBg,
        BorderBrush = WarningAmber,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(PillRadius),
        Padding = new Thickness(12, 8),
        Child = new TextBlock { Text = text, Foreground = WarningAmber, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }
    };

    /// <summary>Danger banner (red) - error/blocked-state box repeated across windows.</summary>
    public static Border DangerBanner(string text) => new()
    {
        Background = DangerRed,
        BorderBrush = DangerRedBorder,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(PillRadius),
        Padding = new Thickness(12, 8),
        Child = new TextBlock { Text = text, Foreground = TextPrimary, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }
    };

    /// <summary>Standard right-aligned bottom button row (Abbrechen/Bestätigen and similar).</summary>
    public static StackPanel ButtonRow(params Control[] buttons)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 16, 0, 0)
        };
        foreach (var b in buttons)
            panel.Children.Add(b);
        return panel;
    }
}
