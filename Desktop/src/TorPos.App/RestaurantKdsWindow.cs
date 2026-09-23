using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.App;

public sealed class RestaurantKdsWindow : Window
{
    private readonly RestaurantEntitlementService _entitlements;
    private readonly RestaurantKitchenOutbox _kitchen;
    private readonly AuthenticatedUser _user;

    private readonly ComboBox _station = new()
    {
        MinWidth = 220,
        MinHeight = 42
    };

    private readonly StackPanel _board = new()
    {
        Spacing = 10
    };

    private readonly TextBlock _status = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.75
    };

    public RestaurantKdsWindow(
        RestaurantEntitlementService entitlements,
        RestaurantKitchenOutbox kitchen,
        AuthenticatedUser user)
    {
        _entitlements = entitlements;
        _kitchen = kitchen;
        _user = user;

        _entitlements.Require(
            RestaurantFeature.KitchenDisplaySystem);

        Title = "TOR Restaurant Plus · KDS";
        Width = 1280;
        Height = 800;
        MinWidth = 1024;
        MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(
            Color.Parse("#0D1420"));

        _station.ItemsSource = new[]
        {
            "ALLE",
            KitchenStations.Grill,
            KitchenStations.Fritteuse,
            KitchenStations.Getraenke
        };
        _station.SelectedIndex = 0;
        _station.SelectionChanged += async (_, _) =>
            await ReloadAsync();

        var refresh = new Button
        {
            Content = "AKTUALISIEREN",
            MinHeight = 42,
            MinWidth = 150
        };
        refresh.Click += async (_, _) =>
            await ReloadAsync();

        var header = new Grid
        {
            ColumnDefinitions =
                new ColumnDefinitions("*,Auto,Auto"),
            Children =
            {
                new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "KITCHEN DISPLAY SYSTEM",
                            FontSize = 26,
                            FontWeight = FontWeight.Bold
                        },
                        new TextBlock
                        {
                            Text = "TOR Restaurant Plus · Offene Küchenpositionen",
                            Opacity = 0.7
                        }
                    }
                },
                new Border
                {
                    [Grid.ColumnProperty] = 1,
                    Margin = new Thickness(8,0),
                    Child = _station
                },
                new Border
                {
                    [Grid.ColumnProperty] = 2,
                    Child = refresh
                }
            }
        };

        Content = new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new Border
                {
                    [DockPanel.DockProperty] = Dock.Top,
                    Padding = new Thickness(14),
                    Margin = new Thickness(0,0,0,12),
                    Background = new SolidColorBrush(
                        Color.Parse("#101925")),
                    CornerRadius = new CornerRadius(12),
                    Child = header
                },
                new Border
                {
                    [DockPanel.DockProperty] = Dock.Bottom,
                    Padding = new Thickness(10),
                    Child = _status
                },
                new ScrollViewer
                {
                    VerticalScrollBarVisibility =
                        Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    Content = _board
                }
            }
        };

        Opened += async (_, _) =>
            await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        var selected =
            Convert.ToString(_station.SelectedItem) ?? "ALLE";
        var station =
            string.Equals(
                selected,
                "ALLE",
                StringComparison.OrdinalIgnoreCase)
                ? ""
                : selected;

        var items = await _kitchen.BoardAsync(
            station);

        _board.Children.Clear();

        foreach (var item in items)
            _board.Children.Add(CreateCard(item));

        _status.Text =
            items.Count == 0
                ? "Keine offenen Küchenpositionen."
                : $"{items.Count} offene Küchenposition(en) · " +
                  DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");
    }

    private Control CreateCard(
        RestaurantKitchenBoardItem item)
    {
        var age = DateTimeOffset.Now - item.AddedAt;
        var ageText =
            age.TotalMinutes < 1
                ? "< 1 Min."
                : $"{(int)age.TotalMinutes} Min.";

        var title = new TextBlock
        {
            Text =
                $"{item.TableName} · {item.Quantity:0.###} × {item.ProductName}",
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap
        };

        var detail = new TextBlock
        {
            Text =
                $"Kellner: {item.Waiter} · " +
                $"Station: {KitchenStations.DisplayName(item.Station)} · " +
                $"Wartezeit: {ageText}",
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap
        };

        var status = new TextBlock
        {
            Text = item.Status.Replace('_', ' '),
            FontWeight = FontWeight.Bold,
            FontSize = 16
        };

        var work = new Button
        {
            Content = "IN ARBEIT",
            MinHeight = 42,
            MinWidth = 120,
            IsEnabled =
                item.Status == "OFFEN"
        };

        var done = new Button
        {
            Content = "FERTIG",
            MinHeight = 42,
            MinWidth = 120,
            IsEnabled =
                item.Status != "FERTIG"
        };

        work.Click += async (_, _) =>
        {
            await _kitchen.SetItemStatusAsync(
                item.SessionItemId,
                "IN_ARBEIT",
                _user.Username);
            await ReloadAsync();
        };

        done.Click += async (_, _) =>
        {
            await _kitchen.SetItemStatusAsync(
                item.SessionItemId,
                "FERTIG",
                _user.Username);
            await ReloadAsync();
        };

        return new Border
        {
            Padding = new Thickness(14),
            Background = new SolidColorBrush(
                Color.Parse(
                    item.Status switch
                    {
                        "FERTIG" => "#173A32",
                        "IN_ARBEIT" => "#4A3512",
                        _ => "#142338"
                    })),
            BorderBrush = new SolidColorBrush(
                Color.Parse("#31526C")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = new Grid
            {
                ColumnDefinitions =
                    new ColumnDefinitions("*,Auto"),
                Children =
                {
                    new StackPanel
                    {
                        Spacing = 5,
                        Children =
                        {
                            title,
                            detail,
                            status
                        }
                    },
                    new StackPanel
                    {
                        [Grid.ColumnProperty] = 1,
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = { work, done }
                    }
                }
            }
        };
    }
}
