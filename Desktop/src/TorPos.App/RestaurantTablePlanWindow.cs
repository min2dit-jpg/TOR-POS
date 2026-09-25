using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.App;

public sealed class RestaurantTablePlanWindow : Window
{
    private readonly RestaurantWorkspaceControl _workspace;
    private Func<Window, Task>? _openMasterDataAsync;

    public Func<Window, Task>? OpenMasterDataAsync
    {
        get => _openMasterDataAsync;
        set
        {
            _openMasterDataAsync = value;
            _workspace.OpenMasterDataAsync =
                value is null ? null : () => value(this);
        }
    }

    public bool ThekeRequested { get; private set; }

    public RestaurantTablePlanWindow(
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
        Title = "TOR Restaurant · Tischplan";
        Width = 1280;
        Height = 700;
        MinWidth = 1024;
        MinHeight = 580;
        WindowState = WindowState.Maximized;
        Background = new SolidColorBrush(Color.Parse("#0D1420"));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _workspace = new RestaurantWorkspaceControl(
            restaurant,
            restaurantFiscal,
            kitchen,
            kitchenDispatcher,
            catalog,
            settings,
            controlledActions,
            user,
            receiptPrinter);

        _workspace.CheckoutRequestedAsync = draft =>
        {
            Close(draft);
            return Task.CompletedTask;
        };
        _workspace.CounterRequestedAsync = () =>
        {
            ThekeRequested = true;
            Close();
            return Task.CompletedTask;
        };

        var close = new Button
        {
            Name = "TablePlanClose",
            Content = "SCHLIESSEN",
            MinHeight = 44,
            Margin = new Thickness(18, 0, 18, 12),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        close.Click += (_, _) => Close();

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto")
        };
        root.Children.Add(_workspace);
        Grid.SetRow(close, 1);
        root.Children.Add(close);
        Content = root;

        Opened += async (_, _) => await _workspace.InitializeAsync();
        Closing += (_, e) =>
        {
            if (_workspace.IsBusy)
                e.Cancel = true;
        };
    }
}
