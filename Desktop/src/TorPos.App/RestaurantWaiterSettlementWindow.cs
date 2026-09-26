using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.App;

/// <summary>
/// R-5.1: Kellnerabrechnung for the open Z period, read only. German-only like
/// every Restaurant screen. Opening it changes nothing; it is information for the
/// end of a shift and never a substitute for the X or Z report.
/// Entry point (menu/table plan) is wired by the Restaurant workspace owner.
/// </summary>
public sealed class RestaurantWaiterSettlementWindow : Window
{
    private readonly RestaurantWaiterSettlementService _service;
    private readonly StackPanel _rows = new() { Spacing = 10 };
    private readonly TextBlock _period = new() { Opacity = 0.78, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _totals = new() { FontSize = 18, FontWeight = FontWeight.Bold };

    public RestaurantWaiterSettlementWindow(RestaurantWaiterSettlementService service)
    {
        _service = service;
        Title = "TOR Restaurant · Kellnerabrechnung";
        Width = 760;
        Height = 680;
        MinWidth = 640;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0D1420"));

        var refresh = new Button { Content = "AKTUALISIEREN", MinHeight = 46, MinWidth = 160 };
        refresh.Click += async (_, _) => await LoadAsync();
        var close = new Button { Content = "SCHLIESSEN", MinHeight = 46, MinWidth = 140 };
        close.Click += (_, _) => Close();

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(20),
            Children =
            {
                new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = "KELLNERABRECHNUNG", FontSize = 24, FontWeight = FontWeight.Bold },
                        _period,
                        new TextBlock
                        {
                            Text = "Nur zur Information – ersetzt weder X- noch Z-Bericht. Es wird nichts gebucht.",
                            Opacity = 0.72,
                            TextWrapping = TextWrapping.Wrap
                        }
                    }
                },
                new ScrollViewer
                {
                    [Grid.RowProperty] = 1,
                    Margin = new Thickness(0, 16, 0, 16),
                    Content = _rows
                },
                new StackPanel
                {
                    [Grid.RowProperty] = 2,
                    Spacing = 10,
                    Children =
                    {
                        _totals,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { refresh, close }
                        }
                    }
                }
            }
        };

        Opened += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var settlement = await _service.BuildForOpenPeriodAsync();
            _period.Text = settlement.From == DateTimeOffset.MinValue
                ? $"Offener Zeitraum: seit Beginn bis {settlement.To.ToLocalTime():dd.MM.yyyy HH:mm}"
                : $"Offener Zeitraum seit letztem Tagesabschluss: {settlement.From.ToLocalTime():dd.MM.yyyy HH:mm} – {settlement.To.ToLocalTime():HH:mm}";
            _rows.Children.Clear();
            foreach (var row in settlement.Rows)
                _rows.Children.Add(RowCard(row));
            if (settlement.Rows.Count == 0)
                _rows.Children.Add(new TextBlock { Text = "Im offenen Zeitraum gibt es noch keine Bons, offenen Tische oder Stornos.", Opacity = 0.78 });
            _totals.Text = $"Summe Bar {Formatting.Money(settlement.TotalCashCents)} · Summe Karte {Formatting.Money(settlement.TotalCardCents)}";
        }
        catch (Exception ex)
        {
            _rows.Children.Clear();
            _rows.Children.Add(new TextBlock { Text = "Kellnerabrechnung konnte nicht geladen werden: " + ex.Message, TextWrapping = TextWrapping.Wrap });
        }
    }

    private static Control RowCard(RestaurantWaiterSettlementRow row) => new Border
    {
        Padding = new Thickness(14),
        CornerRadius = new CornerRadius(10),
        Background = new SolidColorBrush(Color.Parse("#101925")),
        Child = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = row.Waiter, FontSize = 18, FontWeight = FontWeight.Bold },
                new TextBlock { Text = $"Bons: {row.SaleCount} · Umsatz {Formatting.Money(row.SalesCents)} · Storno/Retoure {Formatting.Money(row.StornoReturnCents)}" },
                new TextBlock { Text = $"Bar {Formatting.Money(row.CashCents)} · Karte {Formatting.Money(row.CardCents)}" },
                new TextBlock { Text = $"Offene Tische: {row.OpenTables} · {Formatting.Money(row.OpenTablesCents)}" },
                new TextBlock { Text = $"Stornierte Tischpositionen: {row.CancelledPositions} · {Formatting.Money(row.CancelledPositionsCents)}", Opacity = 0.85 }
            }
        }
    };
}
