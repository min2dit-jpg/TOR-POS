using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.App;

public sealed class RestaurantTablePlanWindow : Window
{
    private readonly RestaurantRepository _restaurant;
    private readonly IProductCatalog _catalog;
    private readonly AuthenticatedUser _user;

    private readonly WrapPanel _tablePanel = new()
    {
        Orientation = Orientation.Horizontal,
        ItemWidth = 150,
        ItemHeight = 105
    };

    private readonly TextBlock _detailTitle = new()
    {
        FontSize = 25,
        FontWeight = FontWeight.Bold
    };

    private readonly TextBlock _detailStatus = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.78
    };

    private readonly ListBox _items = new()
    {
        MinHeight = 260
    };

    private readonly ComboBox _product = new()
    {
        MinWidth = 260,
        MinHeight = 44,
        PlaceholderText = "Artikel auswählen"
    };

    private readonly NumericUpDown _quantity = new()
    {
        Minimum = 1,
        Maximum = 99,
        Value = 1,
        Increment = 1,
        Width = 100,
        MinHeight = 44
    };

    private readonly Button _open = new()
    {
        Content = "TISCH ÖFFNEN",
        MinHeight = 48,
        FontWeight = FontWeight.Bold
    };

    private readonly Button _add = new()
    {
        Content = "POSITION HINZUFÜGEN",
        MinHeight = 48,
        FontWeight = FontWeight.Bold,
        IsEnabled = false
    };

    private IReadOnlyList<RestaurantTable> _tables = Array.Empty<RestaurantTable>();
    private RestaurantTable? _selectedTable;
    private RestaurantTableSession? _selectedSession;

    public RestaurantTablePlanWindow(
        RestaurantRepository restaurant,
        IProductCatalog catalog,
        AuthenticatedUser user)
    {
        _restaurant = restaurant;
        _catalog = catalog;
        _user = user;

        Title = "TOR Restaurant · Tischplan";
        Width = 1320;
        Height = 820;
        MinWidth = 1024;
        MinHeight = 650;
        Background = new SolidColorBrush(Color.Parse("#0D1420"));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var refresh = new Button
        {
            Content = "AKTUALISIEREN",
            MinHeight = 42,
            MinWidth = 150
        };
        refresh.Click += async (_, _) => await ReloadAsync();

        _open.Click += async (_, _) => await OpenSelectedTableAsync();
        _add.Click += async (_, _) => await AddSelectedProductAsync();

        _product.ItemsSource = _catalog.Products
            .Where(x => x.IsActive)
            .OrderBy(x => x.Name)
            .ToArray();
        _product.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Product>(
            (p, _) => new TextBlock
            {
                Text = p is null
                    ? ""
                    : $"{p.Name} · {Formatting.Money(p.BasePriceCents + p.PfandCents)}"
            });

        var left = new DockPanel();
        DockPanel.SetDock(refresh, Dock.Top);
        left.Children.Add(refresh);
        left.Children.Add(new ScrollViewer
        {
            Content = _tablePanel,
            Margin = new Thickness(0, 12, 0, 0),
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        });

        var addRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                _product,
                _quantity,
                _add
            }
        };

        var right = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(18),
            Children =
            {
                new TextBlock
                {
                    Text = "TISCHVORGANG",
                    Foreground = new SolidColorBrush(Color.Parse("#53E0C0")),
                    FontSize = 11,
                    FontWeight = FontWeight.Bold
                },
                _detailTitle,
                _detailStatus,
                _open,
                new TextBlock
                {
                    Text = "Positionen",
                    FontSize = 18,
                    FontWeight = FontWeight.Bold,
                    Margin = new Thickness(0, 8, 0, 0)
                },
                _items,
                addRow
            }
        };

        Content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("2*,*"),
            Margin = new Thickness(18),
            Children =
            {
                new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#101925")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#31526C")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(14),
                    Child = left
                },
                new Border
                {
                    [Grid.ColumnProperty] = 1,
                    Background = new SolidColorBrush(Color.Parse("#101925")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#31526C")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Child = right
                }
            }
        };

        Opened += async (_, _) =>
        {
            await EnsureStarterTablesAsync();
            await ReloadAsync();
        };
    }

    private async Task EnsureStarterTablesAsync()
    {
        var current = await _restaurant.ListTablesAsync();
        if (current.Count > 0)
            return;

        var inside = await _restaurant.SaveAreaAsync("Innenbereich", 0);
        var terrace = await _restaurant.SaveAreaAsync("Terrasse", 1);

        for (var i = 1; i <= 8; i++)
        {
            await _restaurant.SaveTableAsync(
                inside,
                $"I{i:00}",
                $"Tisch {i}",
                seats: i <= 4 ? 4 : 6,
                sortOrder: i);
        }

        for (var i = 1; i <= 4; i++)
        {
            await _restaurant.SaveTableAsync(
                terrace,
                $"T{i:00}",
                $"Terrasse {i}",
                seats: 4,
                sortOrder: i);
        }
    }

    private async Task ReloadAsync()
    {
        _tables = await _restaurant.ListTablesAsync();
        _tablePanel.Children.Clear();

        foreach (var table in _tables)
        {
            var session = await _restaurant.GetLiveSessionForTableAsync(table.Id);
            var button = CreateTableButton(table, session);
            _tablePanel.Children.Add(button);
        }

        if (_selectedTable is not null)
        {
            _selectedTable = _tables.FirstOrDefault(x => x.Id == _selectedTable.Id);
            await RefreshDetailAsync();
        }
        else
        {
            _detailTitle.Text = "Tisch auswählen";
            _detailStatus.Text = "Links einen Tisch wählen.";
            _open.IsEnabled = false;
            _add.IsEnabled = false;
            _items.ItemsSource = Array.Empty<string>();
        }
    }

    private Button CreateTableButton(
        RestaurantTable table,
        RestaurantTableSession? session)
    {
        var isOpen = session is not null;
        var text = isOpen
            ? $"{table.DisplayName}\nBELEGT\n{session!.AssignedWaiter}"
            : $"{table.DisplayName}\nFREI\n{table.Seats} Plätze";

        var button = new Button
        {
            Content = text,
            Tag = table,
            Width = 142,
            Height = 96,
            Margin = new Thickness(5),
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(
                Color.Parse(isOpen ? "#5A3A10" : "#173A32")),
            BorderBrush = new SolidColorBrush(
                Color.Parse(isOpen ? "#E3A62F" : "#53E0C0")),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(10)
        };

        button.Click += async (_, _) =>
        {
            _selectedTable = table;
            await RefreshDetailAsync();
        };

        return button;
    }

    private async Task RefreshDetailAsync()
    {
        if (_selectedTable is null)
            return;

        _selectedSession = await _restaurant.GetLiveSessionForTableAsync(
            _selectedTable.Id);

        _detailTitle.Text = _selectedTable.DisplayName;

        if (_selectedSession is null)
        {
            _detailStatus.Text =
                $"FREI · {_selectedTable.Seats} Plätze\n" +
                "Noch kein offener Tischvorgang.";
            _open.IsVisible = true;
            _open.IsEnabled = true;
            _add.IsEnabled = false;
            _items.ItemsSource = Array.Empty<string>();
            return;
        }

        var currentItems = await _restaurant.ListActiveItemsAsync(
            _selectedSession.Id);

        var total = currentItems.Sum(x => x.LineTotalCents);

        _detailStatus.Text =
            $"BELEGT · {_selectedSession.GuestCount} Gäste · " +
            $"Kellner: {_selectedSession.AssignedWaiter}\n" +
            $"Geöffnet: {_selectedSession.OpenedAt.ToLocalTime():dd.MM.yyyy HH:mm} · " +
            $"Version {_selectedSession.Version} · Summe {Formatting.Money(total)}";

        _items.ItemsSource = currentItems
            .Select(x =>
                $"{x.Quantity:0.###} × {x.ProductName} · " +
                $"{Formatting.Money(x.LineTotalCents)}")
            .ToArray();

        _open.IsVisible = false;
        _open.IsEnabled = false;
        _add.IsEnabled = true;
    }

    private async Task OpenSelectedTableAsync()
    {
        if (_selectedTable is null)
            return;

        try
        {
            _selectedSession = await _restaurant.OpenTableAsync(
                _selectedTable.Id,
                _user.Username,
                guestCount: 1,
                deviceId: Environment.MachineName);

            await ReloadAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    private async Task AddSelectedProductAsync()
    {
        if (_selectedSession is null ||
            _product.SelectedItem is not Product product)
        {
            return;
        }

        try
        {
            var quantity = Convert.ToDecimal(_quantity.Value ?? 1m);

            await _restaurant.AddItemAsync(
                _selectedSession.Id,
                _selectedSession.Version,
                product,
                quantity,
                _user.Username,
                Environment.MachineName);

            _selectedSession = await _restaurant.GetSessionAsync(
                _selectedSession.Id);

            _quantity.Value = 1;
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
            await ReloadAsync();
        }
    }

    private async Task ShowErrorAsync(string message)
    {
        var close = new Button
        {
            Content = "OK",
            MinWidth = 100,
            MinHeight = 40
        };

        var dialog = new Window
        {
            Title = "TOR Restaurant",
            Width = 460,
            Height = 220,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        close.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 18,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap
                },
                close
            }
        };

        await dialog.ShowDialog(this);
    }
}
