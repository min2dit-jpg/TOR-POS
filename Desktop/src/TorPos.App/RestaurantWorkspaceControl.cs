using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.App;

public sealed class RestaurantWorkspaceControl : UserControl
{
    private readonly RestaurantRepository _restaurant;
    private readonly IReceiptPrinterService _receiptPrinter;
    private readonly RestaurantFiscalOrderService _restaurantFiscal;
    private readonly RestaurantKitchenOutbox _kitchen;
    private readonly RestaurantKitchenDispatcher _kitchenDispatcher;
    private readonly IProductCatalog _catalog;
    private readonly ISettingsRepository _settings;
    private readonly ControlledPosActionService _controlledActions;
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
        MinHeight = 120,
        MaxHeight = 240,
        SelectionMode = SelectionMode.Multiple
    };

    private readonly ComboBox _product = new()
    {
        MinWidth = 180,
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

    private readonly NumericUpDown _guestCount = new()
    {
        Minimum = 1,
        Maximum = 999,
        Value = 1,
        Width = 110,
        MinHeight = 42,
        IsEnabled = false
    };

    private readonly TextBox _tableNote = new()
    {
        MinHeight = 70,
        MaxLength = 500,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        PlaceholderText = "z. B. Geburtstag, Kinderstuhl …",
        IsEnabled = false
    };

    private readonly Button _saveDetails = new()
    {
        Content = "TISCHDETAILS SPEICHERN",
        MinHeight = 44,
        IsEnabled = false
    };

    private readonly Button _takeOver = new()
    {
        Content = "TISCH ÜBERNEHMEN",
        MinHeight = 44,
        FontWeight = FontWeight.Bold,
        IsEnabled = false,
        IsVisible = false
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

    private readonly Button _interim = new() { Name="InterimBill",Content="ZWISCHENRECHNUNG",MinHeight=44,IsEnabled=false };
    private readonly Button _payAll = new() { Name="TablePayAll",Content="BEZAHLEN",MinHeight=44,IsEnabled=false };
    public Func<Task>? OpenMasterDataAsync { get; set; }
    public Func<RestaurantCheckoutDraft, Task>? CheckoutRequestedAsync { get; set; }
    public Func<Task>? CounterRequestedAsync { get; set; }
    public bool IsBusy => _addingProduct || _selectingTable;
    private readonly WrapPanel _areas = new();
    private long? _selectedArea;
    private bool _selectingTable;
    private bool _addingProduct;
    private readonly WrapPanel _categoryTiles = new();
    private readonly WrapPanel _productTiles = new();
    private long? _selectedCategory;

    private void ShowProductTiles(long? categoryId = null)
    {
        _selectedCategory=categoryId;
        _categoryTiles.Children.Clear(); _productTiles.Children.Clear();
        foreach(var category in _catalog.Categories.Where(c=>_catalog.Products.Any(p=>p.IsActive && p.CategoryId==c.Id)))
        {
            var button=new Button { Content=category.Name,MinHeight=42,Margin=new Thickness(3) };
            button.Click+=(_,_)=>ShowProductTiles(category.Id);_categoryTiles.Children.Add(button);
        }
        foreach(var product in _catalog.Products.Where(p=>p.IsActive && (categoryId is null || p.CategoryId==categoryId)))
        {
            var button=new Button { Content=new TextBlock { Text=product.Name+"\n"+Formatting.Money(product.BasePriceCents+product.PfandCents),TextWrapping=TextWrapping.Wrap },
                Width=150,MinHeight=62,Margin=new Thickness(3),IsEnabled=_selectedSession?.State==RestaurantTableSessionState.Open && !_user.IsTraining && _user.Can(UserPermissions.Sale) };
            button.Click+=async (_,_)=> { _product.SelectedItem=product; await AddSelectedProductAsync(); };
            _productTiles.Children.Add(button);
        }
    }


    private IReadOnlyList<RestaurantTable> _tables = Array.Empty<RestaurantTable>();
    private RestaurantTable? _selectedTable;
    private RestaurantTableSession? _selectedSession;

    public RestaurantWorkspaceControl(
        RestaurantRepository restaurant,
        RestaurantFiscalOrderService restaurantFiscal,
        RestaurantKitchenOutbox kitchen,
        RestaurantKitchenDispatcher kitchenDispatcher,
        IProductCatalog catalog,
        ISettingsRepository settings,
        ControlledPosActionService controlledActions,
        AuthenticatedUser user,
        IReceiptPrinterService receiptPrinter)
    {
        _restaurant = restaurant;
        _restaurantFiscal = restaurantFiscal;
        _kitchen = kitchen;
        _kitchenDispatcher = kitchenDispatcher;
        _catalog = catalog;
        _settings = settings;
        _controlledActions = controlledActions;
        _user = user;
        _receiptPrinter = receiptPrinter;

        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Background = new SolidColorBrush(Color.Parse("#0D1420"));

        var refresh = new Button
        {
            Content = "AKTUALISIEREN",
            MinHeight = 42,
            MinWidth = 150
        };
        refresh.Click += async (_, _) => await ReloadAsync();

        _interim.Click += async (_, _) =>
        {
            if (_selectedSession is null) return;
            _interim.IsEnabled=false;
            try
            {
                var report=await _restaurant.BuildInterimBillAsync(_selectedSession.Id);
                await new RestaurantInterimBillWindow(report,_receiptPrinter,_settings).ShowDialog(RequireOwner());
            }
            catch(Exception ex){await ShowErrorAsync(ex.Message);}
            finally{await ReloadAsync();}
        };
        _payAll.Click += async (_, _) =>
        {
            if (_selectedSession is null) return;
            _items.SelectAll();
            await CheckoutSelectedAsync();
        };
        _open.Click += async (_, _) => await OpenSelectedTableAsync();
        _saveDetails.Click += async (_, _) => await SaveSessionDetailsAsync();
        _takeOver.Click += async (_, _) => await TakeOverSelectedSessionAsync();
        _add.Click += async (_, _) => await AddSelectedProductAsync();
        _move.Click += async (_, _) => await MoveSelectedSessionAsync();
        _merge.Click += async (_, _) => await MergeSelectedSessionAsync();
        _closeEmpty.Click += async (_, _) => await CloseEmptySelectedSessionAsync();
        _split.Click += async (_, _) => await ShowSplitCheckoutAsync();
        _checkoutSelected.Click += async (_, _) => await CheckoutSelectedAsync();
        _cancelItem.Click += async (_, _) => await CancelSelectedItemAsync();
        _items.SelectionChanged += (_, _) =>
        {
            _cancelItem.IsEnabled =
                _user.Can(UserPermissions.ImmediateStorno) &&
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
        var theke = new Button { Name = "ThekeButton", Content = "THEKE", MinHeight = 52,
            HorizontalAlignment = HorizontalAlignment.Stretch, FontWeight = FontWeight.Bold };
        theke.Click += async (_, _) =>
        {
            if (CounterRequestedAsync is not null)
                await CounterRequestedAsync();
        };
        var master = new Button { Content = "STAMMDATEN", MinHeight = 44, IsVisible = _user.Can(UserPermissions.ManageProducts) };
        master.Click += async (_, _) =>
        {
            if (OpenMasterDataAsync is null) return;
            await OpenMasterDataAsync();
            _product.ItemsSource = _catalog.Products.Where(p => p.IsActive).OrderBy(p => p.Name).ToArray();
            await ReloadAsync();
        };
        var navigation = new StackPanel { Spacing = 8, Children = { theke, master, refresh, _areas } };
        DockPanel.SetDock(navigation, Dock.Top);
        left.Children.Add(navigation);
        left.Children.Add(new ScrollViewer
        {
            Content = _tablePanel,
            Margin = new Thickness(0, 12, 0, 0),
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        });

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

        var details = new StackPanel { Spacing=8,Children={
            new TextBlock { Text="Gäste" },_guestCount,new TextBlock { Text="Tischnotiz / Küchenhinweis" },_tableNote,_takeOver,tableActions} };
        var right = new StackPanel
        {
            Spacing=10,Margin=new Thickness(18),Children={_detailTitle,_detailStatus,_open,
                new TextBlock { Text="Warengruppe",FontWeight=FontWeight.Bold },_categoryTiles,
                new ScrollViewer { Content=_productTiles,MaxHeight=240,VerticalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Auto },
                new TextBlock { Text="Menge für den nächsten Artikel" },_quantity,new TextBlock { Text="Offene Positionen",FontWeight=FontWeight.Bold },_items,
                new Expander { Header="Tischdetails / Umbuchen / Rechnung teilen",Content=details }}
        };

        _saveDetails.Name = "TableDetailsSave";
        var detailGrid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        detailGrid.Children.Add(new ScrollViewer { Content = right,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        var footer = new WrapPanel { Margin = new Thickness(12), Children = { _interim, _payAll, _saveDetails } };
        Grid.SetRow(footer, 1);
        detailGrid.Children.Add(footer);
        Content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
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
                    Child = detailGrid
                }
            }
        };

    }

    public async Task InitializeAsync()
    {
        await EnsureStarterTablesAsync();
        await ReloadAsync();
    }

    public Task RefreshAsync() => ReloadAsync();

    private Window RequireOwner() =>
        TopLevel.GetTopLevel(this) as Window
        ?? throw new InvalidOperationException("Restaurant workspace is not attached to a Window.");

    private async Task EnsureStarterTablesAsync()
    {
        var current = await _restaurant.ListAllTablesAsync();
        if (current.Count > 0)
            return;

        var inside = await _restaurant.SaveAreaAsync("Gastraum", 0);
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
        _interim.IsEnabled=false; _payAll.IsEnabled=false;
        _tables = await _restaurant.ListTablesAsync();
        var areas = await _restaurant.ListAreasAsync();
        _areas.Children.Clear();
        foreach (var area in areas.Where(a => _tables.Any(t => t.AreaId == a.Id)))
        {
            var areaButton = new Button { Content = area.Name, MinHeight = 44, Margin = new Thickness(2) };
            areaButton.Click += async (_, _) => { _selectedArea = area.Id; await ReloadAsync(); };
            _areas.Children.Add(areaButton);
        }
        if (!_tables.Any(t => t.AreaId == _selectedArea)) _selectedArea = _tables.FirstOrDefault()?.AreaId;
        _tablePanel.Children.Clear();
        _targetTable.ItemsSource = _tables
            .Where(x => _selectedTable is null || x.Id != _selectedTable.Id)
            .ToArray();

        foreach (var table in _tables.Where(t => t.AreaId == _selectedArea))
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
            _selectedSession=null;
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
            _guestCount.IsEnabled = false;
            _tableNote.IsEnabled = false;
            _saveDetails.IsEnabled = false;
            _takeOver.IsVisible = false;
            _takeOver.IsEnabled = false;
            _guestCount.Value = 1;
            _tableNote.Text = "";
            _items.ItemsSource = Array.Empty<RestaurantSessionItem>();
        }
        ShowProductTiles(_selectedCategory);
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
            if (_selectingTable) return;
            _selectingTable = true;
            try
            {
                _selectedTable = table;
                _guestCount.Value = 1;
                _tableNote.Text = "";
                await RefreshDetailAsync();
                if (_selectedSession is null && _user.Can(UserPermissions.Sale) && !_user.IsTraining)
                    await OpenSelectedTableAsync();
            }
            finally { _selectingTable = false; }
        };

        return button;
    }

    private async Task RefreshDetailAsync()
    {
        if (_selectedTable is null)
            return;

        _selectedSession = await _restaurant.GetLiveSessionForTableAsync(
            _selectedTable.Id);
        ShowProductTiles(_selectedCategory);

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
            _guestCount.IsEnabled = true;
            _tableNote.IsEnabled = true;
            _saveDetails.IsEnabled = false;
            _takeOver.IsVisible = false;
            _takeOver.IsEnabled = false;
            if (_guestCount.Value is null || _guestCount.Value < 1)
                _guestCount.Value = 1;
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

        _interim.IsEnabled=currentItems.Count>0;
        _payAll.IsEnabled=!paymentLocked && currentItems.Count>0 && _user.Can(UserPermissions.Sale) && !_user.IsTraining;
        _items.ItemsSource = currentItems;
        _guestCount.Value = _selectedSession.GuestCount;
        _tableNote.Text = _selectedSession.Note;

        _open.IsVisible = false;
        _open.IsEnabled = false;
        _add.IsEnabled = !paymentLocked;
        _move.IsEnabled = !paymentLocked;
        _merge.IsEnabled = !paymentLocked;
        _closeEmpty.IsEnabled = !paymentLocked && currentItems.Count == 0;
        _split.IsEnabled = !paymentLocked && currentItems.Count > 0;
        _checkoutSelected.IsEnabled = !paymentLocked && currentItems.Count > 0;
        _cancelItem.IsEnabled = false;
        _guestCount.IsEnabled = !paymentLocked;
        _tableNote.IsEnabled = !paymentLocked;
        _saveDetails.IsEnabled = !paymentLocked;

        var belongsToCurrentUser = string.Equals(
            _selectedSession.AssignedWaiter,
            _user.Username,
            StringComparison.OrdinalIgnoreCase);

        _takeOver.IsVisible = !belongsToCurrentUser;
        _takeOver.IsEnabled = !paymentLocked && !belongsToCurrentUser;
        _takeOver.Content = belongsToCurrentUser
            ? "IHR TISCH"
            : $"TISCH ÜBERNEHMEN · {_selectedSession.AssignedWaiter}";
    }

    private async Task TakeOverSelectedSessionAsync()
    {
        if (_selectedSession is null)
            return;

        if (string.Equals(
                _selectedSession.AssignedWaiter,
                _user.Username,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            _selectedSession = await _restaurant.ReassignWaiterAsync(
                _selectedSession.Id,
                _selectedSession.Version,
                _user.Username,
                _user.Username,
                Environment.MachineName);

            await ReloadAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
            await ReloadAsync();
        }
    }

    private async Task SaveSessionDetailsAsync()
    {
        if (_selectedSession is null)
            return;

        try
        {
            var guests = Math.Clamp(
                Convert.ToInt32(_guestCount.Value ?? 1m),
                1,
                999);

            var oldNote = _selectedSession.Note;

            _selectedSession = await _restaurant.UpdateSessionDetailsAsync(
                _selectedSession.Id,
                _selectedSession.Version,
                guests,
                _tableNote.Text ?? "",
                _user.Username,
                Environment.MachineName);

            if (!string.Equals(
                    oldNote,
                    _selectedSession.Note,
                    StringComparison.Ordinal) &&
                (await _restaurant.ListActiveItemsAsync(
                    _selectedSession.Id)).Count > 0)
            {
                await _kitchen.EnqueueNoteAsync(
                    _selectedSession,
                    _selectedTable?.DisplayName ?? "Tisch",
                    _user.Username);

                _kitchenDispatcher.Notify();
            }

            await ReloadAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
            await ReloadAsync();
        }
    }

    private async Task OpenSelectedTableAsync()
    {
        if (_selectedTable is null || !_user.Can(UserPermissions.Sale) || _user.IsTraining)
            return;

        try
        {
            var guests = Math.Clamp(
                Convert.ToInt32(_guestCount.Value ?? 1m),
                1,
                999);

            _selectedSession = await _restaurant.OpenTableAsync(
                _selectedTable.Id,
                _user.Username,
                guestCount: guests,
                note: _tableNote.Text ?? "",
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
        if (_selectedSession is null || !_user.Can(UserPermissions.Sale) || _user.IsTraining ||
            _product.SelectedItem is not Product product)
        {
            return;
        }

        if (_addingProduct) return;
        _addingProduct=true;
        if (Content is Control content) content.IsEnabled=false;
        RestaurantFiscalVorgang? fiscalVorgang = null;
        RestaurantSessionItem? pendingItem = null;
        RestaurantSessionItem? addedItem = null;

        try
        {
            var secured =
                await _restaurantFiscal.IsCurrentStateSecuredAsync(
                    _selectedSession.Id);

            if (!secured)
            {
                await ShowErrorAsync(
                    "Neue Position kann nicht hinzugefügt werden: Bestellung/TSE-Stand stimmt nicht mit dem Tisch überein.");
                return;
            }

            var quantity = Convert.ToDecimal(_quantity.Value ?? 1m);
            var options=(await _restaurant.Recipes.ListOptionsAsync()).Where(x=>x.IsActive).ToArray();
            var optionText="";
            if(options.Length>0)
            {
                var ingredients=await _restaurant.Recipes.ListIngredientsAsync();
                var recipe=await _restaurant.Recipes.LoadRecipeAsync(product.Id);
                var recipeText=string.Join("\n",recipe.Select(r=>
                {
                    var ingredient=ingredients.First(i=>i.Id==r.IngredientId);
                    return $"{r.Quantity:0.###} {ingredient.Unit} {ingredient.Name}";
                }));
                var choice=await new RestaurantOrderOptionsWindow(product,options,recipeText).ShowDialog<string?>(RequireOwner());
                if(choice is null) return;
                optionText=choice;
            }

            fiscalVorgang = await _restaurantFiscal.BeginChangeAsync(
                _selectedSession.Id,
                _user.Username);

            pendingItem = await _restaurant.AddItemAsync(
                _selectedSession.Id,
                _selectedSession.Version,
                product,
                quantity,
                _user.Username,
                Environment.MachineName,
                orderOptions: optionText);
            addedItem = pendingItem;

            await _restaurantFiscal.SecureAddedItemAsync(
                _selectedSession.Id,
                pendingItem,
                fiscalVorgang,
                _user.Username);

            pendingItem = null;
            fiscalVorgang = null;

            _selectedSession = await _restaurant.GetSessionAsync(
                _selectedSession.Id);

            if (_selectedSession is not null &&
                addedItem is not null)
            {
                await _kitchen.EnqueueNewItemAsync(
                    _selectedSession,
                    addedItem,
                    _selectedTable?.DisplayName ?? "Tisch",
                    _user.Username,
                    ResolveKitchenStation(product));
                _kitchenDispatcher.Notify();
            }

            _quantity.Value = 1;
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            var mayDiscardPending = false;

            if (fiscalVorgang is not null)
            {
                try
                {
                    if (pendingItem is not null)
                    {
                        mayDiscardPending =
                            await _restaurantFiscal.AbortPendingAddedItemAsync(
                                fiscalVorgang,
                                pendingItem,
                                _user.Username);
                    }
                    else
                    {
                        await _restaurantFiscal.AbortChangeAsync(
                            fiscalVorgang,
                            _user.Username);
                    }
                }
                catch
                {
                    // Original failure remains the operator-facing cause.
                }
            }

            if (mayDiscardPending && pendingItem is not null)
            {
                try
                {
                    await _restaurant.DiscardPendingItemAsync(
                        pendingItem.SessionId,
                        pendingItem.Id,
                        _user.Username,
                        Environment.MachineName);
                }
                catch
                {
                    // Keep the pending row visible to the secured-state guard.
                }
            }

            await ShowErrorAsync(ex.Message);
            await ReloadAsync();
        }
        finally
        {
            _addingProduct=false;
            if (Content is Control restoredContent) restoredContent.IsEnabled=true;
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

        if (!_user.Can(UserPermissions.ImmediateStorno))
        {
            try
            {
                await WriteRestaurantStornoAuditAsync(
                    Guid.NewGuid().ToString("N"),
                    "DENIED",
                    "MISSING_IMMEDIATE_STORNO",
                    selected,
                    0,
                    0,
                    "desktop_permission_denied");
            }
            catch
            {
            }

            await ShowErrorAsync(
                "Keine Berechtigung für Restaurant-Storno.");
            return;
        }

        var reason = await AskRestaurantStornoReasonAsync(selected);
        if (reason is null)
            return;

        RestaurantFiscalVorgang? fiscal = null;
        var actionId = Guid.NewGuid().ToString("N");
        var auditAuthorized = false;
        var auditApplied = false;
        long beforeTotal = 0;
        long afterTotal = 0;

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

            var activeItems =
                await _restaurant.ListActiveItemsAsync(
                    _selectedSession.Id);
            beforeTotal =
                activeItems.Sum(x => x.LineTotalCents);
            afterTotal =
                Math.Max(
                    0,
                    beforeTotal - selected.LineTotalCents);

            await WriteRestaurantStornoAuditAsync(
                actionId,
                "AUTHORIZED",
                reason,
                selected,
                beforeTotal,
                afterTotal,
                "source=DESKTOP");
            auditAuthorized = true;

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

            await WriteRestaurantStornoAuditAsync(
                actionId,
                "APPLIED",
                reason,
                cancelled,
                beforeTotal,
                afterTotal,
                "source=DESKTOP");
            auditApplied = true;

            _selectedSession = await _restaurant.GetSessionAsync(
                _selectedSession.Id);

            if (_selectedSession is not null)
            {
                var cancelledProduct = _catalog.Products
                    .FirstOrDefault(x => x.Id == cancelled.ProductId);

                await _kitchen.EnqueueCancellationAsync(
                    _selectedSession,
                    cancelled,
                    _selectedTable?.DisplayName ?? "Tisch",
                    _user.Username,
                    ResolveKitchenStation(cancelledProduct));
                _kitchenDispatcher.Notify();
            }

            await ReloadAsync();
        }
        catch (Exception ex)
        {
            if (auditAuthorized && !auditApplied)
            {
                try
                {
                    await WriteRestaurantStornoAuditAsync(
                        actionId,
                        "FAILED",
                        reason,
                        selected,
                        beforeTotal,
                        afterTotal,
                        "source=DESKTOP; error=" + ex.Message);
                }
                catch
                {
                }
            }

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
        if (!_user.Can(UserPermissions.Sale) || _user.IsTraining) return;
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

            if (CheckoutRequestedAsync is not null)
                await CheckoutRequestedAsync(draft);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
            await ReloadAsync();
        }
    }

    private async Task ShowSplitCheckoutAsync()
    {
        if (!_user.Can(UserPermissions.Sale) || _user.IsTraining) return;
        if (_selectedSession is null)
            return;

        var items = await _restaurant.ListActiveItemsAsync(
            _selectedSession.Id);

        if (items.Count == 0)
            return;

        var dialog = new RestaurantSplitCheckoutWindow(
            _restaurant,
            _selectedSession,
            items,
            _selectedTable?.DisplayName ?? "Tisch");

        var draft = await dialog.ShowDialog<RestaurantCheckoutDraft?>(RequireOwner());
        if (draft is not null)
            if (CheckoutRequestedAsync is not null)
                await CheckoutRequestedAsync(draft);
    }

    private async Task<string?> AskRestaurantStornoReasonAsync(
        RestaurantSessionItem item)
    {
        const string fallback =
            "Fehlbuchung|Doppelte Erfassung|Kundenwunsch|Nicht verfügbar|Sonstiger Grund";

        var configured = await _settings.GetAsync(
            "function.storno_reasons",
            fallback);

        var reasons = configured
            .Split(
                new[] { '|', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (reasons.Length == 0)
        {
            reasons = fallback
                .Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .ToArray();
        }

        return await new PosActionReasonWindow(
            "RESTAURANT STORNO",
            $"{item.ProductName} · Menge {item.Quantity:0.###} · {Formatting.Money(item.LineTotalCents)}",
            reasons)
            .ShowDialog<string?>(RequireOwner());
    }

    private Task WriteRestaurantStornoAuditAsync(
        string actionId,
        string phase,
        string reason,
        RestaurantSessionItem item,
        long beforeTotalCents,
        long afterTotalCents,
        string details)
    {
        return _controlledActions.AppendAsync(
            new PosActionLogRequest
            {
                ActionId = actionId,
                Phase = phase,
                Actor = _user.Username,
                RegisterId = Environment.MachineName,
                OperationId =
                    _selectedSession?.Id ??
                    item.SessionId,
                ActionType = "RESTAURANT_POSITION_STORNO",
                Reason = reason,
                EntityType = "RESTAURANT_SESSION_ITEM",
                EntityId = item.Id.ToString(),
                BeforeTotalCents = beforeTotalCents,
                AfterTotalCents = afterTotalCents,
                AmountCents = Math.Max(0, item.LineTotalCents),
                Details =
                    $"product_id={item.ProductId}; product={item.ProductName}; " +
                    $"qty_milli={item.QuantityMilli}; {details}"
            });
    }

    private string ResolveKitchenStation(Product? product)
    {
        if (product is null)
            return KitchenStations.None;

        var category = _catalog.Categories
            .FirstOrDefault(x => x.Id == product.CategoryId);

        return KitchenStations.Normalize(category?.KitchenStation);
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

        await dialog.ShowDialog(RequireOwner());
    }
}
