using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using TorPos.Core;

namespace TorPos.App;

/// <summary>
/// R126: keeps the header usable on small screens. See HeaderDensityPolicy for
/// the rule and MainWindow.axaml (header) for the layout it works with.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<TextBlock, HeaderLabel> _headerLabels = new();
    private HeaderDensity _headerDensity = HeaderDensity.Full;
    private string _headerLayoutKey = "";
    private bool _applyingHeaderDensity;

    // The cart header shows "AKTUELLER VERKAUF", the EINGABE display and a
    // LOKALE KASSE label. Below this width all three cannot fit, and EINGABE -
    // the quantity being typed - is the one that must not be cut.
    private const double CartHeaderWidthForLocalRegisterLabel = 420;

    private void InitializeResponsiveHeader()
    {
        HeaderMainHost.SizeChanged += (_, _) => UpdateHeaderDensity();
        // Badges appear and disappear (TSE outage, stock, licence, update)
        // without the host changing size; the key check makes this cheap.
        HeaderStatusPanel.LayoutUpdated += (_, _) => UpdateHeaderDensity();
        CartHeaderGrid.SizeChanged += (_, e) =>
            LocalRegisterBadge.IsVisible = e.NewSize.Width >= CartHeaderWidthForLocalRegisterLabel;
    }

    /// <summary>
    /// Sets a header status text in all three lengths. Tooltips stay with the
    /// callers, because some badges already carry their own (stock list, licence
    /// notice, outage reason).
    /// </summary>
    private void SetHeaderLabel(TextBlock target, string full, string compact, string minimal)
    {
        _headerLabels[target] = new HeaderLabel(full, compact, minimal);
        target.Text = _headerLabels[target].For(_headerDensity);
        _headerLayoutKey = "";
        UpdateHeaderDensity();
    }

    private void UpdateHeaderDensity()
    {
        if (_applyingHeaderDensity) return;
        var host = HeaderMainHost.Bounds.Width;
        if (host <= 0) return;

        var key = string.Join("|",
            Math.Round(host),
            TseOutageBadge.IsVisible, LicenseWarningBadge.IsVisible,
            StockWarningBadge.IsVisible, UpdateButton.IsVisible,
            string.Join("¦", _headerLabels.Values.Select(x => x.Full)));
        if (key == _headerLayoutKey) return;

        _applyingHeaderDensity = true;
        try
        {
            _headerDensity = HeaderDensityPolicy.Choose(host, HeaderContentWidth);
            var content = HeaderContentWidth(_headerDensity);   // leaves the chosen texts applied
            CompanyBlock.IsVisible = HeaderDensityPolicy.ShowCompanyName(host, content);
            _headerLayoutKey = key;
        }
        finally
        {
            _applyingHeaderDensity = false;
        }
    }

    private double HeaderContentWidth(HeaderDensity density)
    {
        foreach (var (target, label) in _headerLabels)
        {
            target.Text = label.For(density);
            // Changing Text only invalidates the TextBlock itself; its parents
            // keep their cached measurement until the next layout pass, so the
            // Measure below would silently return the width of the PREVIOUS
            // text (this is exactly what the first snapshot run showed).
            InvalidateMeasureUpTo(target, HeaderMainHost);
        }

        var minimal = density == HeaderDensity.Minimal;
        EditionBadge.IsVisible = !minimal;
        UserModeBadge.IsVisible = !minimal;
        HeaderStatusPanel.InvalidateMeasure();

        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        double width = 0;
        foreach (var part in new Control[] { EditionBadge, HeaderMenu, HeaderStatusPanel })
        {
            if (!part.IsVisible) continue;
            part.Measure(unbounded);
            width += part.DesiredSize.Width;   // DesiredSize already includes Margin
        }
        return width;
    }

    private static void InvalidateMeasureUpTo(Visual start, Visual stop)
    {
        for (Visual? visual = start; visual is not null && visual != stop; visual = visual.GetVisualParent())
            (visual as Layoutable)?.InvalidateMeasure();
    }
}
