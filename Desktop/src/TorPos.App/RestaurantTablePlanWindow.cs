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
    private readonly RestaurantFiscalOrderService _restaurantFiscal;
    private readonly RestaurantKitchenOutbox _kitchen;
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
        MinHeight = 260,
        SelectionMode = SelectionMode.Multiple
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

    private readonly ComboBox _targetTable = new()
    {
        MinWidth = 220,
        MinHeight = 44,
        PlaceholderText = "Zieltisch auswählen"
    };

    private readonly Button _move = new()
    {
        Content = "UMBUCHEN",
        MinHeight = 44,
        IsEnabled = false
    };

    private readonly Button _merge = new()
    {
        Content = "ZUSAMMENLEGEN",
        MinHeight = 44,
        IsEnabled = false
    };

    private readonly Button _closeEmpty = new()
    {
        Content = "LEEREN TISCH SCHLIESSEN",
        MinHeight = 44,
        IsEnabled = false
    };

    private readonly Button _split = new()
    {
        Content = "RECHNUNG TEILEN",
        MinHeight = 44,
        FontWeight = FontWeight.Bold,
        IsEnabled = false
    };

    private readonly Button _checkoutSelected = new()
    {
        Content = "AUSGEWÄHLTE POSITIONEN KASSIEREN",
        MinHeight = 48,
        FontWeight = FontWeight.Bold,
        IsEnabled = false
    };

    private readonly Button _cancelItem = new()
    {
        Content = "POSITION STORNIEREN",
        MinHeight = 44,
        IsEnabled = false
    };

    private IReadOnlyList<RestaurantTable> _tables = Array.Empty<RestaurantTable>();
    private RestaurantTable? _selectedTable;
    private RestaurantTableSession? _selectedSession;

    public RestaurantTablePlanWindow(
        RestaurantRepository restaurant,
        RestaurantFiscalOrderService restaurantFiscal,
        RestaurantKitchenOutbox kitchen,
        IProductCatalog catalog,
        AuthenticatedUser user)
    {
        _restaurant = restaurant;
        _restaurantFiscal = restaurantFiscal;
        _kitchen = kitchen;
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
        _move.Click += async (_, _) => await MoveSelectedSessionAsync();
        _merge.Click += async (_, _) => await MergeSelectedSessionAsync();
        _closeEmpty.Click += async (_, _) => await CloseEmptySelectedSessionAsync();
        _split.Click += async (_, _) => await ShowSplitPreviewAsync();
        _checkoutSelected.Click += async (_, _) => await CheckoutSelectedAsync();
        _cancelItem.Click += async (_, _) => await CancelSelectedItemAsync();
        _items.SelectionChanged += (_, _) =>
        {
            _cancelItem.IsEnabled =
                _selectedSession is not null &&
                _selectedSession.State == RestaurantTableSessionState.Open &&
                _items.SelectedItems?.Count == 1;
        };

        _product.ItemsSource = _catalog.Products
            .Where(x => x.IsActive)
            .OrderBy(x => x.Name)
            .ToArray();
        _items.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<RestaurantSessionItem>(
            (item, _) => new TextBlock
            {
                Text = item is null
                    ? ""
                    : $"{item.Quantity:0.###} × {item.ProductName} · {Formatting.Money(item.LineTotalCents)}",
                FontSize = 15,
                Margin = new Thickness(6)
            });

        _product.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Product>(
            (p, _) => new TextBlock
            {
                Text = p is null
                    ? ""
                    : $"{p.Name} · {Formatting.Money(p.BasePriceCents + p.PfandCents)}"
            });

        _targetTable.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<RestaurantTable>(
            (t, _) => new TextBlock
            {
                Text = t is null ? "" : t.DisplayName
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

        var tableActions = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "Tischaktionen",
                    FontSize = 18,
                    FontWeight = FontWeight.Bold
                },
                _targetTable,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _move, _merge }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _split, _closeEmpty }
                },
                _checkoutSelected,
                _cancelItem
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
                addRow,
                tableActions
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
        _targetTable.ItemsSource = _tables
            .Where(x => _selectedTable is null || x.Id != _selectedTable.Id)
            .ToArray();

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
            _move.IsEnabled = false;
            _merge.IsEnabled = false;
            _closeEmpty.IsEnabled = false;
            _split.IsEnabled = false;
            _checkoutSelected.IsEnabled = false;
            _cancelItem.IsEnabled = false;
            _items.ItemsSource = Array.Empty<RestaurantSessionItem>();
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
            _move.IsEnabled = false;
            _merge.IsEnabled = false;
            _closeEmpty.IsEnabled = false;
            _split.IsEnabled = false;
            _checkoutSelected.IsEnabled = false;
            _cancelItem.IsEnabled = false;
            _items.ItemsSource = Array.Empty<RestaurantSessionItem>();
            return;
        }

        var currentItems = await _restaurant.ListActiveItemsAsync(
            _selectedSession.Id);

        var total = currentItems.Sum(x => x.LineTotalCents);
        var paymentLocked =
            _selectedSession.State == RestaurantTableSessionState.CheckRequested;

        _detailStatus.Text =
            (paymentLocked ? "ZAHLUNG OFFEN / PRÜFUNG ERFORDERLICH\n" : "") +
            $"BELEGT · {_selectedSession.GuestCount} Gäste · " +
            $"Kellner: {_selectedSession.AssignedWaiter}\n" +
            $"Geöffnet: {_selectedSession.OpenedAt.ToLocalTime():dd.MM.yyyy HH:mm} · " +
            $"Version {_selectedSession.Version} · Summe {Formatting.Money(total)}";

        _items.ItemsSource = currentItems;

        _open.IsVisible = false;
        _open.IsEnabled = false;
        _add.IsEnabled = !paymentLocked;
        _move.IsEnabled = !paymentLocked;
        _merge.IsEnabled = !paymentLocked;
        _closeEmpty.IsEnabled = !paymentLocked && currentItems.Count == 0;
        _split.IsEnabled = !paymentLocked && currentItems.Count > 0;
        _checkoutSelected.IsEnabled = !paymentLocked && currentItems.Count > 0;
        _cancelItem.IsEnabled = false;
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

        RestaurantFiscalVorgang? fiscalVorgang = null;
        try
        {
            var quantity = Convert.ToDecimal(_quantity.Value ?? 1m);

            fiscalVorgang = await _restaurantFiscal.BeginChangeAsync(
                _selectedSession.Id,
                _user.Username);

            var item = await _restaurant.AddItemAsync(
                _selectedSession.Id,
                _selectedSession.Version,
                product,
                quantity,
                _user.Username,
                Environment.MachineName);

            await _restaurantFiscal.SecureAddedItemAsync(
                _selectedSession.Id,
                item,
                fiscalVorgang,
                _user.Username);

            fiscalVorgang = null;

            _selectedSession = await _restaurant.GetSessionAsync(
                _selectedSession.Id);

            if (_selectedSession is not null)
            {
                await _kitchen.EnqueueNewItemAsync(
                    _selectedSession,
                    item,
                    _selectedTable?.DisplayName ?? "Tisch",
                    _user.Username);
            }

            _quantity.Value = 1;
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            if (fiscalVorgang is not null)
            {
                try
                {
                    await _restaurantFiscal.AbortChangeAsync(
                        fiscalVorgang,
                        _user.Username);
                }
                catch
                {
                    // Original failure remains the operator-facing cause.
                }
            }

            await ShowErrorAsync(ex.Message);
            await ReloadAsync();
        }
    }

    private async Task MoveSelectedSessionAsync()
    {
        if (_selectedSession is null ||
            _targetTable.SelectedItem is not RestaurantTable target)
            return;

        try
        {
            _selectedSession = await _restaurant.MoveSessionToTableAsync(
                _selectedSession.Id,
                _selectedSession.Version,
                target.Id,
                _user.Username,
                Environment.MachineName);

            _selectedTable = target;
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
            await ReloadAsync();
        }
    }

    private async Task MergeSelectedSessionAsync()
    {
        if (_selectedSession is null ||
            _targetTable.SelectedItem is not RestaurantTable target)
            return;

        RestaurantFiscalVorgang? sourceFiscal = null;
        RestaurantFiscalVorgang? targetFiscal = null;

        try
        {
            var targetSession = await _restaurant.GetLiveSessionForTableAsync(target.Id);
            if (targetSession is null)
            {
                await ShowErrorAsync(
                    "Zum Zusammenlegen muss der Zieltisch bereits geöffnet sein. Für einen freien Zieltisch bitte UMBUCHEN verwenden.");
                return;
            }

            var sourceSecured =
                await _restaurantFiscal.IsCurrentStateSecuredAsync(
                    _selectedSession.Id);
            var targetSecured =
                await _restaurantFiscal.IsCurrentStateSecuredAsync(
                    targetSession.Id);

            if (!sourceSecured || !targetSecured)
            {
                await ShowErrorAsync(
                    "Tische können erst zusammengelegt werden, wenn beide Bestellung/TSE-Stände vollständig gesichert sind.");
                return;
            }

            var movedItems =
                await _restaurant.ListActiveItemsAsync(
                    _selectedSession.Id);

            if (movedItems.Count == 0)
            {
                await ShowErrorAsync(
                    "Der Quelltisch enthält keine offenen Positionen.");
                return;
            }

            sourceFiscal = await _restaurantFiscal.BeginChangeAsync(
                _selectedSession.Id,
                _user.Username);
            targetFiscal = await _restaurantFiscal.BeginChangeAsync(
                targetSession.Id,
                _user.Username);

            var merged = await _restaurant.MergeSessionsAsync(
                _selectedSession.Id,
                _selectedSession.Version,
                targetSession.Id,
                targetSession.Version,
                _user.Username,
                Environment.MachineName);

            await _restaurantFiscal.SecureMergeAsync(
                _selectedSession.Id,
                targetSession.Id,
                movedItems,
                sourceFiscal,
                targetFiscal,
                _user.Username);

            sourceFiscal = null;
            targetFiscal = null;

            _selectedTable = target;
            _selectedSession = merged;
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            if (sourceFiscal is not null)
            {
                try
                {
                    await _restaurantFiscal.AbortChangeAsync(
                        sourceFiscal,
                        _user.Username);
                }
                catch { }
            }

            if (targetFiscal is not null)
            {
                try
                {
                    await _restaurantFiscal.AbortChangeAsync(
                        targetFiscal,
                        _user.Username);
                }
                catch { }
            }

            await ShowErrorAsync(ex.Message);
            await ReloadAsync();
        }
    }

    private async Task CloseEmptySelectedSessionAsync()
    {
        if (_selectedSession is null)
            return;

        try
        {
            await _restaurant.CloseEmptySessionAsync(
                _selectedSession.Id,
                _selectedSession.Version,
                _user.Username,
                Environment.MachineName);

            _selectedSession = null;
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
            await ReloadAsync();
        }
    }

    private async Task CancelSelectedItemAsync()
    {
        if (_selectedSession is null ||
            _items.SelectedItems?.OfType<RestaurantSessionItem>().SingleOrDefault() is not { } selected)
        {
            return;
        }

        RestaurantFiscalVorgang? fiscal = null;

        try
        {
            var secured =
                await _restaurantFiscal.IsCurrentStateSecuredAsync(
                    _selectedSession.Id);

            if (!secured)
            {
                await ShowErrorAsync(
                    "Position kann nicht storniert werden: Bestellung/TSE-Stand stimmt nicht mit dem Tisch überein.");
                return;
            }

            fiscal = await _restaurantFiscal.BeginChangeAsync(
                _selectedSession.Id,
                _user.Username);

            var cancelled = await _restaurant.CancelItemAsync(
                _selectedSession.Id,
                _selectedSession.Version,
                selected.Id,
                _user.Username,
                Environment.MachineName);

            await _restaurantFiscal.SecureCancelledItemAsync(
                _selectedSession.Id,
                cancelled,
                fiscal,
                _user.Username);

            fiscal = null;

            _selectedSession = await _restaurant.GetSessionAsync(
                _selectedSession.Id);

            if (_selectedSession is not null)
            {
                await _kitchen.EnqueueCancellationAsync(
                    _selectedSession,
                    cancelled,
                    _selectedTable?.DisplayName ?? "Tisch",
                    _user.Username);
            }

            await ReloadAsync();
        }
        catch (Exception ex)
        {
            if (fiscal is not null)
            {
                try
                {
                    await _restaurantFiscal.AbortChangeAsync(
                        fiscal,
                        _user.Username);
                }
                catch { }
            }

            await ShowErrorAsync(ex.Message);
            await ReloadAsync();
        }
    }

    private async Task CheckoutSelectedAsync()
    {
        if (_selectedSession is null)
            return;

        var selected = _items.SelectedItems?
            .OfType<RestaurantSessionItem>()
            .ToArray() ?? Array.Empty<RestaurantSessionItem>();

        if (selected.Length == 0)
        {
            await ShowErrorAsync("Bitte mindestens eine Position auswählen.");
            return;
        }

        try
        {
            var selections = selected
                .Select(x => new RestaurantSplitSelection(x.Id, x.QuantityMilli))
                .ToArray();

            var draft = await _restaurant.BuildCheckoutDraftAsync(
                _selectedSession.Id,
                _selectedSession.Version,
                selections);

            Close(draft);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
            await ReloadAsync();
        }
    }

    private async Task ShowSplitPreviewAsync()
    {
        if (_selectedSession is null)
            return;

        var items = await _restaurant.ListActiveItemsAsync(_selectedSession.Id);
        if (items.Count == 0)
            return;

        var total = items.Sum(x => x.LineTotalCents);
        var persons = new NumericUpDown
        {
            Minimum = 2,
            Maximum = 20,
            Value = 2,
            Width = 100,
            MinHeight = 42
        };

        var preview = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 16
        };

        void RefreshPreview()
        {
            var count = Math.Clamp(
                Convert.ToInt32(persons.Value ?? 2m),
                2,
                20);

            var shares = RestaurantSplitCalculator.EqualShares(total, count);
            preview.Text =
                $"Gesamtsumme: {Formatting.Money(total)}\n\n" +
                string.Join(
                    "\n",
                    shares.Select((amount, index) =>
                        $"Person {index + 1}: {Formatting.Money(amount)}"));
        }

        persons.ValueChanged += (_, _) => RefreshPreview();
        RefreshPreview();

        var close = new Button
        {
            Content = "SCHLIESSEN",
            MinWidth = 130,
            MinHeight = 42
        };

        var dialog = new Window
        {
            Title = "TOR Restaurant · Splitrechnung",
            Width = 520,
            Height = 480,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        close.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = "RECHNUNG NACH PERSONEN TEILEN",
                    FontSize = 20,
                    FontWeight = FontWeight.Bold
                },
                new TextBlock
                {
                    Text = "Diese Ansicht berechnet nur die Aufteilung. Es wird noch kein Bon erzeugt und keine TSE-Transaktion abgeschlossen.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.75
                },
                new TextBlock { Text = "Personen" },
                persons,
                preview,
                close
            }
        };

        await dialog.ShowDialog(this);
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
