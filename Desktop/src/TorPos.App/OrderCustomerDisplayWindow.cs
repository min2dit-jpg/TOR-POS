using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TorPos.Infrastructure;

namespace TorPos.App;

/// <summary>
/// Dedicated public order-status screen. This is intentionally independent from the normal
/// customer display: it contains no cart, prices or payment information.
/// </summary>
public sealed class OrderCustomerDisplayWindow : Window
{
    private readonly OrderWorkflowService _service;
    private readonly bool _training;
    private readonly int _screenIndex;
    private readonly PixelRect? _tillScreenBounds;
    private readonly DispatcherTimer _timer = new();
    private readonly WrapPanel _preparing = new() { Orientation = Orientation.Horizontal };
    private readonly WrapPanel _ready = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _status = new() { Opacity = 0.55, TextWrapping = TextWrapping.Wrap };
    private bool _busy;
    private string _lastSignature = "\u0000";

    public OrderCustomerDisplayWindow(
        OrderWorkflowService service,
        bool training,
        int screenIndex,
        int refreshSeconds,
        PixelRect? tillScreenBounds = null)
    {
        _service = service;
        _training = training;
        _screenIndex = screenIndex;
        _tillScreenBounds = tillScreenBounds;

        Title = "TOR POS · Bestellmonitor";
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowState = WindowState.Normal;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        CanResize = false;
        MinWidth = 800;
        MinHeight = 500;
        Background = new SolidColorBrush(Color.Parse("#07111F"));

        var root = new Grid
        {
            Margin = new Thickness(28),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 20
        };

        var title = new TextBlock
        {
            Text = training ? "BESTELLUNGEN · TRAINING" : "BESTELLUNGEN",
            FontSize = 34,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        root.Children.Add(title);

        var columns = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 24
        };
        Grid.SetRow(columns, 1);
        root.Children.Add(columns);

        var preparingPanel = StatusPanel("WIRD VORBEREITET", _preparing, "#13263A", "#F1F5F9");
        columns.Children.Add(preparingPanel);

        var readyPanel = StatusPanel("FERTIG · BITTE ABHOLEN", _ready, "#123B28", "#8FF0B0");
        Grid.SetColumn(readyPanel, 1);
        columns.Children.Add(readyPanel);

        Grid.SetRow(_status, 2);
        root.Children.Add(_status);
        Content = root;

        _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(refreshSeconds, 1, 10));
        _timer.Tick += async (_, _) => await ReloadAsync();
        Opened += async (_, _) =>
        {
            if (!CustomerScreenPlacement.MoveTo(this, _screenIndex, _tillScreenBounds))
            {
                Close();
                return;
            }
            WindowState = WindowState.FullScreen;
            await ReloadAsync();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private static Border StatusPanel(string heading, WrapPanel numbers, string background, string headingColor)
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 20
        };
        grid.Children.Add(new TextBlock
        {
            Text = heading,
            FontSize = 30,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse(headingColor)),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        Grid.SetRow(numbers, 1);
        grid.Children.Add(numbers);
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(background)),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(24),
            Child = grid
        };
    }

    private async Task ReloadAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var rows = await _service.ListAsync(_training, includeDelivered: false);
            var visible = rows
                .Where(x => x.State is "ACCEPTED" or "PREPARING" or "READY")
                .OrderBy(x => x.PickupNumber > 0 ? x.PickupNumber : x.ParkNumber)
                .ToArray();
            var signature = string.Join('|', visible.Select(x => $"{x.Id}:{x.Version}:{x.State}"));
            if (signature == _lastSignature) return;
            _lastSignature = signature;

            ReplaceNumbers(_preparing, visible.Where(x => x.State is "ACCEPTED" or "PREPARING"), ready: false);
            ReplaceNumbers(_ready, visible.Where(x => x.State == "READY"), ready: true);
            _status.Text = visible.Length == 0
                ? "Aktuell keine Bestellungen in Vorbereitung."
                : "Bestellnummer auf dem Bon beachten.";
        }
        catch (Exception ex)
        {
            _status.Text = "Bestellanzeige kann nicht aktualisiert werden: " + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private static void ReplaceNumbers(WrapPanel target, IEnumerable<OrderOverview> rows, bool ready)
    {
        target.Children.Clear();
        var any = false;
        foreach (var row in rows)
        {
            any = true;
            var number = row.PickupNumber > 0 ? row.PickupNumber.ToString("000") : $"P{row.ParkNumber:000000}";
            target.Children.Add(new Border
            {
                Margin = new Thickness(9),
                Padding = new Thickness(22, 14),
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(Color.Parse(ready ? "#1E8E4A" : "#1B3148")),
                Child = new TextBlock
                {
                    Text = number,
                    FontSize = 56,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#FFFFFF"))
                }
            });
        }
        if (!any)
        {
            target.Children.Add(new TextBlock
            {
                Text = "—",
                FontSize = 72,
                Opacity = 0.35,
                Margin = new Thickness(18)
            });
        }
    }

}
