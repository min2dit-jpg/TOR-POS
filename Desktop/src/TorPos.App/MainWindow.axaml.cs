using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CheckoutApplicationService = TorPos.Application.CheckoutApplicationService;
using CheckoutApplicationDisposition = TorPos.Application.CheckoutApplicationDisposition;
using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.App;

public partial class MainWindow:Window
{
    public event Action? LogoutRequested;

    private readonly IProductCatalog _catalog;
    private readonly IProductRepository _repo;
    private readonly ISaleRepository _sales;
    private readonly IParkedReceiptRepository _parkedReceipts;
    private readonly IDailyClosingGuard _dailyClosingGuard;
    private readonly ICashMovementRepository _cashMovements;
    private readonly IAuditLog _audit;
    private readonly IFiscalComplianceService _compliance;
    private readonly IDsfinvkExportService _dsfinvkExport;
    private readonly DatevKassenbuchAsciiService _datevAscii;
    private readonly DatevKassenarchivService _datevKassenarchiv;
    private readonly ProductImageStore _images;
    private readonly PerformanceCounters _perf;
    private readonly ISettingsRepository _settings;
    private readonly DatabaseBackupService _backup;
    private readonly ITseProvider _tseProvider;
    private readonly IReceiptPrinterService _receiptPrinter;
    private readonly IDigitalReceiptPublisher _digitalReceipts;
    private readonly ICardRefundLockRepository _cardRefundLocks;
    private readonly ICommercialLicenseService _commercialLicense;
    private readonly IAuthenticationService _authentication;
    private readonly BusinessManagementService _management;
    private readonly AuthenticatedUser _currentUser;
    private IReadOnlyDictionary<string,string> _settingsCache=
        new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    private readonly SaleEngine _engine=new();
    private readonly Dictionary<string,Bitmap> _imageCache=new(StringComparer.OrdinalIgnoreCase);

    private long _categoryId;
    private string _scan="";
    private long _lastScan;
    private long _scanStartedAt;
    private readonly DispatcherTimer _scanNoEnterTimer = new();
    private bool _scanProcessing;
    // R176: some HID keyboard-wedge scanners emit KeyDown reliably on the
    // cashier window but no TextInput when no TextBox owns focus. Keep one
    // digit fallback and suppress the matching TextInput if Avalonia emits both.
    private char? _lastScannerKeyDownDigit;
    private long _lastScannerKeyDownDigitAt;
    private long? _activeParkedReceiptId;
    private long? _activeParkNumber;
    // Direct action selected from today's Bon-Historie. The target is consumed
    // immediately by BON STORNO / TEILRETOURE so no second picker is opened.
    private long? _directReceiptActionSaleId;
    // R95: Im-Haus/Außer-Haus VAT toggle (§12 UStG). Only meaningful for
    // IMBISS; reset to the Außer-Haus default at the start of every new
    // customer so a forgotten toggle can never silently carry over.
    private bool _imHaus;
    private FiscalReadinessReport? _fiscalReadiness;
    // R140: public key, algorithm and log time format per TSE serial, for the
    // DSFinV-K QR code on the receipt (read from the TSE's own export, R133).
    private IReadOnlyDictionary<string, TseMasterData> _tseMasterData = new Dictionary<string, TseMasterData>();
    private string _numericEntry = "";
    private decimal? _pendingQuantity;
    private bool _saleAllowed;
    private bool _cashEnabledBySettings = true;
    private bool _cardEnabledBySettings = true;
    // R26: one checkout at a time. Prevents double-submit via touch, mouse or F1/F2.
    private bool _paymentInProgress;
    private readonly ICheckoutJournal _checkoutJournal;
    private readonly CheckoutApplicationService _checkoutApplication;
    private readonly SaleFiscalSigningService _fiscalSigning;
    private readonly OrderFiscalSigningService _orderFiscalSigning;
    private readonly TseFailSafeService _tseFailSafe;
    private readonly ControlledPosActionService _controlledActions;
    private readonly PromotionCampaignService _promotions;
    private readonly IAppWindowFactory _windowFactory;
    private CheckoutOperation? _pendingCheckout;
    private string _operationId = Guid.NewGuid().ToString("N");
    private bool _recoveryFault;
    private Task _recoveryWrite = Task.CompletedTask;
    private TorUpdateManifest? _availableUpdate;
    private long _quickItemSequence = -9_100_000;
    private OrderCustomerDisplayWindow? _orderDisplayWindow;
    private string _orderDisplaySignature = "";
    // R104: genuine customer-facing display (e.g. HP L7010t) - distinct
    // from the IMBISS order/pickup-number board above.
    private CustomerDisplayWindow? _customerDisplayWindow;
    private string _customerDisplaySignature = "";
    // R116: simulation is now "anything that cannot book a real sale", not
    // "no licence installed" - see TorPos.Core.SaleModePolicy.
    private bool IsSimulation => !CanCommitProductionSale();

    private bool CartLocked => _paymentInProgress || _pendingCheckout is not null || _recoveryFault || !_cartRecoveryReady;

    // R28: crash/power-loss protection for the currently open receipt.
    // Disabled until startup recovery has been checked so the constructor's
    // initial empty UpdateCart() cannot erase a recovery file from a crash.
    private bool _cartRecoveryReady;

    // R136: the TSE Vorgang of the cart. AEAO zu § 146a Nr. 2.2.2 - the TSE
    // transaction starts with the first position, not after payment. It ends
    // with payment, order acceptance or parking, or as an aborted Vorgang
    // (AVBelegabbruch) when the cart is emptied. TSE calls run one after the
    // other on _tseVorgangWork, never blocking the cashier.
    private readonly TseVorgangCartTracker _tseVorgang = new();
    private Task _tseVorgangWork = Task.CompletedTask;
    private TseVorgangService? Vorgaenge => _fiscalSigning.Vorgaenge;

    private sealed class OpenCartRecoverySnapshot
    {
        public string OperationId { get; set; } = "";
        public DateTimeOffset SavedAt { get; set; }
        public string OperatorName { get; set; } = "";
        public long DiscountCents { get; set; }
        public long? ActiveParkedReceiptId { get; set; }
        public long? ActiveParkNumber { get; set; }
        public List<CartLine> Lines { get; set; } = new();
        // R95 follow-up: without this, a crash between toggling Im Haus and
        // completing checkout would silently recover the cart items but
        // lose the Im-Haus choice, reverting to Außer Haus.
        public bool ImHaus { get; set; }
        // R136: the TSE Vorgang of the recovered cart continues.
        public string TseVorgangId { get; set; } = "";
        public DateTimeOffset? VorgangStartedAt { get; set; }
        // R143: positions cancelled during capture so far.
        public List<CartLine> CancelledLines { get; set; } = new();
    }

    // Touch-Oberfläche: sichtbare Waren-/Artikel-Tasten werden in den
    // Programmeinstellungen als Spalten x Zeilen konfiguriert.
    private int _categoryPage;
    private int _productPage;

    public MainWindow(
        IProductCatalog catalog,
        IProductRepository repo,
        ISaleRepository sales,
        IParkedReceiptRepository parkedReceipts,
        IDailyClosingGuard dailyClosingGuard,
        ICashMovementRepository cashMovements,
        IAuditLog audit,
        IFiscalComplianceService compliance,
        IDsfinvkExportService dsfinvkExport,
        DatevKassenbuchAsciiService datevAscii,
        DatevKassenarchivService datevKassenarchiv,
        ProductImageStore images,
        PerformanceCounters perf,
        ISettingsRepository settings,
        DatabaseBackupService backup,
        ITseProvider tseProvider,
        IReceiptPrinterService receiptPrinter,
        IDigitalReceiptPublisher digitalReceipts,
        ICardRefundLockRepository cardRefundLocks,
        ICommercialLicenseService commercialLicense,
        IAuthenticationService authentication,
        BusinessManagementService management,
        AuthenticatedUser currentUser,
        ICheckoutJournal checkoutJournal,
        CheckoutApplicationService checkoutApplication,
        ControlledPosActionService controlledActions,
        PromotionCampaignService promotions,
        SaleFiscalSigningService fiscalSigning,
        OrderFiscalSigningService orderFiscalSigning,
        TseFailSafeService tseFailSafe,
        IAppWindowFactory windowFactory)
    {
        InitializeComponent();
        InitializeResponsiveHeader();
        // R105: run on whatever screen the till actually has (desktop
        // monitor, laptop, or a tablet like the HP L7010t) instead of
        // always opening at a fixed 1440x900 - the mostly-proportional
        // ("*") internal layout already adapts to the resulting size, it
        // just never got the chance to fill anything but its own default.
        WindowState = WindowState.Maximized;
        _checkoutJournal = checkoutJournal;
        _checkoutApplication = checkoutApplication;
        _fiscalSigning = fiscalSigning;
        _orderFiscalSigning = orderFiscalSigning;
        _tseFailSafe = tseFailSafe;
        _controlledActions = controlledActions;
        _promotions = promotions;
        _windowFactory = windowFactory;
        _catalog=catalog;_repo=repo;_sales=sales;
        _parkedReceipts=parkedReceipts;_dailyClosingGuard=dailyClosingGuard;
        _cashMovements=cashMovements;_audit=audit;_compliance=compliance;
        _dsfinvkExport=dsfinvkExport;
        _datevAscii=datevAscii;
        _datevKassenarchiv=datevKassenarchiv;
        _images=images;_perf=perf;
        _settings=settings;_backup=backup;_tseProvider=tseProvider;_receiptPrinter=receiptPrinter;
        _digitalReceipts=digitalReceipts;
        _cardRefundLocks=cardRefundLocks;
        _commercialLicense=commercialLicense;_authentication=authentication;_management=management;_currentUser=currentUser;

        BuildCategories();
        ShowCategoryOverview();
        UpdateCart();

        // Scanner events are captured at Window tunnel level. Therefore the cashier
        // never needs to click or focus an EAN input field before scanning.
        AddHandler(
            InputElement.KeyDownEvent,
            OnGlobalScannerKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        // R165: keyboard-wedge scanners reliably produce TextInput because that
        // is the same path a normal TextBox receives. Some devices/layouts do
        // not expose their digits as Key.D0..D9, which made the cashier screen
        // silently ignore scans even though Artikelverwaltung could read them.
        AddHandler(
            InputElement.TextInputEvent,
            OnGlobalScannerTextInput,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        // R177: a keyboard-wedge scanner needs a real text-input focus target.
        // Product management already has one; the cashier screen did not.
        // Re-focus the transparent ScannerCapture after touch/click activity or
        // whenever this window becomes active again after a dialog.
        Activated += (_,_) => FocusScannerCaptureSoon();
        AddHandler(
            InputElement.PointerReleasedEvent,
            (_,_) => FocusScannerCaptureSoon(),
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        _scanNoEnterTimer.IsEnabled = false;
        _scanNoEnterTimer.Tick += async (_,_) =>
        {
            _scanNoEnterTimer.Stop();

            if (_scan.Length < 6 || _scanProcessing)
                return;

            // R177: Enter/Tab is still the fastest completion path, but some
            // HID scanners type the digits correctly while their suffix never
            // reaches Avalonia. A fast numeric burst followed by short idle
            // time is therefore accepted as a complete barcode as well.
            var now = Stopwatch.GetTimestamp();
            var elapsedMs = _scanStartedAt == 0
                ? double.MaxValue
                : Stopwatch.GetElapsedTime(_scanStartedAt, now).TotalMilliseconds;
            var averageGapMs = _scan.Length <= 1
                ? double.MaxValue
                : elapsedMs / (_scan.Length - 1);

            if (averageGapMs > 170)
            {
                _scan = "";
                _scanStartedAt = 0;
                return;
            }

            var code = _scan;
            _scan = "";
            _scanStartedAt = 0;
            ScannerStatus.Text = $"SCAN ERKANNT · {code}";
            ScannerStatus.Foreground = AppTheme.AccentBlue;
            await ProcessBarcodeSafely(code);
        };

        Opened += async (_,_) =>
        {
            using (_perf.Measure("startup.settings"))
                await ReloadSettingsAsync();

            using (_perf.Measure("startup.cart_recovery"))
                await TryRestoreOpenCartAsync();

            _cartRecoveryReady = true;
            // R136: a TSE Vorgang left open by a crash whose cart did not come
            // back is ended as aborted. A training login leaves them for the
            // next regular one, which may still restore its cart.
            // R138: but one whose sale was already booked is no abort - it is
            // ended first with the data of that sale.
            if (!_currentUser.IsTraining)
            {
                var keep = _tseVorgang.VorgangId;
                var actor = _currentUser.Username;
                QueueTseVorgangWork(async v =>
                {
                    await _fiscalSigning.FinishCommittedVorgaengeAsync(actor);
                    await v.AbortOrphansAsync(keep, actor);
                });
            }
            // R126: the button states were last computed while recovery was
            // still pending (CartLocked), and nothing recomputed them once it
            // finished - C, EXTRA and SCHNELLARTIKEL stayed disabled after every
            // start until the cashier happened to change the cart. That is why
            // the same version showed grey keys on one till and live ones on the
            // other: only the till that had already been used looked right.
            RefreshSalesActionState();
            PersistOpenCartRecovery();

            using (_perf.Measure("startup.fiscal_status"))
                await RefreshFiscalStatusAsync();

            using (_perf.Measure("startup.parked_count"))
                await RefreshParkedCountAsync();

            using (_perf.Measure("startup.stock_warning"))
                await RefreshStockWarningAsync();

            _ = CheckForUpdateInBackgroundAsync();
            if (_currentUser.IsTraining)
            {
                await _audit.WriteAsync(
                    _currentUser.Username,
                    "TRAINING_SESSION_STARTED",
                    "TRAINING",
                    "",
                    "No fiscal sale, receipt number, TSE transaction or terminal payment is allowed.");
            }

            FocusScannerCaptureSoon();
        };
    }

    private string CurrentBusinessMode()
    {
        return (InstallationEdition.ReadLocked()
            ?? _settingsCache.GetText("business.mode", "IMBISS"))
            .ToUpperInvariant();
    }

    private int UiInt(string key, int fallback, int min, int max)
    {
        var raw = _settingsCache.GetText(key, fallback.ToString());
        return int.TryParse(raw, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;
    }

    // R25: mode-aware defaults without overriding a customer's explicit UI setup.
    // Only the former TOR default value is translated into a KIOSK/IMBISS default.
    private int ModeUiInt(
        string key,
        int formerDefault,
        int kioskDefault,
        int imbissDefault,
        int min,
        int max)
    {
        var raw = _settingsCache.GetText(key, formerDefault.ToString());
        if (!int.TryParse(raw, out var value))
            value = formerDefault;

        if (value == formerDefault)
            value = CurrentBusinessMode() == "KIOSK" ? kioskDefault : imbissDefault;

        return Math.Clamp(value, min, max);
    }


    private async void OnProductClick(object? sender,RoutedEventArgs e)
    {
        if(sender is Button {Tag:Product p}) await AddProduct(p);
    }

    private async Task AddProduct(Product p)
    {
        try
        {
            if (!RequirePermission(UserPermissions.Sale, "Artikelverkauf"))
                return;

            decimal? weighedKilograms = null;
            if (p.IsWeighted)
            {
                if (p.Variants.Count > 0 || p.IsCombo)
                    throw new InvalidOperationException("Gewichtsartikel dürfen keine Varianten oder Menüs verwenden.");

                // A pending Stück multiplier must never leak into a weight sale.
                ClearNumericInput();
                var weighed = await new WeightEntryWindow(p)
                    .ShowDialog<WeightEntryResult?>(this);
                if (weighed is null)
                    return;

                weighedKilograms = weighed.Kilograms;
            }

            ProductVariant? variant=null;
            if(p.Variants.Count>0)
            {
                variant=await new VariantWindow(p).ShowDialog<ProductVariant?>(this);
                if(variant is null) return;
            }
            if (CartLocked) return;

            MenuComponentSnapshot[] menuComponents = Array.Empty<MenuComponentSnapshot>();
            long? menuUnitPrice = null;
            if (p.IsCombo)
            {
                if (p.ComboItems.Any(x => x.IsChoice))
                {
                    var selected = await new MenuChoiceWindow(p, _catalog.Products)
                        .ShowDialog<MenuChoiceResult?>(this);
                    if (selected is null)
                        return;

                    menuComponents = selected.Components;
                    menuUnitPrice = selected.UnitPriceCents;
                }
                else
                {
                    var selected = new List<MenuComponentSnapshot>();
                    foreach (var item in p.ComboItems.OrderBy(x => x.SortOrder))
                    {
                        var component = _catalog.Products.FirstOrDefault(x => x.Id == item.ComponentProductId)
                            ?? throw new InvalidOperationException(
                                $"Menübestandteil {item.ComponentName} ist nicht mehr aktiv.");
                        selected.Add(new MenuComponentSnapshot(
                            component.Id,
                            component.Name,
                            item.Quantity,
                            component.BasePriceCents,
                            component.VatRate,
                            component.ImHausApplicable,
                            ""));
                    }

                    menuComponents = selected.ToArray();
                }
            }

            PromotionSnapshot? promotion;
            using (_perf.Measure("promotion.resolve"))
            {
                // R174: the campaign service resolves the operating date
                // from the open Z period, so an overnight service does not
                // lose its promotion at 00:00. Weighted articles are now
                // line-level cent exact in CartLine and no longer suppressed.
                promotion =
                    await _promotions.GetBestForProductAsync(
                        p.Id,
                        p.CategoryId);
            }

            // Re-check: a checkout may have started while the promotion lookup
            // above was awaiting, which would make _engine.Add a silent no-op.
            if (CartLocked)
            {
                ScannerStatus.Text = "Warenkorb gesperrt · Artikel wurde nicht hinzugefügt.";
                return;
            }

            var promotionSuppressedByManualDiscount =
                promotion is not null &&
                _engine.DiscountCents > 0 &&
                !_settingsCache.GetBool(
                    "promotion.allow_manual_discount",
                    false);

            if (promotionSuppressedByManualDiscount)
                promotion = null;

            var quantity = weighedKilograms ?? ConsumePendingQuantity();
            _engine.Add(
                p,
                variant,
                quantity,
                promotion,
                menuComponents,
                menuUnitPrice);

            UpdateCart();

            if (promotion is not null)
            {
                ScannerStatus.Text =
                    $"ANGEBOT · {promotion.Name} · -{promotion.DiscountPercent}% · " +
                    $"{promotion.StartDate} bis {promotion.EndDate}";
            }
            else if (promotionSuppressedByManualDiscount)
            {
                ScannerStatus.Text =
                    "Artikel zum Normalpreis hinzugefügt · aktives ANGEBOT wurde wegen vorhandenem manuellem Rabatt nicht gestapelt.";
            }
        }
        catch (Exception ex)
        {
            var errorId = ReportOperationalError("WARENKORB", "Artikel konnte nicht hinzugefügt werden.", ex);
            ScannerStatus.Text = $"FEHLER {errorId} · Artikel nicht hinzugefügt.";
        }
    }

    private void UpdateCart()
    {
        CartList.ItemsSource=_engine.Cart.Select((x,i)=>
        {
            var name =
                $"{i+1}. {x.ProductName}" +
                (string.IsNullOrEmpty(x.VariantName)
                    ? ""
                    : " · " + x.VariantName);

            var menuChoices = x.MenuComponents
                .Where(c => !string.IsNullOrWhiteSpace(c.ChoiceGroup))
                .Select(c => $"{c.ChoiceGroup}: {c.Name}")
                .ToArray();
            if (menuChoices.Length > 0)
                name += " · " + string.Join(" · ", menuChoices);

            if (x.IsWeighted)
            {
                var quantity = WeightedSales.QuantityLabel(x.Quantity);
                if (!x.HasPromotion)
                    return name + "   " +
                        $"{quantity} × {Formatting.Money(x.UnitPriceCents)} / kg = {Formatting.Money(x.LineTotalCents)}";

                return name + "   " +
                    $"{quantity} × {Formatting.Money(x.EffectiveListUnitPriceCents)} / kg → " +
                    $"{Formatting.Money(x.UnitPriceCents)} / kg = {Formatting.Money(x.LineTotalCents)} · " +
                    $"ANGEBOT -{x.PromotionPercent}%";
            }

            if (!x.HasPromotion)
            {
                return name + "   " +
                    $"{x.Quantity:0.##} x {Formatting.Money(x.UnitPriceCents)}";
            }

            return name +
                $"   {x.Quantity:0.##} x " +
                $"{Formatting.Money(x.EffectiveListUnitPriceCents)} → " +
                $"{Formatting.Money(x.UnitPriceCents)} · " +
                $"ANGEBOT -{x.PromotionPercent}%";
        }).ToArray();

        var itemCount = _engine.Cart.Count;
        var quantityTotal = _engine.Cart.Sum(x => x.Quantity);
        var subtotalCents = _engine.Cart.Sum(x => x.LineTotalCents);
        var listSubtotalCents = _engine.Cart.Sum(x => x.ListLineTotalCents);
        var promotionDiscountCents = _engine.Cart.Sum(x => x.PromotionDiscountCents);

        var weighedKg = _engine.Cart.Where(x => x.IsWeighted).Sum(x => x.Quantity);
        var otherQuantity = _engine.Cart.Where(x => !x.IsWeighted).Sum(x => x.Quantity);

        ItemsSummaryText.Text =
            itemCount == 0
                ? ""
                : weighedKg > 0m && otherQuantity > 0m
                    ? $"{itemCount} Positionen · {otherQuantity:0.##} Stk. · {GermanFormat.Number(weighedKg, "0.###")} kg"
                    : weighedKg > 0m
                        ? $"{itemCount} Positionen · {GermanFormat.Number(weighedKg, "0.###")} kg"
                        : $"{itemCount} Artikel · {otherQuantity:0.##} Stk.";

        SubtotalText.Text =
            itemCount == 0
                ? ""
                : promotionDiscountCents > 0
                    ? $"Listenwert: {Formatting.Money(listSubtotalCents)}"
                    : $"Zwischensumme: {Formatting.Money(subtotalCents)}";

        var discounts = new List<string>();

        if (promotionDiscountCents > 0)
            discounts.Add(
                $"Angebot: -{Formatting.Money(promotionDiscountCents)}");

        if (_engine.DiscountCents > 0)
            discounts.Add(
                $"Manueller Rabatt: -{Formatting.Money(_engine.DiscountCents)}");

        DiscountText.Text = string.Join(
            Environment.NewLine,
            discounts);

        TotalText.Text = Formatting.Money(_engine.TotalCents);

        EmptyCartHint.IsVisible = itemCount == 0;
        RefreshSalesActionState();

        // R104: only pushes a non-empty cart - an empty cart here can mean
        // either "no customer yet" (already idle) or "just finished a sale"
        // (ClearCompletedCart runs this right after ShowThankYou) - showing
        // idle unconditionally would immediately overwrite the thank-you/QR
        // screen before the customer has a chance to see it. The window's
        // own internal timer (or the next non-empty cart) handles reverting.
        if (itemCount > 0)
            _customerDisplayWindow?.ShowCart(_engine.Cart, _engine.DiscountCents, _engine.TotalCents);

        TrackTseVorgang();

        if (_cartRecoveryReady)
            PersistOpenCartRecovery();
    }

    /// <summary>
    /// R136: starts the TSE transaction with the first position of a Vorgang
    /// and ends it as aborted when the cart is emptied without payment, order
    /// acceptance or parking. Only a till that records the Vorgang fiscally - a
    /// real booking or a recorded training - starts one.
    /// </summary>
    private void TrackTseVorgang()
    {
        if (Vorgaenge is null || !_cartRecoveryReady)
            return;

        var fiscal = _engine.Cart.Count > 0 && _tseVorgang.VorgangId is null &&
            (!IsSimulation || RecordsTrainingFiscally());
        var action = _tseVorgang.OnCartChanged(_engine.Cart, _engine.DiscountCents, _imHaus, fiscal, DateTimeOffset.Now);
        var actor = _currentUser.Username;
        var training = _currentUser.IsTraining;

        switch (action.Kind)
        {
            case TseVorgangActionKind.Start:
                QueueTseVorgangWork(v => v.StartAsync(action.VorgangId, training, action.StartedAt, actor));
                break;
            case TseVorgangActionKind.Abort:
                // R143: the aborted positions and those cancelled before, as pairs.
                var abortLines = action.Lines.Concat(TseVorgangCartTracker.CancellationPairs(action.CancelledLines)).ToArray();
                QueueTseVorgangWork(v => v.AbortAsync(action.VorgangId, abortLines, action.DiscountCents, actor, actor));
                break;
        }
    }

    private void QueueTseVorgangWork(Func<TseVorgangService, Task> work)
    {
        if (Vorgaenge is not { } vorgaenge)
            return;
        _tseVorgangWork = RunTseVorgangWorkAsync(_tseVorgangWork, () => work(vorgaenge));
    }

    private static async Task RunTseVorgangWorkAsync(Task previous, Func<Task> work)
    {
        await previous;
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            // The TSE service documents its own outages; this is a database or
            // programming fault and must not reach the cashier's flow.
            CrashLog.WriteException("TSE-Vorgang", ex);
        }
    }

    private void AdoptTseVorgang(string vorgangId, DateTimeOffset? startedAt, IReadOnlyList<CartLine>? cancelled = null)
    {
        if (!string.IsNullOrEmpty(vorgangId) && startedAt is { } started)
            _tseVorgang.Adopt(vorgangId, started, _engine.Cart, _engine.DiscountCents, _imHaus, cancelled);
    }

    /// <summary>R136: a parked receipt that is taken up again continues its own Vorgang.</summary>
    private async Task ResumeTseVorgangAsync(ParkedReceipt parked)
    {
        if (Vorgaenge is not { } vorgaenge)
            return;
        try
        {
            await _tseVorgangWork;
            if (await vorgaenge.ResumeParkedAsync(parked.Id) is { } vorgang)
                _tseVorgang.Adopt(vorgang.Id, vorgang.StartedAt, parked.Lines, parked.DiscountCents, parked.ImHaus);
            else
                // R137: an accepted order (or a receipt parked before R136) that
                // is only looked at starts no Vorgang; the first change or the
                // payment does (DSFinV-K 2.7.2).
                _tseVorgang.SetBaseline(_engine.Cart, _engine.DiscountCents);
        }
        catch (Exception ex)
        {
            CrashLog.WriteException("TSE-Vorgang fortsetzen", ex);
        }
    }

    private string OpenCartRecoveryPath()
    {
        // R126: the same folder as before - AppPaths.DataDirectory is
        // %APPDATA%\TOR-POS-Pro - but through AppPaths, so the off-screen
        // snapshot tool can point it at a throwaway folder.
        var dir = AppPaths.DataDirectory;
        // R116: the middle file now covers every simulation state, not just
        // "unlicensed". Filename deliberately unchanged so a cart left open by
        // a crash under the previous build is still found after the update.
        return Path.Combine(dir, _currentUser.IsTraining ? "training-cart-recovery.json" :
            IsSimulation ? "development-cart-recovery.json" : "open-cart-recovery.json");
    }

    private void PersistOpenCartRecovery()
    {
        if (_recoveryFault || _currentUser.IsTraining) return;
        var snapshot = new OpenCartRecoverySnapshot
        {
            OperationId = _operationId, SavedAt=DateTimeOffset.Now, OperatorName=_currentUser.Username,
            DiscountCents=_engine.DiscountCents, ActiveParkedReceiptId=_activeParkedReceiptId,
            ActiveParkNumber=_activeParkNumber, Lines=CheckoutSnapshot.CopyLines(_engine.Cart).ToList(),
            ImHaus=_imHaus, TseVorgangId=_tseVorgang.VorgangId ?? "", VorgangStartedAt=_tseVorgang.StartedAt,
            CancelledLines=CheckoutSnapshot.CopyLines(_tseVorgang.CancelledLines).ToList()
        };
        _recoveryWrite = SaveRecoverySafelyAsync(OpenCartRecoveryPath(),
            snapshot.Lines.Count==0 ? null : JsonSerializer.Serialize(snapshot));
    }

    private async Task SaveRecoverySafelyAsync(string path, string? json)
    {
        try { await RecoveryFiles.WriteAsync(path,json); }
        catch(Exception ex)
        {
            CrashLog.WriteException("MainWindow operation", ex);
            _recoveryFault=true;
            ReportOperationalError("RECOVERY", "Offener Bon konnte nicht gesichert werden.", ex);
            ScannerStatus.Text="SICHERUNG FEHLGESCHLAGEN · Kassieren gesperrt · Admin prüfen";
            RefreshSalesActionState();
        }
    }

    private async Task TryRestoreOpenCartAsync()
    {
        if (_currentUser.IsTraining) return;
        var path=OpenCartRecoveryPath();
        try
        {
            var pending=await _checkoutJournal.GetOpenAsync();
            _pendingCheckout=pending.FirstOrDefault();
            if (_pendingCheckout is not null)
            {
                var saved=_pendingCheckout.Snapshot;
                _operationId=saved.OperationId;
                _engine.Restore(saved.Lines,saved.DiscountCents);
                _activeParkedReceiptId=saved.ParkedReceiptId;
                _imHaus=saved.ImHaus;
                AdoptTseVorgang(saved.TseVorgangId, saved.StartedAt, saved.CancelledLines);
                UpdateCart();
                ScannerStatus.Text="ZAHLUNG OFFEN / UNGEKLÄRT · KASSE → ZAHLUNG PRÜFEN · NICHT ERNEUT KASSIEREN";
                return;
            }
            if (await RecoveryFiles.ReadAsync(path+".blocked") is not null)
                throw new InvalidDataException("Beschädigte Wiederherstellung wartet auf Service-Prüfung.");
            var json=await RecoveryFiles.ReadAsync(path);
            if (json is null) return;
            var snapshot=JsonSerializer.Deserialize<OpenCartRecoverySnapshot>(json)
                ?? throw new InvalidDataException("Leere Wiederherstellung.");
            if (snapshot.Lines is null) throw new InvalidDataException("Bonpositionen fehlen.");
            if (!string.IsNullOrEmpty(snapshot.OperationId) && await _checkoutJournal.FindSaleAsync(snapshot.OperationId) is not null)
            {
                await RecoveryFiles.WriteAsync(path,null);
                ScannerStatus.Text="Vorheriger Verkauf bereits gespeichert · keine erneute Zahlung";
                return;
            }
            _operationId=string.IsNullOrEmpty(snapshot.OperationId)?Guid.NewGuid().ToString("N"):snapshot.OperationId;
            _engine.Restore(snapshot.Lines,snapshot.DiscountCents);
            _activeParkedReceiptId=snapshot.ActiveParkedReceiptId; _activeParkNumber=snapshot.ActiveParkNumber;
            _imHaus=snapshot.ImHaus;
            AdoptTseVorgang(snapshot.TseVorgangId, snapshot.VorgangStartedAt, snapshot.CancelledLines);
            if (string.IsNullOrEmpty(snapshot.OperationId) && snapshot.Lines.Count>0 && !IsSimulation)
            {
                var legacy=CaptureCheckout(PaymentMethod.Card);
                await _checkoutJournal.BeginAsync(legacy);
                await _checkoutJournal.MarkTerminalSubmittedAsync(
                    legacy.OperationId,
                    "Legacy recovery: payment state unavailable");
                await _checkoutJournal.TransitionTerminalAsync(
                    legacy.OperationId,
                    "SENT",
                    "UNKNOWN",
                    "Legacy recovery: payment state unavailable",
                    PaymentTerminalOutcome.Unknown,
                    requestSubmitted: true,
                    terminalCode: "LEGACY_RECOVERY",
                    terminalMessage: "Payment state from legacy cart recovery is unknown");
                _pendingCheckout=await _checkoutJournal.GetAsync(legacy.OperationId);
            }
            UpdateCart();
            ScannerStatus.Text=_pendingCheckout is null ? "OFFENER BON WIEDERHERGESTELLT" : "ALTER BON · Zahlungsstatus zuerst prüfen";
        }
        catch(Exception ex)
        {
            CrashLog.WriteException("MainWindow operation", ex);
            _recoveryFault=true;
            try { await RecoveryFiles.QuarantineAsync(path); } catch(Exception q) { CrashLog.WriteException("Recovery quarantine failed",q); }
            ReportOperationalError("RECOVERY","Wiederherstellung gesperrt; Originaldaten zur Prüfung aufbewahrt.",ex);
            ScannerStatus.Text="RECOVERY FEHLER · Daten aufbewahrt · Service prüfen";
        }
    }

    private void RefreshSalesActionState()
    {
        var hasCart = _engine.Cart.Count > 0;
        ClearButton.IsEnabled=!CartLocked && _currentUser.Can(UserPermissions.Sale);
        EditionActionButton.IsEnabled=!CartLocked && _currentUser.Can(UserPermissions.Sale);
        QuickItemButton.IsEnabled=!CartLocked && _currentUser.Can(UserPermissions.Sale);

        // Payment buttons should visually communicate when checkout is possible.
        // This avoids a dead tap on an empty cart and makes the intended flow obvious.
        CashButton.IsEnabled = !CartLocked && _saleAllowed && _cashEnabledBySettings && hasCart;
        CardButton.IsEnabled = !CartLocked && _saleAllowed && _cardEnabledBySettings && hasCart;
        QuickCheckoutButton.IsEnabled =
            !CartLocked &&
            _saleAllowed &&
            hasCart &&
            (_cashEnabledBySettings || _cardEnabledBySettings);

        QtyPlusButton.IsEnabled = !CartLocked && _currentUser.Can(UserPermissions.Sale) && hasCart;
        QtyMinusButton.IsEnabled = !CartLocked && _currentUser.Can(UserPermissions.Sale) && hasCart;
        ImmediateStornoButton.IsEnabled =
            !CartLocked && _currentUser.Can(UserPermissions.ImmediateStorno) && hasCart;
        DiscountButton.IsEnabled =
            !CartLocked && _currentUser.Can(UserPermissions.Discount) && hasCart;
        var trainingOrderAllowed = _currentUser.IsTraining && GetImbissPickupMode() == "ORDER";
        ParkButton.IsEnabled =
            !CartLocked && (!_currentUser.IsTraining || trainingOrderAllowed) &&
            _currentUser.Can(UserPermissions.ParkReceipts) &&
            hasCart;
    }

    private void FocusScannerCaptureSoon()
    {
        if (MenuHubOverlay.IsVisible || CartLocked)
            return;

        Dispatcher.UIThread.Post(
            () =>
            {
                if (MenuHubOverlay.IsVisible || CartLocked)
                    return;

                // Keep the target empty: barcode data is owned by _scan, not
                // by a visible/editable field. Focus alone is what makes HID
                // keyboard-wedge TextInput reliable.
                ScannerCapture.Text = "";
                ScannerCapture.Focus();
            },
            DispatcherPriority.Background);
    }

    private void OnGlobalScannerKeyDown(object? sender, KeyEventArgs e)
    {
        // R145: the card menu hub replaces the cashier workspace while open.
        // Never let scanner input or cashier shortcuts modify the hidden cart.
        if (MenuHubOverlay.IsVisible)
        {
            _scan = "";
            _scanStartedAt = 0;
            if (e.Key == Key.Escape)
                HideMenuHub();
            e.Handled = true;
            return;
        }

        if (CartLocked)
        {
            _scan = "";
            _scanStartedAt = 0;
            return;
        }

        // R176: keyboard-wedge fallback for the real cashier screen. Product
        // maintenance works because a TextBox has focus; the sale screen often
        // has none, so TextInput is not guaranteed. KeyDown digits are therefore
        // accepted directly. TextInput below deduplicates the same keystroke if
        // the platform emits both events.
        if (Digit(e.Key) is char scannerDigit)
        {
            var scannerNow = Stopwatch.GetTimestamp();
            var scannerGap = _lastScan == 0
                ? 999d
                : Stopwatch.GetElapsedTime(_lastScan, scannerNow).TotalMilliseconds;

            if (scannerGap > 180)
            {
                _scan = "";
                _scanStartedAt = scannerNow;
            }
            else if (_scanStartedAt == 0)
            {
                _scanStartedAt = scannerNow;
            }

            _scan += scannerDigit;
            if (_scan.Length > 32)
                _scan = _scan[^32..];

            _lastScan = scannerNow;
            _lastScannerKeyDownDigit = scannerDigit;
            _lastScannerKeyDownDigitAt = scannerNow;
            e.Handled = true;
            ArmScannerNoSuffixTimer();
            return;
        }

        // Kassierer-Schnelltasten: scanner digits are collected by TextInput
        // (R165), so function keys remain independent from barcode capture.
        // R168: there is one payment entry point. F1/F2 remain accepted as
        // legacy shortcuts, but they now behave exactly like F5/KASSIEREN.
        if ((e.Key == Key.F1 || e.Key == Key.F2 || e.Key == Key.F5) &&
            QuickCheckoutButton.IsEnabled)
        {
            e.Handled = true;
            OnQuickCheckoutClick(QuickCheckoutButton, new RoutedEventArgs());
            return;
        }

        if (e.Key == Key.F3 && ParkButton.IsEnabled)
        {
            e.Handled = true;
            OnParkClick(ParkButton, new RoutedEventArgs());
            return;
        }

        if (e.Key == Key.F4 && ImmediateStornoButton.IsEnabled)
        {
            e.Handled = true;
            OnRemoveClick(ImmediateStornoButton, new RoutedEventArgs());
            return;
        }

        // Scanner suffix can be Enter or Tab. Supporting both matters because
        // many USB/HID scanners are configured with TAB as the terminator.
        if (e.Key is not (Key.Enter or Key.Tab))
            return;

        _scanNoEnterTimer.Stop();

        var now = Stopwatch.GetTimestamp();
        var gap = _lastScan == 0
            ? 999d
            : Stopwatch.GetElapsedTime(_lastScan, now).TotalMilliseconds;
        var maxSuffixGap = Math.Max(
            400,
            _settingsCache.GetInt("scanner.wait_ms", 1000));

        if (_scan.Length >= 6 && gap < maxSuffixGap && !_scanProcessing)
        {
            var code = _scan;
            _scan = "";
            _scanStartedAt = 0;
            e.Handled = true;
            ScannerStatus.Text = $"SCAN ERKANNT · {code}";
            ScannerStatus.Foreground = AppTheme.AccentBlue;
            _ = ProcessBarcodeSafely(code);
        }
        else
        {
            _scan = "";
            _scanStartedAt = 0;
        }
    }

    private void OnGlobalScannerTextInput(object? sender, TextInputEventArgs e)
    {
        if (MenuHubOverlay.IsVisible || CartLocked)
        {
            _scan = "";
            return;
        }

        var text = e.Text ?? "";
        if (text.Length == 0)
            return;

        var now = Stopwatch.GetTimestamp();
        var gap = _lastScan == 0
            ? 999d
            : Stopwatch.GetElapsedTime(_lastScan, now).TotalMilliseconds;

        // A scanner emits characters much faster than a cashier can type them.
        // Reset after a human-speed pause so unrelated keyboard input never
        // becomes part of a barcode.
        if (gap > 180)
        {
            _scan = "";
            _scanStartedAt = now;
        }
        else if (_scanStartedAt == 0)
        {
            _scanStartedAt = now;
        }

        var added = false;
        var hasSuffix = false;
        foreach (var ch in text)
        {
            if (ch is '\r' or '\n' or '\t')
            {
                hasSuffix = true;
                continue;
            }

            if (!char.IsDigit(ch))
                continue;

            // KeyDown may already have appended this exact HID digit. Suppress
            // only the immediate matching TextInput pair; virtual scanners that
            // produce TextInput without KeyDown still use the normal path.
            var duplicateKeyDown =
                text.Length == 1 &&
                _lastScannerKeyDownDigit == ch &&
                _lastScannerKeyDownDigitAt != 0 &&
                Stopwatch.GetElapsedTime(
                    _lastScannerKeyDownDigitAt,
                    now).TotalMilliseconds < 80;

            if (!duplicateKeyDown)
                _scan += ch;

            _lastScannerKeyDownDigit = null;
            _lastScannerKeyDownDigitAt = 0;
            added = true;
        }

        if (!added && !hasSuffix)
            return;

        if (_scan.Length > 32)
            _scan = _scan[^32..];

        _lastScan = now;
        e.Handled = true;

        if (hasSuffix && _scan.Length >= 6 && !_scanProcessing)
        {
            _scanNoEnterTimer.Stop();
            var code = _scan;
            _scan = "";
            _scanStartedAt = 0;
            ScannerStatus.Text = $"SCAN ERKANNT · {code}";
            ScannerStatus.Foreground = AppTheme.AccentBlue;
            _ = ProcessBarcodeSafely(code);
            return;
        }

        ArmScannerNoSuffixTimer();
    }

    private void ArmScannerNoSuffixTimer()
    {
        _scanNoEnterTimer.Stop();

        // R177: always arm an idle fallback. If Enter/Tab arrives, it stops
        // this timer and completes immediately. If the scanner suffix is lost,
        // the fast buffered barcode still reaches ProcessBarcodeSafely.
        var configured = _settingsCache.GetInt("scanner.wait_ms", 180);
        var waitMs = _settingsCache.GetBool("scanner.enter_suffix", true)
            ? Math.Max(220, configured)
            : configured;

        _scanNoEnterTimer.Interval = TimeSpan.FromMilliseconds(
            Math.Clamp(waitMs, 100, 600));
        _scanNoEnterTimer.Start();
    }

    private async Task ProcessBarcodeSafely(string code)
    {
        if (CartLocked || _scanProcessing)
            return;

        _scanProcessing = true;
        try
        {
            await ProcessBarcode(code);
        }
        catch (Exception ex)
        {
            var errorId = ReportOperationalError("SCANNER", "Barcode konnte nicht verarbeitet werden.", ex);
            ScannerStatus.Text = $"FEHLER {errorId} · Scan fehlgeschlagen.";
        }
        finally
        {
            _scanProcessing = false;
        }
    }

    private static char? Digit(Key k)=>k switch
    {
        Key.D0 or Key.NumPad0=>'0',Key.D1 or Key.NumPad1=>'1',Key.D2 or Key.NumPad2=>'2',
        Key.D3 or Key.NumPad3=>'3',Key.D4 or Key.NumPad4=>'4',Key.D5 or Key.NumPad5=>'5',
        Key.D6 or Key.NumPad6=>'6',Key.D7 or Key.NumPad7=>'7',Key.D8 or Key.NumPad8=>'8',
        Key.D9 or Key.NumPad9=>'9',_=>null
    };

    private async Task ProcessBarcode(string code)
    {
        code = code.Trim();
        if (code.Length == 0)
            return;

        var started = Stopwatch.GetTimestamp();

        // The article may have been created/changed in Warenverwaltung after the
        // cashier catalog was loaded. On a miss, refresh once before declaring
        // the EAN unknown so a newly added article scans without an app restart.
        if (!_catalog.TryGetByBarcode(code, out var p) || p is null)
        {
            await _catalog.ReloadAsync();
            _catalog.TryGetByBarcode(code, out p);
        }

        if (p is not null)
        {
            ScannerStatus.Text = $"SCAN OK · {p.Name}";
            ScannerStatus.Foreground = AppTheme.AccentTeal;
            await AddProduct(p);
        }
        else
        {
            ScannerStatus.Text = $"EAN NICHT GEFUNDEN · {code}";
            ScannerStatus.Foreground = AppTheme.WarningAmber;

            if (_settingsCache.GetBool("scanner.unknown_dialog", true))
            {
                if (!_currentUser.Can(UserPermissions.ManageProducts))
                {
                    ScannerStatus.Text =
                        $"EAN NICHT GEFUNDEN · {code} · keine Stammdaten-Berechtigung.";
                    return;
                }

                var saved = await new ProductEditorWindow(
                        _repo,
                        _catalog,
                        _images,
                        _management,
                        _promotions,
                        _currentUser,
                        code)
                    .ShowDialog<bool>(this);

                if (saved)
                {
                    await _catalog.ReloadAsync();
                    BuildCategories();
                    SelectCategory(_categoryId);

                    if (_catalog.TryGetByBarcode(code, out p) && p is not null)
                    {
                        ScannerStatus.Text = $"SCAN OK · {p.Name}";
                        ScannerStatus.Foreground = AppTheme.AccentTeal;
                        await AddProduct(p);
                    }
                }
            }
        }

        var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        PerformanceStatus.Text = $"Barcode {ms:0} ms";
        PerformanceStatus.Foreground =
            ms < 100 ? AppTheme.AccentTeal : AppTheme.WarningAmber;
    }

    private async void OnEanSearchClick(object? sender, RoutedEventArgs e)
    {
        var code = await new EanSearchWindow().ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(code))
            return;

        await ProcessBarcodeSafely(code);
    }

    private async void OnQuickItemClick(object? sender, RoutedEventArgs e)
    {
        if (!RequirePermission(UserPermissions.Sale, "SCHNELLARTIKEL") || CartLocked)
            return;

        try
        {
            var item = await new QuickItemWindow().ShowDialog<QuickItemResult?>(this);
            if (item is null || CartLocked)
                return;

            // Negative technical IDs are intentionally not stock-managed by SaleRepository.
            // Each quick item gets its own in-memory ID so differently named free-price items never merge.
            var productId = _quickItemSequence--;
            var quantity = ConsumePendingQuantity();
            _engine.Add(new Product
            {
                Id = productId,
                Name = item.Name,
                BasePriceCents = item.PriceCents,
                VatRate = item.VatRate,
                PfandCents = 0,
                Unit = "Stück"
            }, quantity: quantity);
            UpdateCart();

            await _audit.WriteAsync(
                _currentUser.Username,
                "QUICK_ITEM_ADDED",
                "CURRENT_CART",
                productId.ToString(),
                $"name={item.Name}; cents={item.PriceCents}; vat={item.VatRate:0.##}; qty={quantity:0.###}");
            ScannerStatus.Text = $"Schnellartikel · {item.Name} · {Formatting.Money(item.PriceCents)} · {item.VatRate:0} % hinzugefügt.";
        }
        catch (Exception ex)
        {
            var errorId = ReportOperationalError("WARENKORB", "Schnellartikel konnte nicht hinzugefügt werden.", ex);
            ScannerStatus.Text = $"FEHLER {errorId} · Schnellartikel nicht hinzugefügt.";
        }
    }

    private void OnNumpadDigitClick(object? sender, RoutedEventArgs e)
    {
        if (!RequirePermission(UserPermissions.Sale, "Nummernblock"))
            return;

        if (sender is not Button { Tag: string digit } ||
            digit.Length != 1 || digit[0] < '0' || digit[0] > '9')
            return;

        // Starting a new number cancels a not-yet-consumed multiplier.
        if (_pendingQuantity is not null)
            _pendingQuantity = null;

        var digitCount = _numericEntry.Count(char.IsDigit);
        if (digitCount >= 6)
            return;

        _numericEntry += digit;
        UpdateNumericInputText();
    }

    private void OnNumpadCommaClick(object? sender, RoutedEventArgs e)
    {
        if (!RequirePermission(UserPermissions.Sale, "Nummernblock"))
            return;

        if (_pendingQuantity is not null)
            _pendingQuantity = null;

        if (_numericEntry.Contains(','))
            return;

        _numericEntry = string.IsNullOrWhiteSpace(_numericEntry)
            ? "0,"
            : _numericEntry + ",";

        UpdateNumericInputText();
    }

    private void OnNumpadMultiplyClick(object? sender, RoutedEventArgs e)
    {
        if (!RequirePermission(UserPermissions.Sale, "MENGE ×"))
            return;

        if (!TryReadNumericQuantity(out var quantity))
        {
            ScannerStatus.Text = "MENGE ×: Bitte zuerst eine Zahl eingeben.";
            return;
        }

        if (quantity <= 0m || quantity > 9999m)
        {
            ScannerStatus.Text = "MENGE ×: Erlaubt sind Werte größer 0 bis 9999.";
            return;
        }

        var selectedIndex = CartList.SelectedIndex;
        if (selectedIndex >= 0 && selectedIndex < _engine.Cart.Count)
        {
            var selectedLine = _engine.Cart[selectedIndex];
            _engine.SetQuantity(selectedIndex, quantity);
            ClearNumericInput();
            UpdateCart();
            ScannerStatus.Text = selectedLine.IsWeighted
                ? $"Gewicht auf {GermanFormat.Number(quantity, "0.###")} kg gesetzt."
                : $"Menge auf {quantity:0.###} gesetzt.";
            return;
        }

        _pendingQuantity = quantity;
        _numericEntry = "";
        UpdateNumericInputText();
        ScannerStatus.Text = $"{quantity:0.###} × vorgemerkt · jetzt Artikel wählen oder scannen.";
    }

    private bool TryReadNumericQuantity(out decimal quantity)
    {
        var normalized = (_numericEntry ?? "").Trim().Replace(',', '.');
        return decimal.TryParse(
            normalized,
            System.Globalization.NumberStyles.AllowDecimalPoint,
            System.Globalization.CultureInfo.InvariantCulture,
            out quantity);
    }

    private decimal ConsumePendingQuantity()
    {
        var quantity = _pendingQuantity ?? 1m;
        _pendingQuantity = null;
        _numericEntry = "";
        UpdateNumericInputText();
        return quantity;
    }

    private void ClearNumericInput()
    {
        _numericEntry = "";
        _pendingQuantity = null;
        UpdateNumericInputText();
    }

    private void UpdateNumericInputText()
    {
        if (_pendingQuantity is decimal pending)
        {
            NumericInputText.Text = $"MENGE: {pending:0.###} ×";
            NumericInputText.Foreground = AppTheme.AccentTeal;
            return;
        }

        if (!string.IsNullOrWhiteSpace(_numericEntry))
        {
            NumericInputText.Text = "EINGABE: " + _numericEntry;
            NumericInputText.Foreground = AppTheme.AccentBlue;
            return;
        }

        NumericInputText.Text = "EINGABE: —";
        NumericInputText.Foreground = new SolidColorBrush(Color.Parse("#6F879D"));
    }

    private void OnQtyPlusClick(object? s,RoutedEventArgs e)
    {
        if (!RequirePermission(UserPermissions.Sale, "+1")) return;
        var index = CartList.SelectedIndex;
        if (index >= 0 && index < _engine.Cart.Count && _engine.Cart[index].IsWeighted)
        {
            ScannerStatus.Text = "Gewichtsartikel: Gewicht über MENGE × ändern oder Artikel erneut wiegen.";
            return;
        }
        _engine.ChangeQuantity(index,1);UpdateCart();
    }
    private async void OnQtyMinusClick(object? s,RoutedEventArgs e)
    {
        if (!RequirePermission(UserPermissions.Sale, "-1")) return;
        var index = CartList.SelectedIndex;
        if (index >= 0 && index < _engine.Cart.Count && _engine.Cart[index].IsWeighted)
        {
            ScannerStatus.Text = "Gewichtsartikel: Gewicht über MENGE × ändern oder Position stornieren.";
            return;
        }
        _engine.ChangeQuantity(index,-1);
        UpdateCart();
        await CancelActiveParkedReceiptIfEmptyAsync("MENGE_MINUS");
    }
    private async void OnRemoveClick(object? s, RoutedEventArgs e)
    {
        if (!RequirePermission(UserPermissions.ImmediateStorno, "SOFORT STORNO"))
        {
            await RecordControlledDeniedAsync(
                "SOFORT_STORNO",
                "MISSING_PERMISSION_OR_LOCK");
            return;
        }

        var index = CartList.SelectedIndex;

        if (index < 0 || index >= _engine.Cart.Count)
        {
            ScannerStatus.Text =
                "SOFORT STORNO: Bitte zuerst eine Position auswählen.";
            return;
        }

        var line = _engine.Cart[index];
        var beforeTotal = _engine.TotalCents;
        var afterSubtotal = Math.Max(
            0,
            _engine.SubtotalCents - line.LineTotalCents);
        var afterTotal = Math.Max(
            0,
            afterSubtotal - _engine.DiscountCents);

        var reason = await AskControlledReasonAsync(
            "SOFORT STORNO",
            "function.storno_reasons",
            "Fehlbuchung|Doppelte Ware|Umtausch|Nicht abgeholt|Bruch",
            $"{line.ProductName} · Menge {line.Quantity} · " +
            $"{Formatting.Money(line.LineTotalCents)}");

        if (reason is null || CartLocked)
            return;

        var actionId = Guid.NewGuid().ToString("N");
        var operationId = _operationId;

        try
        {
            await WriteControlledActionAsync(
                actionId,
                "AUTHORIZED",
                "SOFORT_STORNO",
                reason,
                "CURRENT_CART_LINE",
                line.ProductId.ToString(),
                beforeTotal,
                afterTotal,
                line.LineTotalCents,
                $"product={line.ProductName}; variant={line.VariantName}; " +
                $"qty={line.Quantity}; list_unit_price_cents={line.EffectiveListUnitPriceCents}; " +
                $"unit_price_cents={line.UnitPriceCents}; vat={line.VatRate}; " +
                $"promotion_id={line.PromotionId}; promotion_name={line.PromotionName}; " +
                $"promotion_percent={line.PromotionPercent}; promotion_discount_cents={line.PromotionDiscountCents}; " +
                $"manual_discount_cents={_engine.DiscountCents}",
                operationId);
        }
        catch (Exception ex)
        {
            var errorId = ReportOperationalError(
                "AUDIT",
                "SOFORT STORNO wurde NICHT ausgeführt, weil die unveränderbare Protokollierung fehlgeschlagen ist.",
                ex);
            ScannerStatus.Text =
                $"SOFORT STORNO NICHT AUSGEFÜHRT · Audit-Fehler {errorId}";
            return;
        }

        try
        {
            // If this is the last line of a recalled parked receipt, cancel the
            // durable parked receipt BEFORE clearing the RAM cart.
            if (_activeParkedReceiptId is long parkedId &&
                _engine.Cart.Count == 1)
            {
                // R143: a secured order is cancelled as its own record (R137).
                var securedOrder = await SecuredOrderAsync(parkedId);
                await _parkedReceipts.CancelAsync(
                    parkedId,
                    actor: _currentUser.Username);
                if (securedOrder is not null)
                    await SecureOrderCancellationAsync(securedOrder);

                _activeParkedReceiptId = null;
                _activeParkNumber = null;
                _engine.IsReadOnly = false;
            }

            _engine.RemoveAt(index);
            UpdateCart();
            CartList.SelectedIndex = -1;

            if (_engine.Cart.Count == 0)
            {
                _operationId = Guid.NewGuid().ToString("N");
                PrepareNextCustomer();
                await RefreshParkedCountAsync();
            }

            await WriteControlledActionAsync(
                actionId,
                "APPLIED",
                "SOFORT_STORNO",
                reason,
                "CURRENT_CART_LINE",
                line.ProductId.ToString(),
                beforeTotal,
                afterTotal,
                line.LineTotalCents,
                $"product={line.ProductName}; qty={line.Quantity}",
                operationId);

            ScannerStatus.Text =
                $"SOFORT STORNO protokolliert · {line.ProductName} entfernt.";
        }
        catch (Exception ex)
        {
            try
            {
                await WriteControlledActionAsync(
                    actionId,
                    "FAILED",
                    "SOFORT_STORNO",
                    reason,
                    "CURRENT_CART_LINE",
                    line.ProductId.ToString(),
                    beforeTotal,
                    _engine.TotalCents,
                    line.LineTotalCents,
                    "error=" + ex.Message,
                    operationId);
            }
            catch (Exception auditEx)
            {
                CrashLog.WriteException(
                    "R70 storno FAILED audit could not be written.",
                    auditEx);
            }

            var errorId = ReportOperationalError(
                "SOFORT STORNO",
                "Storno konnte nicht vollständig ausgeführt werden.",
                ex);
            ScannerStatus.Text =
                $"SOFORT STORNO FEHLER · {errorId}";
        }
    }

    private async void OnBonStornoClick(object? sender, RoutedEventArgs e)
    {
        if (!RequirePermission(UserPermissions.ReceiptStorno, "BON STORNO"))
            return;

        if (_currentUser.IsTraining)
        {
            ScannerStatus.Text = "BON STORNO ist im TRAININGSMODUS deaktiviert. Trainingsverkäufe sind ohnehin nicht fiskal.";
            return;
        }

        if (!RequireRealMode("BON STORNO"))
            return;

        await _audit.WriteAsync(
            _currentUser.Username,
            "BON_STORNO_OPENED",
            "SALE",
            "",
            "Bon-Storno-Auswahl über das Hauptpanel geöffnet.");

        long? selectedId = _directReceiptActionSaleId;
        _directReceiptActionSaleId = null;
        if (selectedId is null)
        {
            try
            {
                var selection = await new ReceiptHistoryWindow(_sales, _management, stornoMode: true)
                    .ShowDialog<ReceiptHistorySelection?>(this);
                selectedId = selection?.SaleId;
            }
            catch (Exception ex)
            {
                CrashLog.WriteException("Bon Storno picker", ex);
                ScannerStatus.Text = "BON STORNO: Heutige Bon-Liste konnte nicht geladen werden.";
                return;
            }
        }

        if (selectedId is not long saleId)
            return;

        var original = await _sales.GetByIdAsync(saleId);
        if (original is null)
        {
            ScannerStatus.Text = "BON STORNO: Bon wurde nicht gefunden.";
            return;
        }

        // R107: checked BEFORE the reason prompt and, critically, before
        // any card terminal call - RecordStornoAsync's own equivalent
        // check only runs at the very end, after the terminal has already
        // been asked to refund. Without this early check, retrying BON
        // STORNO on an already-(partially-)reversed sale would charge the
        // terminal AGAIN before the DB ever rejected the write.
        var blockReason = await _sales.CheckReversalAllowedAsync(saleId, forFullStorno: true);
        if (blockReason is not null)
        {
            ScannerStatus.Text = $"BON STORNO ABGEBROCHEN · {blockReason}";
            return;
        }

        var reason = await AskControlledReasonAsync(
            "BON STORNO",
            "function.bon_storno_reasons",
            "Kunde reklamiert|Fehlbon|Doppelte Erfassung|Preisirrtum|Sonstiger Grund",
            $"Bon {original.ReceiptNumber:000000} · {Formatting.Money(original.TotalCents)} · " +
            $"{original.CreatedAt.LocalDateTime:dd.MM.yyyy HH:mm}");

        if (reason is null)
            return;

        var actionId = Guid.NewGuid().ToString("N");
        try
        {
            await WriteControlledActionAsync(
                actionId, "REQUESTED", "SALE_STORNO", reason,
                "SALE", saleId.ToString(),
                original.TotalCents, 0, original.TotalCents,
                $"original_receipt={original.ReceiptNumber:000000}", "");

            // R102: a card component (pure KARTE or the card portion of a
            // Mixed original) must be refunded at the terminal BEFORE the
            // DB reversal is ever recorded - RecordStornoAsync structurally
            // refuses to proceed without evidence of this.
            var cardPortion = original.EffectiveCardPortionCents;
            var cardRefundEvidence = "";
            var cardRefundAttemptId = "";
            if (cardPortion > 0)
            {
                // R106: a durable lock against a duplicate refund attempt -
                // see ICardRefundLockRepository's own doc comment. Checked
                // AND opened before the terminal call, so a crash between
                // "terminal charged" and "DB reversal recorded" still
                // leaves this sale locked, not silently retriable.
                if (await _cardRefundLocks.HasUnresolvedAsync(saleId))
                {
                    ScannerStatus.Text = "BON STORNO ABGEBROCHEN · Für diesen Bon läuft bereits eine ungeklärte Kartenerstattung · Diagnose-Fenster prüfen";
                    return;
                }

                ScannerStatus.Text = "BON STORNO · Karten-Anteil wird am Terminal erstattet …";
                cardRefundAttemptId = await _cardRefundLocks.BeginAsync(saleId, "STORNO", cardPortion);
                var refund = await _checkoutApplication.RefundStornoCardPortionAsync(cardPortion, actionId);
                if (refund is null || refund.Outcome != PaymentTerminalOutcome.Approved)
                {
                    // Definite (not ambiguous) outcome - safe to clear the
                    // lock immediately, nothing to reconcile.
                    if (refund is null || refund.Outcome != PaymentTerminalOutcome.Unknown)
                        await _cardRefundLocks.ClearAsync(cardRefundAttemptId);

                    await WriteControlledActionAsync(
                        actionId, "REJECTED", "SALE_STORNO", reason,
                        "SALE", saleId.ToString(),
                        original.TotalCents, 0, original.TotalCents,
                        $"card_refund_outcome={refund?.Outcome}; message={refund?.Message}", "");

                    ScannerStatus.Text = refund?.Outcome == PaymentTerminalOutcome.Unknown
                        ? $"BON STORNO ANGEHALTEN · Erstattungsstatus unklar · Terminalbeleg prüfen, NICHT erneut versuchen · Bon ist gesperrt bis zur Klärung im Diagnose-Fenster · {refund.Message}"
                        : $"BON STORNO ABGEBROCHEN · Karten-Erstattung nicht bestätigt · {refund?.Message ?? "Terminal nicht erreichbar"}";
                    return;
                }
                // R176: keep the APPROVED refund lock until the matching
                // immutable DB reversal has committed. A crash/failure in
                // between must leave a reconciliation lock, never make the
                // same real-world refund silently retryable.
                cardRefundEvidence = $"outcome=APPROVED; terminal_code={refund.OutcomeCode}; terminal_id={refund.TerminalId}; trace={refund.TraceNumber}";
            }

            var storno = await _sales.RecordStornoAsync(saleId, _currentUser.Username, reason, cardRefundEvidence);
            if (cardRefundAttemptId.Length > 0)
                await _cardRefundLocks.ClearAsync(cardRefundAttemptId);

            await WriteControlledActionAsync(
                actionId, "APPLIED", "SALE_STORNO", reason,
                "SALE", storno.Id.ToString(),
                original.TotalCents, 0, original.TotalCents,
                $"storno_receipt={storno.ReceiptNumber:000000}; original_receipt={original.ReceiptNumber:000000}", "");

            try
            {
                await _fiscalSigning.SignAsync(storno, _currentUser.Username);
            }
            catch (Exception ex)
            {
                CrashLog.WriteException("Fiscal signing storno " + storno.ReceiptNumber, ex);
            }

            if (_settingsCache.GetBool("device.receipt_printer.enabled", false) &&
                _settingsCache.GetBool("receipt.auto_print", true))
            {
                var printerName = _settingsCache.GetText("device.receipt_printer.name", "");
                if (!string.IsNullOrWhiteSpace(printerName))
                {
                    var stornoJob = BuildReceiptPrintJob(
                        storno,
                        storno.PaymentMethod,
                        extraHeaderNote: $"BON STORNO · GEGENBUCHUNG ZU BON {original.ReceiptNumber:000000}");
                    _ = PrintReceiptAndReportAsync(stornoJob, printerName);
                }
            }

            await RefreshStockWarningAsync();

            ScannerStatus.Text =
                $"BON STORNO · Bon {original.ReceiptNumber:000000} storniert · " +
                $"Storno-Bon {storno.ReceiptNumber:000000} · {Formatting.Money(storno.TotalCents)}";
        }
        catch (Exception ex)
        {
            CrashLog.WriteException("Bon Storno", ex);
            var errorId = ReportOperationalError(
                "BON STORNO",
                $"Bon {original.ReceiptNumber:000000} konnte nicht storniert werden: " + ex.Message,
                ex);
            ScannerStatus.Text = $"BON STORNO FEHLGESCHLAGEN · Fehler-ID {errorId}";
        }
    }

    private async void OnPartialReturnClick(object? sender, RoutedEventArgs e)
    {
        // R82: partial Retoure reuses the same permission as BON STORNO -
        // both are "reverse an already-completed sale" actions, and admin
        // already controls ReceiptStorno per staff member.
        if (!RequirePermission(UserPermissions.ReceiptStorno, "TEILRETOURE"))
            return;

        if (_currentUser.IsTraining)
        {
            ScannerStatus.Text = "TEILRETOURE ist im TRAININGSMODUS deaktiviert.";
            return;
        }

        if (!RequireRealMode("TEILRETOURE"))
            return;

        long? selectedId = _directReceiptActionSaleId;
        _directReceiptActionSaleId = null;
        if (selectedId is null)
        {
            try
            {
                var selection = await new ReceiptHistoryWindow(_sales, _management, returnMode: true)
                    .ShowDialog<ReceiptHistorySelection?>(this);
                selectedId = selection?.SaleId;
            }
            catch (Exception ex)
            {
                CrashLog.WriteException("Partial return picker", ex);
                ScannerStatus.Text = "TEILRETOURE: Heutige Bon-Liste konnte nicht geladen werden.";
                return;
            }
        }

        if (selectedId is not long saleId)
            return;

        var original = await _sales.GetByIdAsync(saleId);
        if (original is null)
        {
            ScannerStatus.Text = "TEILRETOURE: Bon wurde nicht gefunden.";
            return;
        }

        // R107: checked before the picker even opens for line selection,
        // and again below before any terminal call - same reasoning as
        // BON STORNO's equivalent check above.
        var blockReason = await _sales.CheckReversalAllowedAsync(saleId, forFullStorno: false);
        if (blockReason is not null)
        {
            ScannerStatus.Text = $"TEILRETOURE ABGEBROCHEN · {blockReason}";
            return;
        }

        var requestedLines = await new PartialReturnWindow(original)
            .ShowDialog<IReadOnlyList<ReturnLineRequest>?>(this);

        if (requestedLines is null || requestedLines.Count == 0)
            return;

        var reason = await AskControlledReasonAsync(
            "TEILRETOURE",
            "function.bon_storno_reasons",
            "Kunde reklamiert|Fehlbon|Doppelte Erfassung|Preisirrtum|Sonstiger Grund",
            $"Bon {original.ReceiptNumber:000000} · {requestedLines.Count} Position(en)");

        if (reason is null)
            return;

        var actionId = Guid.NewGuid().ToString("N");
        try
        {
            await WriteControlledActionAsync(
                actionId, "REQUESTED", "SALE_RETURN", reason,
                "SALE", saleId.ToString(),
                original.TotalCents, 0, 0,
                $"original_receipt={original.ReceiptNumber:000000}; lines={requestedLines.Count}", "");

            // R102: same terminal-refund-before-DB-write orchestration as
            // BON STORNO, but the card portion is proportional to THIS
            // return's own total, not the whole original sale - mirrors
            // exactly what RecordReturnAsync itself recomputes server-side.
            // R106: must prorate the original Bon's manual discount here
            // too (via the SAME shared DiscountProration.Prorate helper
            // RecordReturnAsync itself uses) - otherwise the terminal would
            // be asked to refund the raw, undiscounted amount while the DB
            // records the correctly discounted total, refunding the
            // customer MORE than they actually paid.
            // R176: ask the repository for the authoritative quote BEFORE
            // touching the terminal. It includes earlier partial returns and
            // cumulative-cent allocation, so the card refund and the later DB
            // write cannot disagree by one cent on weighted promotion lines.
            var returnQuote =
                await _sales.QuoteReturnAsync(
                    saleId,
                    requestedLines);
            var returnTotalCents = returnQuote.TotalCents;
            var returnCardPortion = returnQuote.CardPortionCents;

            var cardRefundEvidence = "";
            var cardRefundAttemptId = "";
            if (returnCardPortion > 0)
            {
                // R106: same durable duplicate-refund lock as BON STORNO -
                // scoped to the ORIGINAL sale, so an unresolved Teilretoure
                // refund also blocks a full BON STORNO on the same Bon and
                // vice versa, not just another Teilretoure.
                if (await _cardRefundLocks.HasUnresolvedAsync(saleId))
                {
                    ScannerStatus.Text = "TEILRETOURE ABGEBROCHEN · Für diesen Bon läuft bereits eine ungeklärte Kartenerstattung · Diagnose-Fenster prüfen";
                    return;
                }

                ScannerStatus.Text = "TEILRETOURE · Karten-Anteil wird am Terminal erstattet …";
                cardRefundAttemptId = await _cardRefundLocks.BeginAsync(saleId, "RETURN", returnCardPortion);
                var refund = await _checkoutApplication.RefundStornoCardPortionAsync(returnCardPortion, actionId);
                if (refund is null || refund.Outcome != PaymentTerminalOutcome.Approved)
                {
                    if (refund is null || refund.Outcome != PaymentTerminalOutcome.Unknown)
                        await _cardRefundLocks.ClearAsync(cardRefundAttemptId);

                    await WriteControlledActionAsync(
                        actionId, "REJECTED", "SALE_RETURN", reason,
                        "SALE", saleId.ToString(),
                        original.TotalCents, 0, returnTotalCents,
                        $"card_refund_outcome={refund?.Outcome}; message={refund?.Message}", "");

                    ScannerStatus.Text = refund?.Outcome == PaymentTerminalOutcome.Unknown
                        ? $"TEILRETOURE ANGEHALTEN · Erstattungsstatus unklar · Terminalbeleg prüfen, NICHT erneut versuchen · Bon ist gesperrt bis zur Klärung im Diagnose-Fenster · {refund.Message}"
                        : $"TEILRETOURE ABGEBROCHEN · Karten-Erstattung nicht bestätigt · {refund?.Message ?? "Terminal nicht erreichbar"}";
                    return;
                }
                // R176: keep the APPROVED refund lock until the matching
                // immutable DB reversal has committed. A crash/failure in
                // between must leave a reconciliation lock, never make the
                // same real-world refund silently retryable.
                cardRefundEvidence = $"outcome=APPROVED; terminal_code={refund.OutcomeCode}; terminal_id={refund.TerminalId}; trace={refund.TraceNumber}";
            }

            var returned = await _sales.RecordReturnAsync(saleId, requestedLines, _currentUser.Username, reason, cardRefundEvidence);
            if (cardRefundAttemptId.Length > 0)
                await _cardRefundLocks.ClearAsync(cardRefundAttemptId);

            await WriteControlledActionAsync(
                actionId, "APPLIED", "SALE_RETURN", reason,
                "SALE", returned.Id.ToString(),
                original.TotalCents, original.TotalCents - returned.TotalCents, returned.TotalCents,
                $"return_receipt={returned.ReceiptNumber:000000}; original_receipt={original.ReceiptNumber:000000}", "");

            try
            {
                await _fiscalSigning.SignAsync(returned, _currentUser.Username);
            }
            catch (Exception ex)
            {
                CrashLog.WriteException("Fiscal signing return " + returned.ReceiptNumber, ex);
            }

            if (_settingsCache.GetBool("device.receipt_printer.enabled", false) &&
                _settingsCache.GetBool("receipt.auto_print", true))
            {
                var printerName = _settingsCache.GetText("device.receipt_printer.name", "");
                if (!string.IsNullOrWhiteSpace(printerName))
                {
                    var returnJob = BuildReceiptPrintJob(
                        returned,
                        returned.PaymentMethod,
                        extraHeaderNote: $"TEILRETOURE · GEGENBUCHUNG ZU BON {original.ReceiptNumber:000000}");
                    _ = PrintReceiptAndReportAsync(returnJob, printerName);
                }
            }

            await RefreshStockWarningAsync();

            ScannerStatus.Text =
                $"TEILRETOURE · Bon {original.ReceiptNumber:000000} · " +
                $"Retoure-Bon {returned.ReceiptNumber:000000} · {Formatting.Money(returned.TotalCents)}";
        }
        catch (Exception ex)
        {
            CrashLog.WriteException("Partial return", ex);
            var errorId = ReportOperationalError(
                "TEILRETOURE",
                $"Bon {original.ReceiptNumber:000000}: Retoure konnte nicht gebucht werden: " + ex.Message,
                ex);
            ScannerStatus.Text = $"TEILRETOURE FEHLGESCHLAGEN · Fehler-ID {errorId}";
        }
    }

    private async void OnClearClick(object? sender, RoutedEventArgs e)
    {
        if (!RequirePermission(UserPermissions.Sale, "C / Verkauf leeren"))
        {
            await RecordControlledDeniedAsync(
                "C_CLEAR_CART",
                "MISSING_PERMISSION_OR_LOCK");
            return;
        }

        if (!string.IsNullOrWhiteSpace(_numericEntry) ||
            _pendingQuantity is not null)
        {
            ClearNumericInput();
            ScannerStatus.Text = "Zahleneingabe gelöscht.";
            return;
        }

        if (_engine.Cart.Count == 0)
        {
            ScannerStatus.Text = "Verkaufsfenster ist bereits leer.";
            return;
        }

        var beforeTotal = _engine.TotalCents;
        var positions = _engine.Cart.Count;
        var quantity = _engine.Cart.Sum(x => x.Quantity);
        var operationId = _operationId;
        var parkedId = _activeParkedReceiptId;
        var parkedNumber = _activeParkNumber;

        var reason = await AskControlledReasonAsync(
            "VERKAUF ABBRECHEN",
            "function.cancel_reasons",
            "Kunde abgebrochen|Fehleingabe|Doppelerfassung|Zahlung nicht gewünscht|Sonstiger Grund",
            $"{positions} Position(en) · Menge {quantity} · " +
            $"{Formatting.Money(beforeTotal)}" +
            (parkedNumber is long p ? $" · Parkbon P{p:000000}" : ""));

        if (reason is null || CartLocked)
            return;

        var actionId = Guid.NewGuid().ToString("N");

        try
        {
            await WriteControlledActionAsync(
                actionId,
                "AUTHORIZED",
                "C_CLEAR_CART",
                reason,
                parkedId is null ? "CURRENT_CART" : "PARKED_RECEIPT",
                parkedId?.ToString() ?? "",
                beforeTotal,
                0,
                beforeTotal,
                $"positions={positions}; quantity={quantity}; " +
                $"discount_cents={_engine.DiscountCents}; parked_number={parkedNumber}",
                operationId);
        }
        catch (Exception ex)
        {
            var errorId = ReportOperationalError(
                "AUDIT",
                "Verkauf wurde NICHT abgebrochen, weil die unveränderbare Protokollierung fehlgeschlagen ist.",
                ex);
            ScannerStatus.Text =
                $"ABBRUCH NICHT AUSGEFÜHRT · Audit-Fehler {errorId}";
            return;
        }

        try
        {
            // R137: an order the TSE has secured is cancelled as its own
            // Bestellung-V1 record with reversed sign (DSFinV-K 4.2.3), not as an
            // aborted cart. Read before the cancellation closes it.
            var securedOrder = parkedId is long orderId && SecuresVorgaengeFiscally()
                ? await _parkedReceipts.GetOpenByIdAsync(orderId, training: _currentUser.IsTraining)
                : null;
            if (securedOrder is not null && !await _orderFiscalSigning.IsSecuredAsync(securedOrder))
                securedOrder = null;

            // Durable parked receipt cancellation happens before the in-memory
            // cart is cleared. A DB failure therefore leaves the cashier's cart intact.
            if (parkedId is long id)
            {
                await _parkedReceipts.CancelAsync(
                    id,
                    actor: _currentUser.Username);
            }

            // A change begun on the recalled order ends as the cancellation
            // record, so the emptied cart is no abort.
            var cancelStartedAt = securedOrder is null ? null : _tseVorgang.StartedAt;
            // R146: what was cancelled while the recalled order was changed belongs to its cancellation record.
            var cancelCancelled = securedOrder is null ? null : _tseVorgang.CancelledLines.ToArray();
            var cancelVorgang = securedOrder is null ? null : _tseVorgang.Release();

            _engine.Clear();
            UpdateCart();

            if (securedOrder is not null)
            {
                try
                {
                    await _tseVorgangWork;
                    await _orderFiscalSigning.SecureCancellationAsync(securedOrder, cancelVorgang ?? "", cancelStartedAt, _currentUser.Username, cancelledLines: cancelCancelled);
                }
                catch (Exception ex)
                {
                    CrashLog.WriteException("Fiscal signing order cancellation " + securedOrder.ParkNumber, ex);
                }
            }
            CartList.SelectedIndex = -1;

            _activeParkedReceiptId = null;
            _activeParkNumber = null;
            _engine.IsReadOnly = false;
            _operationId = Guid.NewGuid().ToString("N");

            await WriteControlledActionAsync(
                actionId,
                "APPLIED",
                "C_CLEAR_CART",
                reason,
                parkedId is null ? "CURRENT_CART" : "PARKED_RECEIPT",
                parkedId?.ToString() ?? "",
                beforeTotal,
                0,
                beforeTotal,
                $"positions={positions}; quantity={quantity}",
                operationId);

            PrepareNextCustomer();
            await RefreshParkedCountAsync();

            ScannerStatus.Text =
                "Verkauf abgebrochen · Grund unveränderbar protokolliert.";
        }
        catch (Exception ex)
        {
            try
            {
                await WriteControlledActionAsync(
                    actionId,
                    "FAILED",
                    "C_CLEAR_CART",
                    reason,
                    parkedId is null ? "CURRENT_CART" : "PARKED_RECEIPT",
                    parkedId?.ToString() ?? "",
                    beforeTotal,
                    _engine.TotalCents,
                    beforeTotal,
                    "error=" + ex.Message,
                    operationId);
            }
            catch (Exception auditEx)
            {
                CrashLog.WriteException(
                    "R70 clear-cart FAILED audit could not be written.",
                    auditEx);
            }

            var errorId = ReportOperationalError(
                "VERKAUF ABBRECHEN",
                "Verkauf konnte nicht vollständig abgebrochen werden.",
                ex);
            ScannerStatus.Text =
                $"ABBRUCH FEHLER · {errorId}";
        }
    }

    /// <summary>R143: the open parked receipt if the TSE has secured it as an order, read before it is cancelled.</summary>
    private async Task<ParkedReceipt?> SecuredOrderAsync(long parkedId)
    {
        if (!SecuresVorgaengeFiscally())
            return null;
        var order = await _parkedReceipts.GetOpenByIdAsync(parkedId, training: _currentUser.IsTraining);
        return order is not null && await _orderFiscalSigning.IsSecuredAsync(order) ? order : null;
    }

    private async Task SecureOrderCancellationAsync(ParkedReceipt order)
    {
        try
        {
            await _tseVorgangWork;
            await _orderFiscalSigning.SecureCancellationAsync(order, "", null, _currentUser.Username);
        }
        catch (Exception ex)
        {
            CrashLog.WriteException("Fiscal signing order cancellation " + order.ParkNumber, ex);
        }
    }

    private async Task<bool> CancelActiveParkedReceiptIfEmptyAsync(string trigger)
    {
        if (_activeParkedReceiptId is not long parkedId || _engine.Cart.Count != 0)
            return false;

        var parkedNumber = _activeParkNumber;
        try
        {
            // R143: a secured order emptied position by position is cancelled like
            // with C - as its own Bestellung-V1 record (R137, DSFinV-K 4.2.3).
            var securedOrder = await SecuredOrderAsync(parkedId);
            await _parkedReceipts.CancelAsync(parkedId);
            if (securedOrder is not null)
                await SecureOrderCancellationAsync(securedOrder);
            await _audit.WriteAsync(
                _currentUser.Username,
                "PARK_CANCEL",
                "PARKED_RECEIPT",
                parkedId.ToString(),
                $"parked_number={parkedNumber}; trigger={trigger}; reason=empty cart");

            _activeParkedReceiptId = null;
            _activeParkNumber = null;
            _operationId = Guid.NewGuid().ToString("N");
            _engine.IsReadOnly = false;
            PrepareNextCustomer();
            await RefreshParkedCountAsync();
            ScannerStatus.Text = $"Geparkter Bon P{parkedNumber:000000} entfernt · kein offener Parkbon mehr.";
        }
        catch (Exception ex)
        {
            var errorId = ReportOperationalError("DATENBANK", "Geparkten Bon entfernen fehlgeschlagen.", ex);
            ScannerStatus.Text = $"Geparkter Bon konnte nicht entfernt werden · Fehler-ID {errorId}";
        }
        return true;
    }

    private async void OnDiscountClick(object? s,RoutedEventArgs e)
    {
        if (!RequirePermission(UserPermissions.Discount, "RABATT"))
        {
            await RecordControlledDeniedAsync(
                "DISCOUNT_SET",
                "MISSING_PERMISSION_OR_LOCK");
            return;
        }

        if (_engine.Cart.Count == 0)
        {
            ScannerStatus.Text = "RABATT: Kein offener Verkauf.";
            return;
        }

        // R149: a receipt with returned deposit takes no manual discount.
        if (_engine.HasDepositReturns)
        {
            await RecordControlledDeniedAsync(
                "DISCOUNT_SET",
                "DEPOSIT_RETURN");
            ScannerStatus.Text = "RABATT GESPERRT · Der Bon enthält eine Pfand-Rückgabe.";
            return;
        }

        if (_engine.PromotionDiscountCents > 0 &&
            !_settingsCache.GetBool(
                "promotion.allow_manual_discount",
                false))
        {
            await RecordControlledDeniedAsync(
                "DISCOUNT_SET",
                "ACTIVE_PROMOTION_POLICY");

            ScannerStatus.Text =
                "MANUELLER RABATT GESPERRT · Für diesen Verkauf ist bereits ein ANGEBOT aktiv.";
            return;
        }

        var amount =
            await new MoneyInputWindow(
                "Rabatt",
                "Rabatt in €")
                .ShowDialog<long?>(this);

        if (amount is null || CartLocked)
            return;

        var newDiscount = Math.Max(0, amount.Value);
        var oldDiscount = _engine.DiscountCents;
        var beforeTotal = _engine.TotalCents;
        var afterTotal = Math.Max(
            0,
            _engine.SubtotalCents - newDiscount);

        var reason = await AskControlledReasonAsync(
            "RABATT",
            "function.discount_reasons",
            "Kundenrabatt|Kulanz|Aktion|Preisabweichung|Sonstiger Grund",
            $"Rabatt alt {Formatting.Money(oldDiscount)} → " +
            $"neu {Formatting.Money(newDiscount)} · " +
            $"Verkauf {Formatting.Money(beforeTotal)} → " +
            $"{Formatting.Money(afterTotal)}");

        if (reason is null || CartLocked)
            return;

        var actionId = Guid.NewGuid().ToString("N");
        var operationId = _operationId;

        try
        {
            await WriteControlledActionAsync(
                actionId,
                "AUTHORIZED",
                "DISCOUNT_SET",
                reason,
                "CURRENT_CART",
                "",
                beforeTotal,
                afterTotal,
                newDiscount,
                $"old_discount_cents={oldDiscount}; " +
                $"new_discount_cents={newDiscount}; " +
                $"subtotal_cents={_engine.SubtotalCents}",
                operationId);
        }
        catch (Exception ex)
        {
            var errorId = ReportOperationalError(
                "AUDIT",
                "Rabatt wurde NICHT angewendet, weil die unveränderbare Protokollierung fehlgeschlagen ist.",
                ex);
            ScannerStatus.Text =
                $"RABATT NICHT AUSGEFÜHRT · Audit-Fehler {errorId}";
            return;
        }

        try
        {
            _engine.SetDiscount(newDiscount);
            UpdateCart();

            await WriteControlledActionAsync(
                actionId,
                "APPLIED",
                "DISCOUNT_SET",
                reason,
                "CURRENT_CART",
                "",
                beforeTotal,
                _engine.TotalCents,
                newDiscount,
                $"old_discount_cents={oldDiscount}; " +
                $"new_discount_cents={_engine.DiscountCents}",
                operationId);

            ScannerStatus.Text =
                $"RABATT protokolliert · {Formatting.Money(newDiscount)}";
        }
        catch (Exception ex)
        {
            try
            {
                await WriteControlledActionAsync(
                    actionId,
                    "FAILED",
                    "DISCOUNT_SET",
                    reason,
                    "CURRENT_CART",
                    "",
                    beforeTotal,
                    _engine.TotalCents,
                    newDiscount,
                    "error=" + ex.Message,
                    operationId);
            }
            catch (Exception auditEx)
            {
                CrashLog.WriteException(
                    "R70 discount FAILED audit could not be written.",
                    auditEx);
            }

            var errorId = ReportOperationalError(
                "RABATT",
                "Rabatt konnte nicht vollständig ausgeführt werden.",
                ex);
            ScannerStatus.Text =
                $"RABATT FEHLER · {errorId}";
        }
    }

    private async void OnEditionActionClick(object? sender, RoutedEventArgs e)
    {
        if (!RequirePermission(UserPermissions.Sale, "PFAND / EXTRA"))
            return;

        try
        {
            var business = InstallationEdition.ReadLocked()
                ?? _settingsCache.GetText("business.mode", "IMBISS").ToUpperInvariant();

            if (business == "IMBISS")
                await AddExtraAsync();
            else
                await AddPfandAsync();
        }
        catch (Exception ex)
        {
            var errorId = ReportOperationalError("WARENKORB", "Pfand/Extra konnte nicht hinzugefügt werden.", ex);
            ScannerStatus.Text = $"FEHLER {errorId} · nicht hinzugefügt.";
        }
    }

    private async Task AddPfandAsync()
    {
        long ReadPfand(string key, long fallback) =>
            long.TryParse(_settingsCache.GetText(key, fallback.ToString()), out var value)
                ? Math.Max(0, value)
                : fallback;

        var selection =
            await new PfandSelectionWindow(
                ReadPfand("pfand.direct.8.cent", 8),
                ReadPfand("pfand.direct.15.cent", 15),
                ReadPfand("pfand.direct.25.cent", 25),
                ReadPfand("pfand.crate.empty.cent", 150),
                ReadPfand("pfand.crate.full.cent", 330))
                .ShowDialog<PfandReturnSelection?>(this);

        if (selection is null)
            return;

        if (CartLocked) return;
        // R149: the key takes back empties - money leaves the till, a negative
        // position at the rate of the deposit, never a sale.
        if (_engine.DiscountCents > 0)
        {
            ScannerStatus.Text = "PFAND-RÜCKGABE: Zuerst den Rabatt entfernen - beides auf einem Bon ist nicht möglich.";
            return;
        }

        var option = selection.Option;
        var quantity = ConsumePendingQuantity();
        if (!_engine.AddDepositReturn(option.ProductId, option.Name, option.PriceCents, selection.VatRate, quantity))
        {
            ScannerStatus.Text = "PFAND-RÜCKGABE nicht möglich.";
            return;
        }

        UpdateCart();

        await _audit.WriteAsync(
            _currentUser.Username,
            "PFAND_RETURN_ADDED",
            "CURRENT_CART",
            option.ProductId.ToString(),
            $"name={option.Name}; cents=-{option.PriceCents}; quantity={quantity}; vat={selection.VatRate}");

        ScannerStatus.Text =
            $"{option.Name} · {quantity:0.###} × {Formatting.Money(-option.PriceCents)} · {selection.VatRate:0.#} %";
    }

    private async Task AddExtraAsync()
    {
        if (_engine.Cart.Count == 0)
        {
            ScannerStatus.Text = "EXTRA: Bitte zuerst einen Artikel zum Verkauf hinzufügen.";
            return;
        }

        var extras = await _repo.GetExtrasAsync();
        if (extras.Count == 0)
        {
            ScannerStatus.Text = "Keine Extras angelegt · STAMMDATEN → EXTRAS.";
            return;
        }

        var extra = await new ExtraSelectionWindow(extras)
            .ShowDialog<ExtraItem?>(this);
        if (extra is null)
            return;

        if (CartLocked) return;
        _engine.Add(new Product
        {
            Id = -1_000_000 - extra.Id,
            Name = extra.Name,
            BasePriceCents = extra.PriceCents,
            VatRate = extra.VatRate,
            PfandCents = 0,
            Unit = "Portion"
        }, quantity: ConsumePendingQuantity());
        UpdateCart();

        await _audit.WriteAsync(
            _currentUser.Username,
            "EXTRA_ITEM_ADDED",
            "CURRENT_CART",
            extra.Id.ToString(),
            $"name={extra.Name}; cents={extra.PriceCents}; vat={extra.VatRate:0.##}");

        ScannerStatus.Text =
            $"{extra.Name} · {Formatting.Money(extra.PriceCents)} hinzugefügt.";
    }

    private OrderWorkflowService OrderWorkflow => new(new SqliteDatabase(AppPaths.DatabasePath));
    private async void OnOrderBoardClick(object? sender, RoutedEventArgs e)
    {
        if(!RequirePermission(UserPermissions.ParkReceipts,"BESTELLÜBERSICHT")) return;
        if(_engine.Cart.Count>0) { ScannerStatus.Text="Aktuellen Verkauf zuerst kassieren oder parken."; return; }
        try {
            var id=await new OrderBoardWindow(OrderWorkflow,_currentUser.IsTraining,_currentUser.Username).ShowDialog<long?>(this);
            if(id is not long selected || CartLocked) return;
            var parked=await _parkedReceipts.GetOpenByIdAsync(selected,training:_currentUser.IsTraining);
            if(parked is null) { ScannerStatus.Text="Bestellung ist nicht mehr offen.";return; }
            _operationId=Guid.NewGuid().ToString("N");_engine.Restore(parked.Lines,parked.DiscountCents);
            _activeParkedReceiptId=parked.Id;_activeParkNumber=parked.ParkNumber;
            _imHaus=parked.ImHaus;
            await ResumeTseVorgangAsync(parked);
            UpdateCart();
        } catch(Exception ex) { ShowOperationalError("BESTELLÜBERSICHT", ex); }
    }

    private async void OnParkClick(object? sender, RoutedEventArgs e)
    {
        var orderMode = GetImbissPickupMode() == "ORDER";
        if (!RequirePermission(UserPermissions.ParkReceipts, orderMode ? "BESTELLUNG ANNEHMEN" : "PARKEN"))
            return;
        if (_currentUser.IsTraining && !orderMode)
        {
            ScannerStatus.Text = "PARKEN ist im TRAININGSMODUS deaktiviert. ORDER-Abholnummern können dagegen getestet werden.";
            return;
        }

        if (_engine.Cart.Count == 0)
        {
            ScannerStatus.Text = "Leerer Bon kann nicht geparkt werden.";
            return;
        }

        // R150: Parken/Bestellung itself becomes a fiscal Vorgang on a
        // production till. Refuse an ambiguous menu before the durable order
        // row or Bestellung-V1 TSE side effect is created.
        if (SecuresVorgaengeFiscally())
        {
            var blockedMenus = MenuVatPolicy.BlockingMenus(
                _engine.Cart,
                _catalog.Products,
                _imHaus);

            if (blockedMenus.Count > 0)
            {
                ScannerStatus.Text =
                    "PARKEN/BESTELLUNG GESPERRT · Menü-KDV fiskal nicht eindeutig · " +
                    string.Join(", ", blockedMenus.Select(x => x.MenuName).Distinct());
                return;
            }
        }

        var menuLines = MenuVatPolicy.ApplyAllocations(
            CheckoutSnapshot.CopyLines(_engine.Cart, _imHaus),
            _catalog.Products,
            _imHaus);

        SetCheckoutBusy(true);
        try
        {
            // R136: the Vorgang that ends here (order acceptance) or stays open
            // in the TSE while the receipt is parked.
            var vorgangId = _tseVorgang.VorgangId;
            var vorgangStartedAt = _tseVorgang.StartedAt;
            // R146/R151: cancelled positions belong to this record too, and
            // a cancelled menu must keep the same hidden VAT allocation as the
            // commercial menu line.
            var cancelledLines = MenuVatPolicy.ApplyAllocations(
                CheckoutSnapshot.CopyLines(_tseVorgang.CancelledLines, _imHaus),
                _catalog.Products,
                _imHaus);
            if (_activeParkedReceiptId is long parkedId)
            {
                // R138: every parked receipt is an order in the TSE, not only in ORDER mode.
                // R142: training orders too, marked as training in the export.
                var before = SecuresVorgaengeFiscally()
                    ? await _parkedReceipts.GetOpenByIdAsync(parkedId, training: _currentUser.IsTraining)
                    : null;
                await _parkedReceipts.UpdateAsync(
                    parkedId,
                    menuLines,
                    _engine.DiscountCents, orderPrint: orderMode, actor:_currentUser.Username, imHaus: _imHaus);
                var updated=await _parkedReceipts.GetOpenByIdAsync(parkedId, training: _currentUser.IsTraining);
                if (before is not null && updated is not null)
                {
                    // R137: DSFinV-K 4.2.3 - the change of an accepted order (R138:
                    // or parked receipt) is its own Bestellung-V1 record holding
                    // only the difference.
                    try
                    {
                        await _tseVorgangWork;
                        await _orderFiscalSigning.SecureChangeAsync(updated, before.Lines, vorgangId ?? "", vorgangStartedAt, _currentUser.Username, cancelledLines: cancelledLines);
                    }
                    catch (Exception ex)
                    {
                        CrashLog.WriteException("Fiscal signing order change " + updated.ParkNumber, ex);
                    }
                }
                else if (vorgangId is not null)
                {
                    // Training or a till that stopped booking for real while the
                    // receipt was open: nothing is secured, the Vorgang ends as aborted.
                    // R146: with its cancelled positions, as every abort (R143).
                    var lines = menuLines.Concat(TseVorgangCartTracker.CancellationPairs(cancelledLines)).ToArray();
                    var discount = _engine.DiscountCents;
                    var actor = _currentUser.Username;
                    QueueTseVorgangWork(v => v.AbortAsync(vorgangId, lines, discount, actor, actor));
                }
                ScannerStatus.Text = orderMode && updated?.PickupNumber>0
                    ? $"BESTELLUNG {updated.PickupNumber:000} aktualisiert und wieder geöffnet gespeichert."
                    : $"Geparkter Bon P{_activeParkNumber:000000} aktualisiert.";
            }
            else
            {
                var parked = await _parkedReceipts.ParkAsync(
                    menuLines,
                    _engine.DiscountCents,
                    _currentUser.Username,
                    assignPickupNumber: orderMode && _settingsCache.GetBool("imbiss.order.number_enabled",true),
                    training: _currentUser.IsTraining, orderPrint: orderMode, imHaus: _imHaus);

                // R83: TSE-Signatur der Bestellannahme (eigener "Bestellung-V1"-
                // Vorgang) läuft bewusst NACH dem durablen Park-Commit, genau wie
                // R78's Kassenbeleg-Signierung nach dem Sale-Commit - ein
                // TSE-Ausfall darf eine bereits angenommene Bestellung nie
                // rückgängig machen oder blockieren.
                // R137: only a till that books for real secures orders - a test
                // till records nothing fiscal (as for sales R113, cash movements
                // R134, training R135). Before, a test till signed every order,
                // logged a TSE outage for it and later forced an automatic closing.
                // R138: every parked receipt, in ORDER mode or not, ends its
                // Vorgang as Bestellung-V1. AEAO zu § 146a Nr. 2.2.3.6.2 and the
                // BMF Kassen-FAQ: a Vorgang that is not completed is secured as an
                // order and linked to its receipt - no Kassenbeleg transaction
                // stays open while a receipt waits, however long it waits.
                if (SecuresVorgaengeFiscally())
                {
                    try
                    {
                        // R136: ends the Vorgang started with the first position.
                        // R142: a training order is secured the same way (AVTraining).
                        await _tseVorgangWork;
                        await _orderFiscalSigning.SignInVorgangAsync(parked, vorgangId ?? "", vorgangStartedAt, _currentUser.Username, cancelledLines: cancelledLines);
                    }
                    catch (Exception ex)
                    {
                        CrashLog.WriteException("Fiscal signing order " + parked.ParkNumber, ex);
                    }
                }
                else if (vorgangId is not null)
                {
                    // A training order stays a simulation (R135), and a till that
                    // stopped booking for real secures nothing: the Vorgang does
                    // not become a record and ends as aborted.
                    var lines = menuLines.Concat(TseVorgangCartTracker.CancellationPairs(cancelledLines)).ToArray();
                    var discount = _engine.DiscountCents;
                    var actor = _currentUser.Username;
                    QueueTseVorgangWork(v => v.AbortAsync(vorgangId, lines, discount, actor, actor));
                }

                ScannerStatus.Text = orderMode && parked.PickupNumber>0
                    ? $"BESTELLUNG ANGENOMMEN · ABHOLNR. {parked.PickupNumber:000} · Bon wird erst beim Kassieren erstellt."
                    : $"Bon {parked.DisplayNumber} geparkt.";

            }

            _tseVorgang.Release();
            _activeParkedReceiptId = null;
            _activeParkNumber = null;
            _operationId=Guid.NewGuid().ToString("N");
            _engine.IsReadOnly=false;
            _engine.Clear();
            PrepareNextCustomer();
            await RefreshParkedCountAsync();
        }
        catch (Exception ex)
        {
            CrashLog.WriteException("MainWindow operation", ex);
            var errorId = ReportOperationalError("DATENBANK", "Parken fehlgeschlagen: " + ex.Message, ex);
            ScannerStatus.Text = $"Parken fehlgeschlagen · Fehler-ID {errorId}";
        }
        finally { SetCheckoutBusy(false); }
    }

    private async void OnParkedReceiptsClick(object? sender, RoutedEventArgs e)
    {
        var orderMode = GetImbissPickupMode() == "ORDER";
        if (!RequirePermission(UserPermissions.ParkReceipts, orderMode ? "OFFENE BESTELLUNGEN" : "GEPARKTE BONS"))
            return;
        if (_currentUser.IsTraining && !orderMode)
        {
            ScannerStatus.Text = "GEPARKTE BONS sind im TRAININGSMODUS deaktiviert. ORDER-Bestellungen können getestet werden.";
            return;
        }

        if (_engine.Cart.Count > 0)
        {
            ScannerStatus.Text =
                "Aktuellen Verkauf zuerst kassieren oder PARKEN.";
            return;
        }

        var open = await _parkedReceipts.GetOpenAsync(training: _currentUser.IsTraining);

        if (open.Count == 0)
        {
            ScannerStatus.Text = "Keine geparkten Bons vorhanden.";
            await RefreshParkedCountAsync();
            return;
        }

        var selection =
            await new ParkedReceiptsWindow(open, GetImbissPickupMode()=="ORDER").ShowDialog<ParkedReceiptDialogResult?>(this);

        if (selection is null)
            return;

        if (selection.Delete)
        {
            try
            {
                await _parkedReceipts.CancelAsync(selection.Id,orderPrint:orderMode,actor:_currentUser.Username);
                if (open.FirstOrDefault(x => x.Id == selection.Id) is { } deleted)
                {
                    var actor = _currentUser.Username;
                    if (SecuresVorgaengeFiscally() && await _orderFiscalSigning.IsSecuredAsync(deleted))
                    {
                        // R137: DSFinV-K 4.2.3 - a cancelled order is a new record
                        // with reversed sign, secured on its own.
                        try
                        {
                            await _tseVorgangWork;
                            await _orderFiscalSigning.SecureCancellationAsync(deleted, "", null, actor);
                        }
                        catch (Exception ex)
                        {
                            CrashLog.WriteException("Fiscal signing order cancellation " + deleted.ParkNumber, ex);
                        }
                    }
                    else
                    {
                        // R136: the Vorgang of a deleted parked receipt ends as aborted.
                        QueueTseVorgangWork(v => v.AbortParkedAsync(deleted.Id, deleted.Lines, deleted.DiscountCents, actor, actor));
                    }
                }
                ScannerStatus.Text = "Geparkter Bon gelöscht.";
                await RefreshParkedCountAsync();
            }
            catch (Exception ex)
            {
                var errorId = ReportOperationalError("DATENBANK", "Geparkten Bon löschen fehlgeschlagen.", ex);
                ScannerStatus.Text = $"Geparkter Bon konnte nicht gelöscht werden · Fehler-ID {errorId}";
            }
            return;
        }

        var parked =
            await _parkedReceipts.GetOpenByIdAsync(selection.Id, training: _currentUser.IsTraining);

        if (parked is null)
        {
            ScannerStatus.Text =
                "Der geparkte Bon ist nicht mehr offen.";
            await RefreshParkedCountAsync();
            return;
        }

        if (CartLocked) return;
        _operationId=Guid.NewGuid().ToString("N");
        _engine.Restore(parked.Lines, parked.DiscountCents);
        _activeParkedReceiptId = parked.Id;
        _activeParkNumber = parked.ParkNumber;
        _imHaus = parked.ImHaus;
        await ResumeTseVorgangAsync(parked);
        UpdateCart();

        ScannerStatus.Text = parked.PickupNumber>0
            ? $"BESTELLUNG {parked.PickupNumber:000} übernommen · jetzt BAR/KARTE kassieren."
            : $"{parked.DisplayNumber} übernommen · jetzt kassieren.";
        await _audit.WriteAsync(
            _currentUser.Username,
            "UNPARK",
            "PARKED_RECEIPT",
            parked.Id.ToString(),
            parked.DisplayNumber);
    }

    private async Task RefreshParkedCountAsync()
    {
        try
        {
            var count = await _parkedReceipts.GetOpenCountAsync(training: _currentUser.IsTraining);

            ParkedReceiptsText.Text = GetImbissPickupMode()=="ORDER"
                ? $"OFFENE BESTELLUNGEN ({count})"
                : $"GEPARKTE BONS ({count})";

            var hasOpen = count > 0;
            ParkedReceiptsText.Foreground = hasOpen ? AppTheme.WarningAmber : AppTheme.TextPrimary;
            ParkedReceiptsButton.Background = new SolidColorBrush(
                Color.Parse(hasOpen ? "#4A3B10" : "#1B344C"));
            ParkedReceiptsButton.BorderBrush = new SolidColorBrush(
                Color.Parse(hasOpen ? "#D6A83C" : "#496985"));
        }
        catch
        {
            ParkedReceiptsText.Text = GetImbissPickupMode()=="ORDER" ? "OFFENE BESTELLUNGEN (?)" : "GEPARKTE BONS (?)";
        }
    }

    public async Task<DailyCloseCheck> CheckZClosingAllowedAsync()
    {
        return await _dailyClosingGuard.CheckAsync();
    }

    private async void OnZReportClick(object? sender, RoutedEventArgs e)
    {
        if (!RequirePermission(UserPermissions.ZReport, "Z-BERICHT") ||
            !RequireRealMode("Z-BERICHT"))
            return;

        // R136: AEAO zu § 146a Nr. 2.2.3.3 - no Vorgang may stay open at a
        // closing. The cart must be empty; transactions still open without a
        // cart (a crash) are ended as aborted before the closing.
        if (_engine.Cart.Count > 0)
        {
            ScannerStatus.Text = "Z-BERICHT: Aktuellen Vorgang zuerst kassieren, parken oder mit C leeren.";
            return;
        }

        await _tseVorgangWork;
        if (Vorgaenge is { } openVorgaenge)
        {
            try
            {
                await _fiscalSigning.FinishCommittedVorgaengeAsync(_currentUser.Username);
                await openVorgaenge.AbortOrphansAsync(null, _currentUser.Username);
            }
            catch (Exception ex)
            {
                CrashLog.WriteException("TSE-Vorgänge vor Z-Bericht", ex);
            }
        }

        var parkedCheck = await _dailyClosingGuard.CheckAsync();

        if (!parkedCheck.Allowed)
        {
            ScannerStatus.Text = parkedCheck.Message;
            await new ZReportInfoWindow(
                "Z-BERICHT GESPERRT",
                parkedCheck.Message,
                false).ShowDialog<bool>(this);
            return;
        }

        var fiscal = await _compliance.CheckAsync();

        if (!fiscal.ProductionAllowed)
        {
            var message =
                "Keine geparkten Bons offen. Der echte fiskale Z-Bericht ist jedoch " +
                "noch nicht produktiv freigegeben, weil TOR POS weiterhin im TESTBETRIEB läuft.";

            ScannerStatus.Text = "Z-Bericht: TESTBETRIEB · produktiv gesperrt";

            await _audit.WriteAsync(
                _currentUser.Username,
                "Z_REPORT_BLOCKED_TEST_MODE",
                "DAILY_CLOSE",
                DateTimeOffset.Now.ToString("yyyy-MM-dd"),
                $"blocking_count={fiscal.BlockingCount}");

            await new ZReportInfoWindow(
                "Z-BERICHT · TESTBETRIEB",
                message,
                false).ShowDialog<bool>(this);
            return;
        }

        try
        {
            var archived = await _management.CreateZArchiveAsync(
                _currentUser.Username,
                "PRODUCTION_ALLOWED");

            var document = _management.ZArchiveToDocument(archived);
            var datevNotes = new List<string>();

            // R155: the zero-cost Standard-ASCII/CSV path is the primary DATEV
            // workflow. Z is already final at this point; file/mail failures
            // must never roll the fiscal close back.
            if (_settingsCache.GetBool(DatevKassenbuchAsciiService.SettingEnabled, false) &&
                _settingsCache.GetBool(DatevKassenbuchAsciiService.SettingAutoAfterZ, false))
            {
                try
                {
                    var ascii = await _datevAscii.PrepareAsync(
                        archived,
                        _currentUser.Username);
                    var note = $"DATEV CSV erstellt: {Path.GetFileName(ascii.CsvPath)}";

                    if (_settingsCache.GetBool(DatevKassenbuchAsciiService.SettingAutoEmail, false))
                    {
                        try
                        {
                            var sent = await _datevAscii.SendToSteuerberaterAsync(
                                ascii,
                                _currentUser.Username);
                            note += sent.EmailState == "SENT"
                                ? $" · an {sent.EmailRecipient} gesendet"
                                : $" · E-Mail-Status {sent.EmailState}";
                        }
                        catch (Exception mailEx)
                        {
                            CrashLog.WriteException("DATEV ASCII E-Mail after Z", mailEx);
                            note += " · E-Mail offen: " + mailEx.Message;
                        }
                    }

                    datevNotes.Add(note);
                }
                catch (Exception asciiEx)
                {
                    CrashLog.WriteException("DATEV Kassenbuch ASCII after Z", asciiEx);
                    datevNotes.Add("DATEV CSV noch offen: " + asciiEx.Message);
                }
            }

            // Optional future API route stays separate and disabled unless the
            // operator explicitly enables it.
            if (_settingsCache.GetBool(DatevKassenarchivService.SettingEnabled, false) &&
                _settingsCache.GetBool(DatevKassenarchivService.SettingAutoAfterZ, false))
            {
                try
                {
                    var prepared = await _datevKassenarchiv.PrepareZAsync(
                        archived,
                        _currentUser.Username);
                    await _datevKassenarchiv.MarkWaitingForOfficialApiAsync(
                        prepared.Entry.Id,
                        _currentUser.Username);
                    datevNotes.Add(
                        $"Kassenarchiv-API-Paket vorbereitet ({prepared.Entry.PackageSha256[..12]}…) · wartet auf Freischaltung");
                }
                catch (Exception datevEx)
                {
                    CrashLog.WriteException("DATEV Kassenarchiv after Z", datevEx);
                    datevNotes.Add("Kassenarchiv-API-Paket offen: " + datevEx.Message);
                }
            }

            var datevNote = datevNotes.Count == 0
                ? ""
                : " · " + string.Join(" · ", datevNotes);

            ScannerStatus.Text =
                $"Z {archived.ZNumber:000000} abgeschlossen und unveränderbar archiviert." +
                datevNote;

            await new TextReportWindow(
                _management,
                document,
                _receiptPrinter,
                _settings,
                $"Z-Abschluss gespeichert · {archived.ReceiptCount} Bons · {GermanFormat.Amount(archived.GrossCents)} € Umsatz.{datevNote} Drucker und 58/80 mm oder A4 können jetzt gewählt werden.")
                .ShowDialog(this);
        }
        catch (Exception ex)
        {
            CrashLog.WriteException("MainWindow operation", ex);
            var errorId = ReportOperationalError("Z-BERICHT", "Z-Bericht fehlgeschlagen: " + ex.Message, ex);
            ScannerStatus.Text = $"Z-Bericht fehlgeschlagen · Fehler-ID {errorId}";
        }
    }

    private async void OnCashMovementClick(object? sender, RoutedEventArgs e)
    {
        if (!RequirePermission(UserPermissions.CashMovement, "EINLAGE / ENTNAHME") ||
            !RequireRealMode("EINLAGE / ENTNAHME"))
            return;

        var request = await new CashMovementWindow()
            .ShowDialog<CashMovementRequest?>(this);

        if (request is null)
            return;

        try
        {
            // R134: a real booking is a fiscal Vorgang and is signed by the
            // TSE (AEAO zu § 146a Nr. 1.10.2); in test mode it stays a test entry.
            var production = !IsSimulation;
            var movement = await _cashMovements.AddAsync(
                request with { Production = production },
                _currentUser.Username);

            var tseNote = "";
            if (production)
            {
                var signed = await new CashMovementFiscalSigningService(_tseFailSafe, _settings, _cashMovements)
                    .SignAsync(movement, _currentUser.Username);
                tseNote = signed.Signed ? " · TSE-signiert" : " · TSE-AUSFALL dokumentiert";
            }

            var opening = _settingsCache.GetInt("cash.start.cents", 0);
            // R139: the same running cash position the Kassensturz compares against.
            var expected = (await _cashMovements.GetCashBalanceAsync(opening, production)).ExpectedCents;

            ScannerStatus.Text =
                $"{movement.Kind} ({movement.BusinessCase}): {Formatting.Money(movement.AmountCents)} gebucht{tseNote} · " +
                $"Soll-Kassenbestand{(production ? "" : " TEST")}: {Formatting.Money(expected)}";
        }
        catch (Exception ex)
        {
            CrashLog.WriteException("MainWindow operation", ex);
            var errorId = ReportOperationalError("DATENBANK", "Kassenbewegung fehlgeschlagen: " + ex.Message, ex);
            ScannerStatus.Text = $"Kassenbewegung fehlgeschlagen · Fehler-ID {errorId}";
        }
    }

    private async void OnCashClick(object? sender, RoutedEventArgs e) =>
        await OpenPaymentWindowAsync(invokedByQuickCheckout: false);

    private async void OnCardClick(object? sender, RoutedEventArgs e) =>
        await OpenPaymentWindowAsync(invokedByQuickCheckout: false);

    private async void OnQuickCheckoutClick(object? sender, RoutedEventArgs e) =>
        await OpenPaymentWindowAsync(invokedByQuickCheckout: true);

    private bool IsImbissBusiness() =>
        string.Equals(
            (InstallationEdition.ReadLocked()
                ?? _settingsCache.GetText("business.mode", "IMBISS")).Trim(),
            "IMBISS",
            StringComparison.OrdinalIgnoreCase);

    private async Task OpenPaymentWindowAsync(bool invokedByQuickCheckout)
    {
        if (CartLocked || _engine.Cart.Count == 0 || !CanCompleteSale())
            return;

        var allowImHaus = IsImbissBusiness();
        var paymentTotal = _engine.TotalCents;
        var choice = await new PaymentChoiceWindow(
                _cashEnabledBySettings,
                _cardEnabledBySettings,
                allowImHaus,
                allowImHaus && _imHaus,
                totalCents: paymentTotal,
                simulation: IsSimulation)
            .ShowDialog<PaymentChoiceResult?>(this);

        if (choice is null)
            return;

        _imHaus = allowImHaus && choice.ImHaus;

        // R168: Verkaufsart, BAR/KARTE/GEMISCHT and BAR/GEMISCHT amount entry
        // all live in this one payment page. No second cash/mixed/test-card page.
        UpdateCart();

        CashPaymentResult? pageCash = null;
        if (choice.Method == PaymentMethod.Cash && paymentTotal > 0)
        {
            if (choice.CashTenderedCents < paymentTotal)
            {
                ScannerStatus.Text = "BARZAHLUNG: Gegebener Betrag ist kleiner als der Zahlbetrag.";
                return;
            }

            pageCash = new CashPaymentResult(
                choice.CashTenderedCents,
                choice.CashTenderedCents - paymentTotal);
        }

        await CheckoutAsync(
            choice.Method,
            invokedByQuickCheckout,
            choice.CashPortionCents,
            pageCash,
            cardConfirmedOnPaymentPage: true);
    }

    private CheckoutSnapshot CaptureCheckout(PaymentMethod method, long cashPortionCents = 0) => new(
        _operationId,
        CheckoutSnapshot.CopyLines(_engine.Cart, _imHaus),
        _engine.DiscountCents,
        method,
        _currentUser.Username,
        _activeParkedReceiptId,
        _imHaus,
        cashPortionCents,
        _tseVorgang.VorgangId ?? "",
        _tseVorgang.StartedAt,
        _tseVorgang.CancelledLines.Count == 0
            ? null
            : CheckoutSnapshot.CopyLines(_tseVorgang.CancelledLines, _imHaus));

    private void SetCheckoutBusy(bool busy)
    {
        _paymentInProgress=busy;
        _engine.IsReadOnly=busy;
        if (Content is Control surface) surface.IsEnabled=!busy;
        RefreshSalesActionState();

        if (!busy)
            FocusScannerCaptureSoon();
    }

    private async Task CheckoutAsync(
        PaymentMethod method,
        bool invokedByQuickCheckout,
        long cashPortionCents = 0,
        CashPaymentResult? paymentPageCash = null,
        bool cardConfirmedOnPaymentPage = false)
    {
        if (CartLocked || _engine.Cart.Count==0 || !CanCompleteSale()) return;
        var snapshot=CaptureCheckout(method, cashPortionCents);
        // R149: returned deposit exceeding the purchase is paid out - in cash only.
        if (snapshot.TotalCents < 0 && method != PaymentMethod.Cash)
        {
            ScannerStatus.Text = "Pfand-Auszahlung nur BAR möglich.";
            return;
        }
        CashPaymentResult? cash=null;
        SetCheckoutBusy(true);
        try
        {
            ScannerStatus.Text = "ZAHLUNG WIRD VORBEREITET";
            // Give Avalonia one turn to paint the status before Windows device I/O.
            await Task.Yield();
            // Flush earlier cart writes before admitting an external effect.
            using (_perf.Measure("checkout.recovery_flush"))
                await _recoveryWrite;

            if (_recoveryFault) return;

            // R67.2: Measure only technical printer I/O inside
            // EnsureReceiptPrinterReadyForCheckoutAsync. The time an operator
            // spends reading/answering the warning dialog is NOT system latency.
            var printerReady = await EnsureReceiptPrinterReadyForCheckoutAsync();

            if (!printerReady)
            {
                ScannerStatus.Text = "ZAHLUNG ABGEBROCHEN · Bondrucker nicht erkannt";
                return;
            }
            if (method==PaymentMethod.Cash && snapshot.TotalCents <= 0)
            {
                // R149: nothing to tender. A payout is confirmed once the money is handed over.
                if (snapshot.TotalCents < 0 &&
                    !await new DepositPayoutWindow(-snapshot.TotalCents).ShowDialog<bool>(this))
                    return;
                cash = new CashPaymentResult(0, 0);
            }
            else if (method==PaymentMethod.Cash)
            {
                if (paymentPageCash is not null)
                {
                    cash = paymentPageCash;
                }
                else if (QuickCheckoutPolicy.UseExactCashWithoutDialog(
                    _settingsCache,
                    method,
                    invokedByQuickCheckout))
                {
                    // Compatibility fallback for non-UI callers. Normal cashier
                    // checkout already collected the amount in PaymentChoiceWindow.
                    cash = new CashPaymentResult(snapshot.TotalCents, 0);
                    _perf.RecordElapsed("checkout.quick_exact_cash", 0);
                }
                else
                {
                    // Legacy fallback only; the visible R168 flow never reaches
                    // a second cash dialog.
                    cash=await new CashPaymentWindow(snapshot.TotalCents).ShowDialog<CashPaymentResult?>(this);
                    if(cash is null) return;
                }
            }
            else if (method==PaymentMethod.Mixed)
            {
                // R168: the BAR portion was entered on the one payment page.
                cash = new CashPaymentResult(snapshot.EffectiveCashPortionCents, 0);
            }
            if (IsSimulation)
            {
                // R151: simulation / fiscally recorded training bypasses the
                // production application service, so enrich its immutable
                // snapshot here. Components still stay hidden on the receipt.
                snapshot = snapshot with
                {
                    Lines = MenuVatPolicy.ApplyAllocations(
                        snapshot.Lines,
                        _catalog.Products,
                        snapshot.ImHaus),
                    CancelledLines = snapshot.CancelledLines is null
                        ? null
                        : MenuVatPolicy.ApplyAllocations(
                            snapshot.CancelledLines,
                            _catalog.Products,
                            snapshot.ImHaus)
                };

                if((method==PaymentMethod.Card || method==PaymentMethod.Mixed) &&
                    !_currentUser.IsTraining &&
                    !cardConfirmedOnPaymentPage &&
                    !await new CardTestPaymentWindow(snapshot.EffectiveCardPortionCents).ShowDialog<bool>(this)) return;
                await _audit.WriteAsync(_currentUser.Username,"TEST_SALE_COMPLETED","SIMULATION",snapshot.OperationId,
                    $"total_cents={snapshot.TotalCents}; payment={method}; no_fiscal_sale=true");
                // R135: on a till that books for real, a training sale is an
                // AVTraining Vorgang (AEAO zu § 146a Nr. 1.11.1, DSFinV-K 4.2.6):
                // recorded and TSE-secured, never part of turnover or the closing.
                if (RecordsTrainingFiscally())
                {
                    try
                    {
                        var trainings = new TrainingReceiptRepository(new SqliteDatabase(AppPaths.DatabasePath));
                        // R142: a training order paid with other positions is changed first.
                        await SecurePaidOrderChangeAsync(snapshot);
                        var training = await trainings.RecordAsync(snapshot);
                        await _tseVorgangWork;
                        await new TrainingFiscalSigningService(_tseFailSafe, _settings, trainings) { Vorgaenge = Vorgaenge }
                            .SignInVorgangAsync(training, snapshot.TseVorgangId, _currentUser.Username);
                    }
                    catch (Exception ex)
                    {
                        ReportOperationalError("TRAINING", "Trainingsvorgang konnte nicht als AVTraining erfasst werden: " + ex.Message, ex);
                    }
                }
                else if (snapshot.TseVorgangId.Length > 0)
                {
                    // R136: the till left real booking while this cart was open
                    // (fiscal readiness changed). The started Vorgang does not
                    // become a receipt and ends as aborted.
                    var actor = _currentUser.Username;
                    QueueTseVorgangWork(v => v.AbortAsync(snapshot.TseVorgangId, snapshot.Lines, snapshot.DiscountCents, actor, actor));
                }
                long testPickup = 0;
                var pickupMode = GetImbissPickupMode();
                if (_activeParkedReceiptId is long testParkedLookup)
                {
                    var parkedOrder = await _parkedReceipts.GetOpenByIdAsync(testParkedLookup, training: _currentUser.IsTraining);
                    testPickup = parkedOrder?.PickupNumber ?? 0;
                }
                if (testPickup == 0 && pickupMode == "SALE")
                    testPickup = await _management.NextSimulationPickupAsync(_currentUser.IsTraining);
                var testJob = SimulationReceipt.Create(snapshot,
                    _settingsCache.GetText("company.name", "TOR POS"),
                    string.Join(" ", new[] { "company.street", "company.zip", "company.city" }
                        .Select(key => _settingsCache.GetText(key, "")).Where(value => !string.IsNullOrWhiteSpace(value))),
                    cash?.TenderedCents,
                    _settingsCache.GetText("receipt.logo_path", ""),
                    testPickup);
                if (_activeParkedReceiptId is long testParkedId) {
                    await OrderWorkflow.CompleteSimulationAsync(testParkedId,method,_currentUser.IsTraining);
                    await _audit.WriteAsync(_currentUser.Username,"PARK_CLOSED_TEST","PARKED_RECEIPT",testParkedId.ToString(),"Simulation completed; no fiscal sale");
                }

                // R177: hardware checkout tests must exercise the same drawer
                // path even while fiscal production is intentionally locked.
                await TryOpenCashDrawerAfterPaymentAsync(
                    method,
                    snapshot.EffectiveCashPortionCents);

                ClearCompletedCart();
                await RefreshParkedCountAsync();
                ScannerStatus.Text = testPickup > 0
                    ? $"TEST · ABHOLNR. {testPickup:000} · {Formatting.Money(snapshot.TotalCents)} · keine echte Buchung"
                    : $"TEST · {Formatting.Money(snapshot.TotalCents)} · keine echte Buchung";
                // R65: Der Testverkauf ist bereits abgeschlossen. Der optionale
                // Windows-Druck darf die Kassenoberfläche danach nicht mehr blockieren.
                // R145: the digital receipt can be tried here too, marked TESTBON.
                if (await DigitalReceiptOfferedAsync())
                {
                    var withoutPrinter = _checkoutWithoutPrinterAccepted;
                    _ = OfferReceiptChoiceAsync(
                        testJob,
                        DigitalReceiptDocument.PaymentsFor(method, snapshot.EffectiveCashPortionCents, snapshot.EffectiveCardPortionCents),
                        "SIMULATION",
                        snapshot.OperationId,
                        () => PrintSimulationAsync(testJob, withoutPrinter, explicitRequest: true));
                }
                else
                    _ = PrintSimulationAsync(testJob);
                return;
            }
            var prepared =
                await _checkoutApplication.PrepareProductionAsync(
                    snapshot);

            if(prepared.Timings.FiscalPreflightMs is double fiscalMs)
                _perf.RecordElapsed("checkout.fiscal_preflight",fiscalMs);

            if(prepared.Timings.JournalBeginMs is double journalMs)
                _perf.RecordElapsed("checkout.journal_begin",journalMs);

            if(prepared.Timings.TerminalRoundtripMs is double terminalMs)
                _perf.RecordElapsed("terminal.payment_roundtrip",terminalMs);

            if(prepared.Disposition==CheckoutApplicationDisposition.FiscalBlocked)
            {
                ScannerStatus.Text=
                    $"FISKAL-FREIGABE FEHLT · keine Zahlung gestartet · " +
                    $"{prepared.FiscalReadiness.BlockingCount} Blocker";
                return;
            }

            var operation =
                prepared.Operation
                ?? throw new InvalidOperationException(
                    "Application-Service lieferte keinen Checkout-Status.");

            _pendingCheckout=operation;

            if(prepared.Disposition==CheckoutApplicationDisposition.NotCharged)
            {
                _pendingCheckout=null;
                _operationId=Guid.NewGuid().ToString("N");
                PersistOpenCartRecovery();

                ScannerStatus.Text =
                    operation.TerminalOutcome switch
                    {
                        PaymentTerminalOutcome.Declined =>
                            "KARTE ABGELEHNT · keine Belastung · Bon bleibt offen",

                        PaymentTerminalOutcome.Cancelled =>
                            "KARTENZAHLUNG ABGEBROCHEN · keine Belastung · Bon bleibt offen",

                        _ =>
                            "KEINE ZAHLUNG GESENDET · Bon bleibt offen"
                    };

                return;
            }

            if(prepared.Disposition==CheckoutApplicationDisposition.Unresolved)
            {
                var id=ReportOperationalError(
                    "ZVT",
                    "Zahlungsstatus unklar. Nicht erneut kassieren. " +
                    (prepared.TerminalResult?.Message ?? ""));

                ScannerStatus.Text=
                    $"ZAHLUNG GESPERRT · KASSE → ZAHLUNG PRÜFEN · {id}";

                return;
            }

            if(prepared.Disposition!=CheckoutApplicationDisposition.ReadyToCommit)
            {
                throw new InvalidOperationException(
                    "Unbekannter Application-Checkout-Status.");
            }

            await CommitCheckoutAsync(operation.Snapshot,cash);
        }
        catch(Exception ex)
        {
            CrashLog.WriteException("MainWindow operation", ex);
            // If begin/commit returned an error after a write, consult the journal before retrying.
            try
            {
                _pendingCheckout=(await _checkoutJournal.GetOpenAsync()).FirstOrDefault();
                if(await _checkoutJournal.FindSaleAsync(snapshot.OperationId) is not null)
                {
                    ClearCompletedCart();
                    // R138: the sale is booked; its TSE transaction is ended with its data.
                    var actor=_currentUser.Username;
                    QueueTseVorgangWork(_ => _fiscalSigning.FinishCommittedVorgaengeAsync(actor));
                }
            }
            catch { _recoveryFault=true; }
            var id=ReportOperationalError("ZAHLUNG","Verarbeitung prüfen. Keine automatische Wiederholung.",ex);
            ScannerStatus.Text=$"VORGANG PRÜFEN · NICHT ERNEUT KASSIEREN · Fehler-ID {id}";
        }
        finally { SetCheckoutBusy(false); _checkoutWithoutPrinterAccepted = false; }
    }

    /// <summary>
    /// R137: DSFinV-K 4.2.3 - when the positions paid differ from what the
    /// order secured (a position left out at pickup, one added, another
    /// discount), that change of the order is secured first, so orders and
    /// receipt account for each other. The payment has already happened here; a
    /// failure is logged and never stops the booking. Repeated after a crash,
    /// the difference is already secured and nothing is signed twice.
    /// R142: for a training order too.
    /// </summary>
    private async Task SecurePaidOrderChangeAsync(CheckoutSnapshot snapshot)
    {
        if (snapshot.ParkedReceiptId is not long paidOrderId || !SecuresVorgaengeFiscally())
            return;
        try
        {
            if (await _parkedReceipts.GetOpenByIdAsync(paidOrderId, training: _currentUser.IsTraining) is { } paidOrder &&
                await _orderFiscalSigning.IsSecuredAsync(paidOrder))
            {
                var orderedLines = paidOrder.Lines;
                paidOrder.Lines = snapshot.Lines.ToList();
                paidOrder.ImHaus = snapshot.ImHaus;
                paidOrder.DiscountCents = snapshot.DiscountCents;
                await _tseVorgangWork;
                await _orderFiscalSigning.SecureChangeAsync(paidOrder, orderedLines, "", null, _currentUser.Username);
            }
        }
        catch (Exception ex)
        {
            CrashLog.WriteException("Fiscal signing order change at payment " + paidOrderId, ex);
        }
    }

    private async Task TryOpenCashDrawerAfterPaymentAsync(
        PaymentMethod method,
        long cashPortionCents)
    {
        if (_currentUser.IsTraining)
            return;

        var cashMovement =
            method == PaymentMethod.Cash ||
            (method == PaymentMethod.Mixed && cashPortionCents > 0);

        if (!cashMovement)
            return;

        var enabled = _settingsCache.GetBool(
            "device.drawer.enabled",
            _settingsCache.GetBool("printer.drawer_kick.enabled", true));

        if (!enabled)
            return;

        var printerName = _settingsCache.GetText(
            "device.receipt_printer.name",
            "");

        if (string.IsNullOrWhiteSpace(printerName))
        {
            ReportOperationalError(
                "KASSENSCHUBLADE",
                "Barzahlung gespeichert, aber kein Bondrucker für die Kassenschublade konfiguriert.",
                null,
                printerRelated: true);
            return;
        }

        try
        {
            var channel = Math.Clamp(
                _settingsCache.GetInt(
                    "device.receipt_printer.drawer_channel",
                    1),
                1,
                2);

            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(6));

            await _receiptPrinter.OpenCashDrawerAsync(
                printerName,
                timeout.Token,
                channel);
        }
        catch (Exception ex)
        {
            // A drawer failure must never make a committed sale look unpaid
            // or encourage the cashier to repeat the payment.
            CrashLog.WriteException(
                "R177 cash drawer after payment",
                ex);
            ReportOperationalError(
                "KASSENSCHUBLADE",
                "Zahlung ist gespeichert, aber die Kassenschublade konnte nicht geöffnet werden.",
                ex,
                printerRelated: true);
        }
    }

    private async Task CommitCheckoutAsync(CheckoutSnapshot snapshot, CashPaymentResult? cash=null)
    {
        await SecurePaidOrderChangeAsync(snapshot);

        Sale sale;
        using (_perf.Measure("checkout.database_commit"))
            sale = await _sales.CommitAsync(snapshot);

        // R177: the drawer is a payment-side effect, not a paper-receipt
        // effect. Open it immediately after the durable sale commit so it also
        // works with digital receipt / BON AUS and cannot be skipped by print
        // routing. The sale is already committed if the drawer itself fails.
        await TryOpenCashDrawerAfterPaymentAsync(
            snapshot.Method,
            sale.EffectiveCashPortionCents);

        // R78: TSE-Beleg-Signatur läuft bewusst NACH dem durablen Commit.
        // Ein TSE-Ausfall darf einen bereits abgeschlossenen Verkauf nie
        // rückgängig machen oder blockieren - siehe SaleFiscalSigningService.
        try
        {
            // R136: ends the Vorgang whose TSE transaction started with the
            // first position of this cart.
            using (_perf.Measure("checkout.fiscal_signing"))
            {
                await _tseVorgangWork;
                await _fiscalSigning.SignInVorgangAsync(sale, snapshot.TseVorgangId, _currentUser.Username);
            }
        }
        catch (Exception ex)
        {
            CrashLog.WriteException("Fiscal signing " + sale.ReceiptNumber, ex);
        }

        // R113: signing may just have opened an outage (TSE inactive, or the
        // transaction failed) - surface it immediately instead of waiting for
        // the next full fiscal-status refresh.
        await RefreshTseOutageBadgeAsync();

        // Commit includes checkout state, parked state, number and stock in one transaction.
        // The sale.completed Cloud event is queued in that same transaction; Cloud uses it
        // to decrement its stock projection without making checkout wait for Internet.
        ClearCompletedCart();
        ScannerStatus.Text = sale.PickupNumber > 0
            ? $"ABHOLNR. {sale.PickupNumber:000} · Bon {sale.ReceiptNumber:000000} · {Formatting.Money(sale.TotalCents)}"
            : $"Bon {sale.ReceiptNumber:000000} GESPEICHERT · {Formatting.Money(sale.TotalCents)}";
        try
        {
            using (_perf.Measure("checkout.aftercare"))
            {
                await _audit.WriteAsync(_currentUser.Username,"SALE_COMPLETED","SALE",sale.Id.ToString(),
                    $"operation={snapshot.OperationId}; tendered={cash?.TenderedCents}; change={cash?.ChangeCents}");
                await RefreshParkedCountAsync();
                await RefreshStockWarningAsync();
            }
        }
        catch(Exception ex) {
            CrashLog.WriteException("MainWindow operation", ex); ReportOperationalError("NACHVERARBEITUNG",$"Bon {sale.ReceiptNumber} gespeichert. Nicht erneut kassieren.",ex); }
        var willAutoPrint = !_checkoutWithoutPrinterAccepted && _settingsCache.GetBool("device.receipt_printer.enabled",false) && _settingsCache.GetBool("receipt.auto_print",true);
        // R145: with the digital receipt switched on, the customer chooses
        // Papierbeleg or Digitalbeleg (QR) - the sale is final and signed by now
        // (AEAO zu § 146a Nr. 2.5.2). BON EIN/AUS applies when it is switched off.
        if(await DigitalReceiptOfferedAsync())
        {
            var job = BuildReceiptPrintJob(sale,snapshot.Method,cashPayment:cash);
            var paperPossible = !_checkoutWithoutPrinterAccepted && _settingsCache.GetBool("device.receipt_printer.enabled",false);
            var printerName = _settingsCache.GetText("device.receipt_printer.name","");
            _=OfferReceiptChoiceAsync(
                job,
                DigitalReceiptDocument.PaymentsFor(snapshot.Method,sale.EffectiveCashPortionCents,sale.EffectiveCardPortionCents,
                    _settingsCache.GetText("pay.cash.label","Bar"),_settingsCache.GetText("pay.card.label","Karte")),
                "SALE",
                sale.Id.ToString(),
                () =>
                {
                    if(paperPossible) return PrintReceiptAndReportAsync(job,printerName);
                    ScannerStatus.Text+=" · KEIN BONDRUCKER: Papierbeleg nicht möglich";
                    return Task.CompletedTask;
                });
        }
        else if(willAutoPrint)
        {
            _=PrintReceiptAndReportAsync(BuildReceiptPrintJob(sale,snapshot.Method,cashPayment:cash),
                _settingsCache.GetText("device.receipt_printer.name",""));
            // R104: still a "thank you" moment on the Kundendisplay even
            // when paper prints normally - no QR, just the total.
            _customerDisplayWindow?.ShowThankYou(sale.TotalCents, null);
        }
        else
            _customerDisplayWindow?.ShowThankYou(sale.TotalCents, null);

        if (GetImbissPickupMode() != "ORDER")
            _ = PrintKitchenForSaleAsync(sale);
    }

    private string GetImbissPickupMode()
    {
        var edition = (InstallationEdition.ReadLocked()
            ?? _settingsCache.GetText("business.mode", "KIOSK")).Trim().ToUpperInvariant();
        if (edition != "IMBISS") return "OFF";
        var mode=_settingsCache.GetText("imbiss.pickup_number.mode", "").Trim().ToUpperInvariant();
        if (mode is "OFF" or "SALE" or "ORDER") return mode;
        return _settingsCache.GetBool("imbiss.pickup_number.enabled", true) ? "SALE" : "OFF";
    }

    private bool IsImbissPickupNumberEnabled() => GetImbissPickupMode() != "OFF";

    // R76: gruppiert Küchenzeilen nach der Küchenstation ihrer Warengruppe
    // (Grill/Fritteuse/Getränke/Standard), damit jede Station an ihren
    // eigenen konfigurierten Drucker geroutet werden kann.
    private Dictionary<string,List<KitchenPrintLine>> BuildKitchenLinesByStation(IReadOnlyList<CartLine> lines)
    {
        var groups=new Dictionary<string,List<KitchenPrintLine>>();
        void Add(string station,KitchenPrintLine line)
        {
            if(!groups.TryGetValue(station,out var list)) groups[station]=list=new List<KitchenPrintLine>();
            list.Add(line);
        }
        foreach(var line in lines)
        {
            // R149: deposit is nothing the kitchen prepares.
            if(PfandProducts.IsDeposit(line.ProductId)) continue;

            var display=line.ProductName + (string.IsNullOrWhiteSpace(line.VariantName) ? "" : " · " + line.VariantName);
            var product=_catalog.Products.FirstOrDefault(x=>x.Id==line.ProductId);
            var category=product is null ? null : _catalog.Categories.FirstOrDefault(x=>x.Id==product.CategoryId);
            var parentStation=KitchenStations.Normalize(category?.KitchenStation);
            Add(parentStation,new KitchenPrintLine(display,line.Quantity));

            // R153: customer receipt stays ONE menu line, but kitchen/order
            // preparation must see the actual chosen articles, never every
            // possible option in the menu recipe.
            if(line.MenuComponents.Length>0)
            {
                foreach(var component in line.MenuComponents)
                {
                    var componentProduct=_catalog.Products.FirstOrDefault(x=>x.Id==component.ProductId);
                    var componentCategory=componentProduct is null
                        ? null
                        : _catalog.Categories.FirstOrDefault(x=>x.Id==componentProduct.CategoryId);
                    var station=KitchenStations.Normalize(componentCategory?.KitchenStation);
                    if(station==KitchenStations.None)
                        station=parentStation;

                    var componentText=string.IsNullOrWhiteSpace(component.ChoiceGroup)
                        ? component.Name
                        : $"{component.ChoiceGroup}: {component.Name}";
                    Add(station,new KitchenPrintLine(
                        componentText,
                        line.Quantity*component.Quantity,
                        true));
                }
            }
            else if(product?.ComboItems is {Count:>0})
            {
                // Legacy/static menu records without an R153 selection snapshot:
                // only fixed components are printable. Choice alternatives are
                // never all printed as if they had all been selected.
                foreach(var component in product.ComboItems.Where(x=>!x.IsChoice))
                    Add(parentStation,new KitchenPrintLine(
                        component.ComponentName,
                        line.Quantity*component.Quantity,
                        true));
            }
        }
        return groups;
    }

    // R76: liefert den Drucker für eine Küchenstation - eigener Stationsdrucker
    // wenn in den Einstellungen aktiviert und ausgewählt, sonst der eine
    // konfigurierte Standard-Küchendrucker.
    private string ResolveKitchenPrinter(string station)
    {
        if(station!=KitchenStations.None)
        {
            var prefix=KitchenStations.SettingsPrefix(station);
            if(_settingsCache.GetBool(prefix+".enabled",false))
            {
                var stationPrinter=_settingsCache.GetText(prefix+".name","");
                if(!string.IsNullOrWhiteSpace(stationPrinter)) return stationPrinter;
            }
        }
        return _settingsCache.GetText("device.kitchen_printer.name","");
    }

    private async Task PrintKitchenForSaleAsync(Sale sale)
    {
        if(!_settingsCache.GetBool("device.kitchen_printer.enabled",false) || !_settingsCache.GetBool("device.kitchen_printer.auto_print",true)) return;
        var groups=BuildKitchenLinesByStation(sale.Lines);
        var failures=new List<string>();
        foreach(var (station,printLines) in groups)
        {
            var printer=ResolveKitchenPrinter(station);
            if(string.IsNullOrWhiteSpace(printer)) continue;
            var note = station==KitchenStations.None ? "DIREKTVERKAUF" : "DIREKTVERKAUF · "+KitchenStations.DisplayName(station);
            try
            {
                using (_perf.Measure("printer.kitchen_spool"))
                    await _receiptPrinter.PrintKitchenAsync(
                        new KitchenPrintJob(
                            sale.CreatedAt,
                            sale.PickupNumber,
                            0,
                            sale.OperatorName,
                            printLines,
                            note),
                        printer);
            }
            catch(Exception ex){CrashLog.WriteException("Kitchen print "+station,ex);failures.Add(KitchenStations.DisplayName(station)+": "+ex.Message);}
        }
        if(failures.Count>0)
            ReportOperationalError("KÜCHENDRUCKER",$"Bon {sale.ReceiptNumber:000000} ist gespeichert; Küchenbon fehlgeschlagen: "+string.Join(" · ",failures),null,true);
    }

    private void ClearCompletedCart()
    {
        // R136: the Vorgang ended with its receipt; the empty cart is no abort.
        _tseVorgang.Release();
        _pendingCheckout=null; _engine.IsReadOnly=false; _engine.Clear();
        _activeParkedReceiptId=null; _activeParkNumber=null;
        _operationId=Guid.NewGuid().ToString("N");
        PrepareNextCustomer();
    }

    // R85: shows a short, customer-safe status line + Fehler-ID instead of a
    // raw .NET exception message. The exception text still reaches
    // CrashLog (and, via ReportOperationalError, the printed error slip)
    // for a technician - it must never land on the cashier screen itself.
    // printerRelated=true (default here) skips the physical error-slip
    // print for routine admin/background actions (CSV import, backup,
    // report export, ...) where handing the cashier a paper slip about it
    // would be pointless; pass false for operational KASSE-flow failures
    // that should still produce one.
    private void ShowOperationalError(string category, Exception ex, bool printerRelated = true)
    {
        // ReportOperationalError already writes ERROR-ID/CATEGORY/MESSAGE plus the
        // full exception to CrashLog - no need to log it a second time here.
        var errorId = ReportOperationalError(category, ex.Message, ex, printerRelated);
        ScannerStatus.Text = $"{category} FEHLGESCHLAGEN · Fehler-ID {errorId}";
    }

    private string ReportOperationalError(
        string category,
        string message,
        Exception? ex = null,
        bool printerRelated = false)
    {
        var errorId =
            DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + "-" +
            Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();

        var logMessage =
            $"ERROR-ID={errorId}; CATEGORY={category}; MESSAGE={message}";

        if (ex is null)
            CrashLog.Write(logMessage);
        else
            CrashLog.WriteException(logMessage, ex);

        if (!printerRelated)
            _ = PrintErrorSlipSafelyAsync(category, message, errorId);

        return errorId;
    }

    private async Task PrintErrorSlipSafelyAsync(
        string category,
        string message,
        string errorId)
    {
        try
        {
            if (!_settingsCache.GetBool("device.receipt_printer.enabled", false))
                return;

            var printerName =
                _settingsCache.GetText("device.receipt_printer.name", "");

            if (string.IsNullOrWhiteSpace(printerName))
                return;

            var safeMessage = message.Trim();
            if (safeMessage.Length > 500)
                safeMessage = safeMessage[..500];

            var job = new ErrorSlipPrintJob(
                DateTimeOffset.Now,
                category,
                safeMessage,
                errorId,
                _settingsCache.GetText("cash.register.number", "1"),
                _currentUser.Username);

            await _receiptPrinter.PrintErrorSlipAsync(job, printerName);
            CrashLog.Write(
                $"ERROR-SLIP-SPOOL-ACCEPTED; ERROR-ID={errorId}; PRINTER={printerName}");
        }
        catch (Exception printEx)
        {
            // Never recurse: a printer failure must not try to print its own error.
            CrashLog.WriteException(
                $"ERROR-SLIP-FAILED; ERROR-ID={errorId}; CATEGORY={category}",
                printEx);
        }
    }

    private void PrepareNextCustomer()
    {
        _scanNoEnterTimer.Stop();
        _scan = "";
        _scanProcessing = false;
        _lastScan = 0;
        _lastScannerKeyDownDigit = null;
        _lastScannerKeyDownDigitAt = 0;
        _imHaus = false;
        ClearNumericInput();
        CartList.SelectedIndex = -1;
        _productPage = 0;
        UpdateCart();
    }

    private ReceiptPrintJob BuildReceiptPrintJob(
        Sale sale,
        PaymentMethod method,
        bool isCopy = false,
        CashPaymentResult? cashPayment = null,
        string extraHeaderNote = "")
    {
        var address=string.Join(" ",
            new[]
            {
                _settingsCache.GetText("company.street","").Trim(),
                _settingsCache.GetText("company.zip","").Trim(),
                _settingsCache.GetText("company.city","").Trim()
            }.Where(x=>!string.IsNullOrWhiteSpace(x)));

        var tseActive=string.Equals(
            _settingsCache.GetText("tse.status",""),
            "AKTIV",
            StringComparison.OrdinalIgnoreCase);

        // R140: the processData exactly as signed (R130), for the QR code.
        string processData;
        try { processData = FiscalProcessData.KassenbelegText(sale); }
        catch (UnsupportedVatRateException) { processData = ""; }
        var tseMaster = _tseMasterData.TryGetValue(sale.TseSerialNumber, out var knownTse) ? knownTse : null;

        return new ReceiptPrintJob(
            sale.ReceiptNumber,
            sale.CreatedAt,
            _settingsCache.GetText("company.name","TOR POS Pro"),
            address,
            _settingsCache.GetText("company.tax_no",""),
            _settingsCache.GetText("company.vat_id",""),
            string.Join(
                Environment.NewLine,
                new[]
                {
                    extraHeaderNote,
                    isCopy ? "BON-KOPIE · BON-HISTORIE" : "",
                    _settingsCache.GetText("receipt.header","")
                }.Where(x => !string.IsNullOrWhiteSpace(x))),
            _settingsCache.GetText("receipt.footer","Vielen Dank für Ihren Einkauf!"),
            method switch
            {
                PaymentMethod.Cash => _settingsCache.GetText("pay.cash.label","Bar"),
                // R101: shows the real split, e.g. "Bar 5,00 € / Karte 3,50 €",
                // not just a generic "gemischt" label with the whole total.
                PaymentMethod.Mixed => $"{_settingsCache.GetText("pay.cash.label","Bar")} {Formatting.Money(sale.CashPortionCents)} / {_settingsCache.GetText("pay.card.label","Karte")} {Formatting.Money(sale.CardPortionCents)}",
                _ => _settingsCache.GetText("pay.card.label","Karte")
            },
            sale.DiscountCents,
            sale.TotalCents,
            sale.Lines,
            _fiscalReadiness?.ProductionAllowed != true,
            // R140: AEAO zu § 146a Nr. 2.4.4 Nr. 6 - the serial the TSE logged
            // for this receipt (§ 2 Satz 2 Nr. 8 KassenSichV) is the client id.
            string.IsNullOrWhiteSpace(sale.TseClientId) ? _fiscalReadiness?.EasSerial ?? "" : sale.TseClientId,
            string.IsNullOrWhiteSpace(sale.TseSerialNumber) ? _settingsCache.GetText("tse.serial","") : sale.TseSerialNumber,
            sale.TseTransactionNumber,
            long.TryParse(sale.TseSignatureCounter, out var tseCounter) ? tseCounter : 0,
            // R136: Vorgangsbeginn is the TSE's start log time; during an outage
            // the till's own start of the Vorgang (sales before R136: CreatedAt).
            sale.TseStartLogTime ?? sale.StartedAt ?? sale.CreatedAt,
            // R121: no "?? DateTimeOffset.Now" fallback. Vorgangsende is the
            // TSE's log time; substituting the PC clock presented a fabricated
            // value as TSE-derived and made the receipt validator's own
            // Vorgangsende check impossible to fail. When there is no TSE time
            // the sale is a TSE outage, the receipt says so, and the field is
            // legitimately absent.
            sale.TseLogTime,
            sale.TseSignature,
            sale.TseOutage,
            sale.OperatorName,
            cashPayment?.TenderedCents ?? 0,
            cashPayment?.ChangeCents ?? 0,
            _settingsCache.GetText("receipt.logo_path", ""),
            sale.PickupNumber,
            _settingsCache.GetBool("receipt.tse_qr_code.enabled", false),
            _settingsCache.GetBool("printer.auto_cut.enabled", true),
            // R177: the drawer is opened once by the committed payment path.
            // Never couple it to paper/digital receipt output or reopen it on
            // a delayed print/copy.
            false,
            // R137: DSFinV-K 2.7.2 - start of the first order transaction.
            sale.OrderStartedAt,
            TseClientId: sale.TseClientId,
            TseProcessType: FiscalProcessData.KassenbelegProcessType,
            TseProcessData: processData,
            TseStartLogTime: sale.TseStartLogTime,
            TseSignatureAlgorithm: tseMaster?.SignatureAlgorithm ?? "",
            TseLogTimeFormat: tseMaster?.LogTimeFormat ?? "",
            TsePublicKey: tseMaster?.PublicKeyBase64 ?? "",
            CashDrawerChannel: Math.Clamp(
                _settingsCache.GetInt("device.receipt_printer.drawer_channel", 1),
                1,
                2));
    }

    private async Task PrintReceiptAndReportAsync(
        ReceiptPrintJob job,
        string printerName)
    {
        try
        {
            using (_perf.Measure("printer.receipt_spool"))
                await _receiptPrinter.PrintReceiptAsync(job,printerName);

            ScannerStatus.Text = job.PickupNumber > 0
                ? $"ABHOLNR. {job.PickupNumber:000} · Bon {job.ReceiptNumber:000000} · an Druckwarteschlange übergeben"
                : $"Bon {job.ReceiptNumber:000000} · an Windows-Druckwarteschlange übergeben";
        }
        catch(Exception ex)
        {
            CrashLog.WriteException("MainWindow operation", ex);
            var errorId = ReportOperationalError(
                "DRUCKER",
                $"Bon {job.ReceiptNumber:000000} gespeichert, aber Druckauftrag fehlgeschlagen: {ex.Message}",
                ex,
                printerRelated: true);

            ScannerStatus.Text =
                $"Bon {job.ReceiptNumber:000000} GESPEICHERT · Druckerfehler · " +
                $"NICHT ERNEUT KASSIEREN · Fehler-ID {errorId}";
        }
    }

    /// <summary>R145: the digital receipt is switched on and TOR Cloud is set up on this till.</summary>
    private async Task<bool> DigitalReceiptOfferedAsync()
    {
        try
        {
            return await _digitalReceipts.IsAvailableAsync();
        }
        catch (Exception ex)
        {
            CrashLog.WriteException("Digital receipt availability", ex);
            return false;
        }
    }

    /// <summary>
    /// R145: the customer chooses Papierbeleg or Digitalbeleg (QR). The choice is
    /// recorded; choosing the digital receipt is the customer's consent (AEAO zu
    /// § 146a Nr. 2.5.3). The sale itself is complete - nothing here can undo or
    /// repeat it.
    /// </summary>
    private async Task OfferReceiptChoiceAsync(
        ReceiptPrintJob job,
        IReadOnlyList<DigitalReceiptPayment> payments,
        string entityType,
        string entityId,
        Func<Task> printPaper)
    {
        try
        {
            _customerDisplayWindow?.ShowThankYou(job.TotalCents, null);
            var choice = await new ReceiptChoiceWindow(job.TotalCents, job.FiscalTestMode).ShowDialog<ReceiptChoice>(this);
            if (choice == ReceiptChoice.Digitalbeleg)
            {
                await IssueDigitalReceiptAsync(job, payments, entityType, entityId, printPaper);
                return;
            }

            await _audit.WriteAsync(_currentUser.Username, "RECEIPT_CHANNEL", entityType, entityId, "Papierbeleg (Wahl des Kunden)");
            await printPaper();
        }
        catch (Exception ex)
        {
            ReportOperationalError("BELEG", "Die Belegausgabe ist fehlgeschlagen. Der Verkauf ist gespeichert - nicht erneut kassieren.", ex);
        }
    }

    /// <summary>
    /// R145: publishes the receipt to TOR Cloud and shows the QR code. When that
    /// is not possible the paper receipt follows at once - the receipt belongs to
    /// the end of the Vorgang (AEAO zu § 146a Nr. 2.5.7).
    /// </summary>
    private async Task IssueDigitalReceiptAsync(
        ReceiptPrintJob job,
        IReadOnlyList<DigitalReceiptPayment> payments,
        string entityType,
        string entityId,
        Func<Task> printPaper)
    {
        var window = new DigitalReceiptWindow();
        window.Show(this);
        ScannerStatus.Text = "Digitaler Kassenbon wird erstellt …";
        try
        {
            var document = DigitalReceiptDocument.From(job, payments);
            var publication = await _digitalReceipts.PublishAsync(document, DigitalReceiptDocument.ReferenceFor(job));
            // The link itself is the key to the receipt; only its id is logged.
            await _audit.WriteAsync(_currentUser.Username, "RECEIPT_CHANNEL", entityType, entityId,
                $"Digitalbeleg (Wahl des Kunden, AEAO zu § 146a Nr. 2.5.3); TOR Cloud {publication.ReceiptId}; abrufbar bis {publication.ExpiresAt:O}");
            window.ShowLink(publication.Url, publication.ExpiresAt);
            _customerDisplayWindow?.ShowThankYou(job.TotalCents, publication.Url);
            ScannerStatus.Text = $"Digitaler Kassenbon bereit · abrufbar bis {publication.ExpiresAt.ToLocalTime():dd.MM.yyyy}";
        }
        catch (Exception ex)
        {
            CrashLog.WriteException("Digital receipt", ex);
            var reason = ex is TimeoutException or HttpRequestException
                ? "TOR Cloud ist nicht erreichbar."
                : ex.Message;
            try
            {
                await _audit.WriteAsync(_currentUser.Username, "RECEIPT_DIGITAL_FAILED", entityType, entityId, reason);
            }
            catch (Exception auditEx)
            {
                CrashLog.WriteException("Digital receipt audit", auditEx);
            }
            window.ShowFailure(reason, "Der Papierbeleg wird ausgegeben.");
            ScannerStatus.Text = "Digitalbeleg nicht möglich · Papierbeleg";
            await printPaper();
        }
    }

    private async void OnReceiptHistoryClick(
        object? sender,
        RoutedEventArgs e)
    {
        if (_currentUser.IsTraining)
        {
            await ShowMenuInfoAsync("BON-HISTORIE · TRAINING", "Trainingsverkäufe werden nicht in der echten Bon-Historie gespeichert. Für echte Belege mit einem regulären Benutzer anmelden.");
            return;
        }
        if (!RequirePermission(UserPermissions.ViewReceiptHistory, "BON-HISTORIE") ||
            !RequireRealMode("BON-HISTORIE"))
            return;

        try
        {
            var history = new ReceiptHistoryWindow(_sales, _management);
            var selection = await history.ShowDialog<ReceiptHistorySelection?>(this);
            if (selection is null)
                return;

            if (selection.Action == ReceiptHistoryAction.FullStorno)
            {
                _directReceiptActionSaleId = selection.SaleId;
                OnBonStornoClick(sender, e);
                return;
            }

            if (selection.Action == ReceiptHistoryAction.PartialReturn)
            {
                _directReceiptActionSaleId = selection.SaleId;
                OnPartialReturnClick(sender, e);
                return;
            }

            if (!_settingsCache.GetBool("device.receipt_printer.enabled", false))
            {
                ScannerStatus.Text = "BON-HISTORIE: Bondrucker ist deaktiviert.";
                return;
            }

            var printerName = _settingsCache.GetText("device.receipt_printer.name", "");
            if (string.IsNullOrWhiteSpace(printerName))
            {
                ScannerStatus.Text = "BON-HISTORIE: Kein Bondrucker ausgewählt.";
                return;
            }

            var sale = await _sales.GetByIdAsync(selection.SaleId);
            if (sale is null)
            {
                ScannerStatus.Text = "BON-HISTORIE: Bon wurde nicht gefunden.";
                return;
            }

            var job = BuildReceiptPrintJob(sale, sale.PaymentMethod, isCopy: true);
            ScannerStatus.Text = $"Bon {sale.ReceiptNumber:000000} · Kopie wird gedruckt ...";

            await _audit.WriteAsync(
                _currentUser.Username,
                "RECEIPT_HISTORY_REPRINT",
                "SALE",
                sale.Id.ToString(),
                $"receipt={sale.ReceiptNumber}; payment={sale.PaymentMethod}");

            await _receiptPrinter.PrintReceiptAsync(job, printerName);
            ScannerStatus.Text =
                $"Bon {sale.ReceiptNumber:000000} · Kopie an Windows übergeben; Papierausdruck prüfen.";
        }
        catch (Exception ex)
        {
            CrashLog.WriteException("MainWindow operation", ex);
            var errorId = ReportOperationalError(
                "DRUCKER",
                "Bon-Historie konnte nicht verarbeitet werden: " + ex.Message,
                ex,
                printerRelated: true);

            ScannerStatus.Text = $"BON-HISTORIE · Fehler-ID {errorId}";
        }
    }

    private async void OnReceiptModeClick(
        object? sender,
        RoutedEventArgs e)
    {
        if (!RequirePermission(UserPermissions.Sale, "BON EIN/AUS") ||
            !RequireRealMode("BON EIN/AUS"))
            return;

        try
        {
            var current = _settingsCache.GetBool("receipt.auto_print", true);
            var selected = await new ReceiptModeWindow(current)
                .ShowDialog<bool?>(this);

            if (selected is null)
                return;

            await SetReceiptAutoPrintAsync(selected.Value);
        }
        catch (Exception ex)
        {
            ShowOperationalError("BON EIN/AUS", ex);
        }
    }

    private async Task SetReceiptAutoPrintAsync(bool enabled)
    {
        await _settings.SaveManyAsync(
            new Dictionary<string,string>
            {
                ["receipt.auto_print"] = enabled ? "true" : "false"
            });

        _settingsCache = await _settings.LoadAllAsync();
        RefreshReceiptModeButtons();

        await _audit.WriteAsync(
            _currentUser.Username,
            enabled ? "RECEIPT_AUTO_PRINT_ON" : "RECEIPT_AUTO_PRINT_OFF",
            "SETTINGS",
            "receipt.auto_print",
            enabled ? "true" : "false");

        ScannerStatus.Text = enabled
            ? "BON EIN · automatische Bonausgabe aktiviert."
            : "BON AUS · automatischer Druck deaktiviert. BON-HISTORIE bleibt verfügbar.";
    }

    private async void OnReceiptOnClick(
        object? sender,
        RoutedEventArgs e)
    {
        if (!RequirePermission(UserPermissions.Sale, "BON EIN") ||
            !RequireRealMode("BON EIN"))
            return;

        try
        {
            await SetReceiptAutoPrintAsync(true);
        }
        catch (Exception ex)
        {
            ShowOperationalError("BON EIN", ex);
        }
    }

    private async void OnReceiptOffClick(
        object? sender,
        RoutedEventArgs e)
    {
        if (!RequirePermission(UserPermissions.Sale, "BON AUS") ||
            !RequireRealMode("BON AUS"))
            return;

        try
        {
            await SetReceiptAutoPrintAsync(false);
        }
        catch (Exception ex)
        {
            ShowOperationalError("BON AUS", ex);
        }
    }

    private void RefreshReceiptModeButtons()
    {
        var isOn = _settingsCache.GetBool("receipt.auto_print", true);

        ReceiptModeText.Text = isOn
            ? "BON EIN/AUS · EIN"
            : "BON EIN/AUS · AUS";

        ReceiptModeButton.Background = new SolidColorBrush(
            Color.Parse(isOn ? "#0E6C42" : "#5E3038"));

        ReceiptModeButton.BorderBrush = new SolidColorBrush(
            Color.Parse(isOn ? "#53C996" : "#B36070"));
    }

    private async Task RefreshStockWarningAsync()
    {
        try
        {
            var low = await _management.GetLowStockAsync(100);
            StockWarningBadge.IsVisible = low.Count > 0;
            if (low.Count > 0)
                SetHeaderLabel(StockWarningText, $"BESTAND · {low.Count} NIEDRIG", $"BESTAND · {low.Count}", $"BESTAND {low.Count}");
            else
                SetHeaderLabel(StockWarningText, "BESTAND OK", "BESTAND OK", "BESTAND OK");
            ToolTip.SetTip(StockWarningBadge, low.Count == 0
                ? "Keine Mindestbestand-Warnung."
                : string.Join("\n", low.Take(8).Select(x => $"{x.Name}: {x.StockQuantity:0.###} / Min. {x.MinStockQuantity:0.###}")));
        }
        catch (Exception ex)
        {
            CrashLog.WriteException("Stock warning refresh", ex);
            StockWarningBadge.IsVisible = false;
        }
    }

    private string _selectedSettingsHubSection = "Kasse & Bedienung";

    private static void MarkHubNavActive(Button active, params Button[] buttons)
    {
        foreach (var button in buttons)
        {
            button.Classes.Remove("active");
            if (ReferenceEquals(button, active))
                button.Classes.Add("active");
        }
    }

    private void ShowGoodsHubSection(string section)
    {
        GoodsArticlesPanel.IsVisible = section == "ARTIKEL";
        GoodsInventoryPanel.IsVisible = section == "BESTAND";
        GoodsExchangePanel.IsVisible = section == "DATENAUSTAUSCH";
        var active = section switch
        {
            "BESTAND" => GoodsNavInventory,
            "DATENAUSTAUSCH" => GoodsNavExchange,
            _ => GoodsNavArticles
        };
        MarkHubNavActive(active, GoodsNavArticles, GoodsNavInventory, GoodsNavExchange);
    }

    private void OnGoodsHubSectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string section })
            ShowGoodsHubSection(section);
    }

    private void ShowCashHubSection(string section)
    {
        CashOperationPanel.IsVisible = section == "BETRIEB";
        CashReceiptPanel.IsVisible = section == "BON";
        CashCorrectionPanel.IsVisible = section == "KORREKTUREN";
        var active = section switch
        {
            "BON" => CashNavReceipt,
            "KORREKTUREN" => CashNavCorrection,
            _ => CashNavOperation
        };
        MarkHubNavActive(active, CashNavOperation, CashNavReceipt, CashNavCorrection);
    }

    private void OnCashHubSectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string section })
            ShowCashHubSection(section);
    }

    private void ShowReportsHubSection(string section)
    {
        ReportsDailyPanel.IsVisible = section == "TAGESKONTROLLE";
        ReportsSalesPanel.IsVisible = section == "VERKAUF";
        ReportsArchivePanel.IsVisible = section == "ARCHIV";
        ReportsMailPanel.IsVisible = section == "MAIL";
        ReportsFiscalPanel.IsVisible = section == "FINANZAMT";
        var active = section switch
        {
            "VERKAUF" => ReportsNavSales,
            "ARCHIV" => ReportsNavArchive,
            "MAIL" => ReportsNavMail,
            "FINANZAMT" => ReportsNavFiscal,
            _ => ReportsNavDaily
        };
        MarkHubNavActive(active, ReportsNavDaily, ReportsNavSales, ReportsNavArchive, ReportsNavMail, ReportsNavFiscal);
    }

    private void OnReportsHubSectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string section })
            ShowReportsHubSection(section);
    }

    private void ShowSettingsHubSection(string section)
    {
        _selectedSettingsHubSection = section;

        var (hint, cardOne, cardTwo) = section switch
        {
            "Firma & Bon" => ("Firmendaten, Bonkopf, Rechnung und sichtbare Belegdaten.", "Firmendaten", "Bon & Rechnung"),
            "Artikel & Steuern" => ("Steuersätze, Warengruppen und Artikelregeln.", "Steuersätze", "Warengruppen"),
            "Zahlung" => ("Bar, Karte, SumUp und weitere Zahlarten konfigurieren.", "Barzahlung", "Kartenzahlung / SumUp"),
            "Geräte" => ("Bondrucker, Schublade, Kundenanzeige, Scanner und Cloud.", "Drucker & Schublade", "Anzeige / Scanner / Cloud"),
            "Personal" => ("Benutzer, Rollen, Rechte und Bediener-PIN verwalten.", "Benutzer", "Rollen & Rechte"),
            "Berichte & E-Mail" => ("Berichte, TOR Mail und automatischen Versand einrichten.", "Berichte", "TOR Mail / Monatsversand"),
            "DATEV" => ("Kassenbuch Standard-ASCII und DATEV-Anbindungen verwalten.", "Kassenbuch Standard-ASCII", "Kassenarchiv / Steuerberater"),
            "Datensicherung" => ("Automatische Sicherung, Zielordner und Wiederherstellung.", "Automatische Sicherung", "Wiederherstellung"),
            "Software & Update" => ("Programmversion, Update-Kanal und Aktualisierung.", "Version", "Updates"),
            "Erweitert / Techniker" => ("Geschützter Bereich für TSE, Fiskal, Netzwerk und Diagnose.", "TSE / Fiskal", "Netzwerk / Diagnose"),
            _ => ("Alltagseinstellungen für Verkauf, Anzeige und Bedienung.", "Start & Anzeige", "Kassenfunktionen")
        };

        SettingsDetailTitle.Text = section;
        SettingsDetailHint.Text = hint;
        SettingsDetailCardOne.Text = cardOne;
        SettingsDetailCardTwo.Text = cardTwo;

        var active = section switch
        {
            "Firma & Bon" => SettingsNavCompany,
            "Artikel & Steuern" => SettingsNavArticle,
            "Zahlung" => SettingsNavPayment,
            "Geräte" => SettingsNavDevices,
            "Personal" => SettingsNavPersonnel,
            "Berichte & E-Mail" => SettingsNavMail,
            "DATEV" => SettingsNavDatev,
            "Datensicherung" => SettingsNavBackup,
            "Software & Update" => SettingsNavUpdate,
            "Erweitert / Techniker" => SettingsNavTechnician,
            _ => SettingsNavCash
        };
        MarkHubNavActive(active,
            SettingsNavCash, SettingsNavCompany, SettingsNavArticle, SettingsNavPayment,
            SettingsNavDevices, SettingsNavPersonnel, SettingsNavMail, SettingsNavDatev,
            SettingsNavBackup, SettingsNavUpdate, SettingsNavTechnician);
    }

    private void OnSettingsHubSectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string section })
            ShowSettingsHubSection(section);
    }

    private async void OnSettingsDetailOpenClick(object? sender, RoutedEventArgs e) =>
        await OpenSettingsPageAsync(_selectedSettingsHubSection);

    private void ShowMenuHub(string section)
    {
        MenuHubOverlay.IsVisible = true;
        GoodsHubPanel.IsVisible = section == "WAREN";
        SettingsHubPanel.IsVisible = section == "EINSTELLUNGEN";
        CashHubPanel.IsVisible = section == "KASSE";
        ReportsHubPanel.IsVisible = section == "BERICHTE";

        MenuHubTitleText.Text = section;
        MenuHubSubtitleText.Text = section switch
        {
            "WAREN" => "Artikel · Bestand · Datenaustausch",
            "EINSTELLUNGEN" => "Kasse · Unternehmen · System",
            "KASSE" => "Betrieb · Bon / Abrechnung · Korrekturen",
            "BERICHTE" => "Tageskontrolle · Verkauf / Bestand · Finanzamt",
            _ => "Schnellzugriff"
        };

        if (section == "WAREN") ShowGoodsHubSection("ARTIKEL");
        if (section == "EINSTELLUNGEN") ShowSettingsHubSection("Kasse & Bedienung");
        if (section == "KASSE") ShowCashHubSection("BETRIEB");
        if (section == "BERICHTE") ShowReportsHubSection("TAGESKONTROLLE");

        ScannerStatus.Text = $"{section} · Schnellzugriff geöffnet";
    }

    private void HideMenuHub()
    {
        MenuHubOverlay.IsVisible = false;
        GoodsHubPanel.IsVisible = false;
        SettingsHubPanel.IsVisible = false;
        CashHubPanel.IsVisible = false;
        ReportsHubPanel.IsVisible = false;
        ScannerStatus.Text = "Scanner bereit";
    }

    private void OnGoodsHubClick(object? sender, RoutedEventArgs e) =>
        ShowMenuHub("WAREN");

    private void OnSettingsHubClick(object? sender, RoutedEventArgs e) =>
        ShowMenuHub("EINSTELLUNGEN");

    private void OnCashHubClick(object? sender, RoutedEventArgs e) =>
        ShowMenuHub("KASSE");

    private void OnReportsHubClick(object? sender, RoutedEventArgs e) =>
        ShowMenuHub("BERICHTE");

    private void OnMenuHubBackClick(object? sender, RoutedEventArgs e) =>
        HideMenuHub();

    private async void OnPromotionsClick(object? sender, RoutedEventArgs e)
    {
        if (!RequirePermission(
            UserPermissions.ManageProducts,
            "ANGEBOTE / AKTIONEN"))
        {
            return;
        }

        await new PromotionManagementWindow(
            _promotions,
            _currentUser,
            PromotionScope.All)
            .ShowDialog(this);
    }

    private async void OnProductsClick(object? s,RoutedEventArgs e)
    {
        if (!RequirePermission(UserPermissions.ManageProducts, "STAMMDATEN"))
            return;

        await new ProductEditorWindow(_repo,_catalog,_images,_management,_promotions,_currentUser,"").ShowDialog<bool>(this);
        await _catalog.ReloadAsync();
        BuildCategories();
        await RefreshStockWarningAsync();

        if (_categoryId != 0 && _catalog.Categories.Any(x => x.Id == _categoryId))
        {
            if (ProductArea.IsVisible)
                SelectCategory(_categoryId);
        }
        else
        {
            _categoryId = 0;
            ShowCategoryOverview();
        }
    }


    private async Task ReloadCatalogAfterMasterDataAsync()
    {
        await _catalog.ReloadAsync();
        BuildCategories();
        await RefreshStockWarningAsync();

        if (_categoryId != 0 && _catalog.Categories.Any(x => x.Id == _categoryId))
        {
            if (ProductArea.IsVisible)
                SelectCategory(_categoryId);
        }
        else
        {
            _categoryId = 0;
            ShowCategoryOverview();
        }
    }

    private async void OnDuplicatesClick(object? sender, RoutedEventArgs e)
    {
        if (!RequirePermission(UserPermissions.ManageProducts, "DUPLIKATE"))
            return;

        try
        {
            var rows = await _management.GetDuplicatesAsync();
            await new DuplicateArticlesWindow(rows).ShowDialog(this);
        }
        catch (Exception ex)
        {
            ShowOperationalError("DUPLIKATE", ex);
        }
    }

    private async void OnLabelsClick(object? sender, RoutedEventArgs e)
    {
        if (!RequirePermission(UserPermissions.ManageProducts, "ETIKETTEN DRUCKEN"))
            return;

        try
        {
            ScannerStatus.Text = "Artikel-Etiketten werden erstellt ...";
            var path = await _management.CreateArticleLabelsPdfAsync();
            BusinessManagementService.OpenFile(path);
            ScannerStatus.Text = $"Etiketten-PDF erstellt: {path}";
        }
        catch (Exception ex)
        {
            ShowOperationalError("ETIKETTEN", ex);
        }
    }

    private async void OnImportArticlesClick(object? sender, RoutedEventArgs e)
    {
        if (!RequirePermission(UserPermissions.ManageProducts, "ARTIKEL IMPORTIEREN"))
            return;

        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "TOR Artikel-CSV importieren",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("CSV") { Patterns = ["*.csv", "*.txt"] }
                ]
            });
        var file = files.FirstOrDefault();
        if (file is null)
            return;

        try
        {
            var count = await _management.ImportArticlesCsvAsync(
                file.Path.LocalPath,
                _currentUser.Username);
            await ReloadCatalogAfterMasterDataAsync();
            ScannerStatus.Text = $"Artikelimport abgeschlossen: {count} Datensätze.";
        }
        catch (Exception ex)
        {
            ShowOperationalError("ARTIKELIMPORT", ex);
        }
    }

    private async void OnExportArticlesClick(object? sender, RoutedEventArgs e)
    {
        if (!RequirePermission(UserPermissions.ManageProducts, "ARTIKEL EXPORTIEREN"))
            return;

        var file = await StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "TOR Artikel exportieren",
                SuggestedFileName = $"TOR-Artikel-{DateTime.Now:yyyyMMdd}.csv",
                DefaultExtension = "csv",
                FileTypeChoices =
                [
                    new FilePickerFileType("CSV") { Patterns = ["*.csv"] }
                ]
            });
        if (file is null)
            return;

        try
        {
            var path = await _management.ExportArticlesCsvAsync(file.Path.LocalPath);
            ScannerStatus.Text = $"Artikelexport erstellt: {path}";
        }
        catch (Exception ex)
        {
            ShowOperationalError("ARTIKELEXPORT", ex);
        }
    }

    private async void OnImportDatabaseClick(object? sender, RoutedEventArgs e)
    {
        if (!RequirePermission(UserPermissions.ManageProducts, "IMPORT AUS DATENBANK"))
            return;

        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "TOR POS Datenbank auswählen",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("SQLite / TOR POS") { Patterns = ["*.db", "*.sqlite", "*.sqlite3"] }
                ]
            });
        var file = files.FirstOrDefault();
        if (file is null)
            return;

        try
        {
            var count = await _management.ImportArticlesFromDatabaseAsync(
                file.Path.LocalPath,
                _currentUser.Username);
            await ReloadCatalogAfterMasterDataAsync();
            ScannerStatus.Text = $"Datenbankimport: {count} Artikel übernommen/aktualisiert.";
        }
        catch (Exception ex)
        {
            ShowOperationalError("DATENBANKIMPORT", ex);
        }
    }

    private async void OnInventoryClick(object? sender, RoutedEventArgs e)
    {
        if (!RequirePermission(UserPermissions.ManageProducts, "INVENTUR"))
            return;

        await new InventoryWindow(_management, _currentUser, _receiptPrinter, _settings).ShowDialog(this);
        await RefreshStockWarningAsync();
    }

    private void OnProgramExitMenuClick(object? sender, RoutedEventArgs e)
    {
        if (_engine.Cart.Count > 0)
        {
            ScannerStatus.Text = "PROGRAMM BEENDEN: Aktuellen Verkauf zuerst kassieren, PARKEN oder mit C leeren.";
            return;
        }

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            Close();
        else
            Close();
    }

    private async Task OpenSettingsPageAsync(string page)
    {
        if (!_currentUser.IsAdmin)
        {
            ScannerStatus.Text = "Keine Berechtigung für Einstellungen.";
            return;
        }

        await _windowFactory
            .CreateSettingsWindow(_currentUser, page)
            .ShowDialog<bool>(this);

        await ReloadSettingsAsync();
        await RefreshFiscalStatusAsync();
    }

    private async void OnCashOperationSettingsMenuClick(object? sender, RoutedEventArgs e) =>
        await OpenSettingsPageAsync("Kasse & Bedienung");

    private async void OnCompanyReceiptSettingsMenuClick(object? sender, RoutedEventArgs e) =>
        await OpenSettingsPageAsync("Firma & Bon");

    private async void OnArticleTaxSettingsMenuClick(object? sender, RoutedEventArgs e) =>
        await OpenSettingsPageAsync("Artikel & Steuern");

    private async void OnGroupedPaymentSettingsMenuClick(object? sender, RoutedEventArgs e) =>
        await OpenSettingsPageAsync("Zahlung");

    private async void OnGroupedDeviceSettingsMenuClick(object? sender, RoutedEventArgs e) =>
        await OpenSettingsPageAsync("Geräte");

    private async void OnGroupedPersonnelSettingsMenuClick(object? sender, RoutedEventArgs e) =>
        await OpenSettingsPageAsync("Personal");

    private async void OnGroupedReportsEmailSettingsMenuClick(object? sender, RoutedEventArgs e) =>
        await OpenSettingsPageAsync("Berichte & E-Mail");

    private async void OnDatevSettingsMenuClick(object? sender, RoutedEventArgs e) =>
        await OpenSettingsPageAsync("DATEV");

    private async void OnGroupedBackupSettingsMenuClick(object? sender, RoutedEventArgs e) =>
        await OpenSettingsPageAsync("Datensicherung");

    private async void OnSoftwareUpdateSettingsMenuClick(object? sender, RoutedEventArgs e) =>
        await OpenSettingsPageAsync("Software & Update");

    private async void OnTechnicianSettingsMenuClick(object? sender, RoutedEventArgs e) =>
        await OpenSettingsPageAsync("Erweitert / Techniker");

    // Legacy handlers bleiben erhalten, damit ältere interne Aufrufe nicht brechen.
    private async void OnProgramSettingsMenuClick(object? sender, RoutedEventArgs e) =>
        await OpenSettingsPageAsync("Allgemein");

    private async void OnFunctionSettingsMenuClick(object? sender, RoutedEventArgs e) =>
        await OpenSettingsPageAsync("Funktionen");

    private async void OnPersonnelSettingsMenuClick(object? sender, RoutedEventArgs e)
    {
        if (!_currentUser.IsAdmin)
        {
            ScannerStatus.Text = "Personalverwaltung ist nur für Admin verfügbar.";
            return;
        }
        await new UserManagementWindow(_authentication, _currentUser).ShowDialog<bool>(this);
    }

    private void OnArticleOptionsMenuClick(object? sender, RoutedEventArgs e) =>
        OnProductsClick(sender, e);

    private async void OnTaxSettingsMenuClick(object? sender, RoutedEventArgs e) =>
        await OpenSettingsPageAsync("Steuern");

    private async void OnPaymentSettingsMenuClick(object? sender, RoutedEventArgs e) =>
        await OpenSettingsPageAsync("Zahlarten");

    private async void OnEanSettingsMenuClick(object? sender, RoutedEventArgs e) =>
        await OpenSettingsPageAsync("Scanner");

    private async void OnStornoReasonsMenuClick(object? sender, RoutedEventArgs e) =>
        await OpenSettingsPageAsync("Funktionen");

    private async void OnBackupSettingsMenuClick(object? sender, RoutedEventArgs e) =>
        await OpenSettingsPageAsync("Datensicherung");

    private async void OnDeviceManagerMenuClick(object? sender, RoutedEventArgs e) =>
        await OpenSettingsPageAsync("Geräte-Manager");

    private async void OnDiagnosticsClick(object? sender, RoutedEventArgs e)
    {
        if (!_currentUser.IsAdmin)
        {
            ScannerStatus.Text = "Systemdiagnose ist nur für Admin verfügbar.";
            return;
        }

        await _windowFactory
            .CreateDiagnosticsWindow()
            .ShowDialog(this);
    }

    private async void OnReceiptSettingsMenuClick(object? sender, RoutedEventArgs e) =>
        await OpenSettingsPageAsync("Bon & Rechnung");

    private async void OnPfandSettingsMenuClick(object? sender, RoutedEventArgs e)
    {
        if (!_currentUser.IsAdmin)
            return;
        await new PfandLeergutSettingsWindow(_settings).ShowDialog(this);
        await ReloadSettingsAsync();
    }

    private async void OnDatabaseBackupMenuClick(object? sender, RoutedEventArgs e)
    {
        if (!_currentUser.IsAdmin)
            return;

        try
        {
            var directory = _settingsCache.GetText("backup.directory", "");
            var path = await _backup.CreateBackupAsync(directory);
            await _audit.WriteAsync(
                _currentUser.Username,
                "DATABASE_BACKUP_MANUAL",
                "DATABASE",
                Path.GetFileName(path),
                path);
            ScannerStatus.Text = $"Datenbank-Sicherung erstellt: {path}";
        }
        catch (Exception ex)
        {
            ShowOperationalError("DATENSICHERUNG", ex);
        }
    }

    private bool RequireFinancialReport(string function, bool zPermissionAllowed = true)
    {
        if (_currentUser.IsTraining)
        {
            ScannerStatus.Text = $"{function} ist im TRAININGSMODUS deaktiviert.";
            return false;
        }

        if (_currentUser.IsAdmin || (zPermissionAllowed && _currentUser.Can(UserPermissions.ZReport)))
            return true;

        ScannerStatus.Text = $"Keine Berechtigung für {function}.";
        return false;
    }

    private async Task ShowReportAsync(
        Task<ReportDocument> reportTask,
        string? note = null)
    {
        try
        {
            var report = await reportTask;
            await new TextReportWindow(_management, report, _receiptPrinter, _settings, note).ShowDialog(this);
        }
        catch (Exception ex)
        {
            ShowOperationalError("BERICHT", ex);
        }
    }

    private async void OnXReportMenuClick(object? sender, RoutedEventArgs e)
    {
        if (!RequireFinancialReport("X-BERICHT"))
            return;

        await ShowReportAsync(
            _management.BuildXReportAsync(),
            "X-Bericht ist ein Zwischenbericht. Er löst keinen Tagesabschluss aus und verändert den Z-Zähler nicht.");
    }

    private async void OnCashCountReportMenuClick(object? sender, RoutedEventArgs e)
    {
        if (!RequireFinancialReport("KASSENSTURZ"))
            return;

        try
        {
            // R139: the calculated cash runs on from the last confirmed
            // Kassensturz (DSFinV-K Anfangsbestand/Geldtransit); the difference
            // found is booked as DifferenzSollIst and, on a till that books for
            // real, secured by the TSE like any Einlage/Entnahme (R134).
            var production = !IsSimulation;
            var opening = long.TryParse(_settingsCache.GetText("cash.start.cents", "0"), out var start)
                ? start
                : 0;

            CashCountBooking? booking = null;
            string note = "";
            while (booking is null)
            {
                var counted = await new MoneyInputWindow("Kassensturz", "Gezählter Bargeldbestand in €")
                    .ShowDialog<long?>(this);
                if (counted is null)
                    return;

                var balance = await _cashMovements.GetCashBalanceAsync(opening, production);
                var decision = await new CashCountConfirmWindow(balance.ExpectedCents, counted.Value, production)
                    .ShowDialog<CashCountDecision?>(this);
                if (decision is null)
                {
                    // Nothing is booked, but the count that was seen is documented.
                    await _audit.WriteAsync(_currentUser.Username, "CASH_COUNT_ABANDONED", "CASH_MOVEMENT", "",
                        $"soll_cents={balance.ExpectedCents}; ist_cents={counted.Value}; production={production}");
                    return;
                }
                if (decision.Recount)
                    continue;

                note = decision.Note;
                booking = await _cashMovements.BookCashCountAsync(counted.Value, opening, note, production, _currentUser.Username);
            }

            var tseNote = "";
            if (booking.Difference is { } differenceMovement && production)
            {
                var signed = await new CashMovementFiscalSigningService(_tseFailSafe, _settings, _cashMovements)
                    .SignAsync(differenceMovement, _currentUser.Username);
                tseNote = signed.Signed ? " · TSE-signiert" : " · TSE-AUSFALL dokumentiert";
                await RefreshTseOutageBadgeAsync();
            }

            var lines = new List<string>
            {
                $"Datum / Zeit: {DateTime.Now:dd.MM.yyyy HH:mm:ss}",
                $"Bediener: {_currentUser.Username}",
                // R123: printable report - German amounts regardless of Windows culture.
                $"Soll-Bargeld: {GermanFormat.Eur(booking.ExpectedCents)}",
                $"Ist-Bargeld: {GermanFormat.Eur(booking.CountedCents)}",
                $"Differenz: {GermanFormat.Eur(booking.DifferenceCents)}",
                booking.Difference is { } booked
                    ? $"Gebucht: {CashBusinessCases.Label(CashBusinessCase.DifferenzSollIst, booked.Kind)}{tseNote}"
                    : "Keine Differenz"
            };
            if (!string.IsNullOrWhiteSpace(note))
                lines.Add($"Bemerkung: {note.Trim()}");
            if (!production)
                lines.Add("TESTBETRIEB - keine fiskalische Buchung");

            var report = new ReportDocument("KASSENSTURZ", lines.ToArray(), DateTimeOffset.Now);
            await new TextReportWindow(_management, report, _receiptPrinter, _settings).ShowDialog(this);
        }
        catch (Exception ex)
        {
            ShowOperationalError("KASSENSTURZ", ex);
        }
    }

    private async void OnCashJournalMenuClick(object? sender, RoutedEventArgs e)
    {
        if (!RequireFinancialReport("KASSENJOURNAL"))
            return;
        await ShowReportAsync(_management.BuildCashJournalAsync());
    }

    private async void OnBookingExportMenuClick(object? sender, RoutedEventArgs e)
    {
        if (!RequireFinancialReport("EXPORT BUCHUNGSDATEN", zPermissionAllowed: false))
            return;

        var file = await StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Buchungsdaten exportieren",
                SuggestedFileName = $"TOR-Buchungsdaten-{DateTime.Now:yyyyMMdd}.csv",
                DefaultExtension = "csv",
                FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }]
            });
        if (file is null)
            return;

        try
        {
            var path = await _management.ExportBookingDataAsync(file.Path.LocalPath);
            ScannerStatus.Text = $"Buchungsdaten exportiert: {path}";
        }
        catch (Exception ex)
        {
            ShowOperationalError("EXPORT BUCHUNGSDATEN", ex);
        }
    }

    private async void OnInventoryReportMenuClick(object? sender, RoutedEventArgs e)
    {
        if (!RequireFinancialReport("WARENBESTAND", zPermissionAllowed: false) &&
            !RequirePermission(UserPermissions.ManageProducts, "WARENBESTAND"))
            return;
        await ShowReportAsync(_management.BuildInventoryReportAsync());
    }

    private async void OnZArchiveMenuClick(object? sender, RoutedEventArgs e)
    {
        if (!RequireFinancialReport("Z-ABSCHLUSS-JOURNAL"))
            return;
        await new ZArchiveWindow(_management, _receiptPrinter, _settings).ShowDialog(this);
    }

    private async void OnReportCenterClick(object? sender, RoutedEventArgs e)
    {
        if (!RequireFinancialReport("BERICHTSZENTRUM", zPermissionAllowed: false)) return;
        var panel = new StackPanel { Margin = new Thickness(22), Spacing = 12 };
        var window = new Window { Title = "Berichte · Tageskontrolle", Width = 760, Height = 650,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = panel } };
        panel.Children.Add(new TextBlock { Text = "BERICHTE · TAGESKONTROLLE", FontSize = 24 });
        panel.Children.Add(new TextBlock { Text = "Auswertungen öffnen, als PDF speichern oder direkt auf Bon-/A4-Drucker ausgeben. Ein Bericht führt keinen Tagesabschluss aus.", TextWrapping = TextWrapping.Wrap });
        void Add(string title, Func<Task<ReportDocument>> create)
        {
            var button = new Button { Content = title, MinHeight = 50, HorizontalAlignment = HorizontalAlignment.Stretch };
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                try { await new TextReportWindow(_management, await create(), _receiptPrinter, _settings).ShowDialog(window); }
                catch (Exception ex) { CrashLog.WriteException("Report center", ex); panel.Children.Add(new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap }); }
                finally { button.IsEnabled = true; }
            };
            panel.Children.Add(button);
        }
        Add("HEUTE / WOCHE / MONAT · Bar & Karte", () => _management.BuildTurnoverSummaryAsync());
        Add("X-BERICHT · Aktueller Kassenstand", () => _management.BuildXReportAsync());
        Add("KASSENJOURNAL · Einlagen & Entnahmen", () => _management.BuildCashJournalAsync());
        Add("MONATSUMSATZ · letzte 24 Monate", () => _management.BuildMonthlyTurnoverAsync());
        Add("VERKAUFSSTATISTIK · Artikel", () => _management.BuildSalesStatisticsAsync());
        Add("BEDIENERABRECHNUNG", () => _management.BuildOperatorSettlementAsync());
        Add("STORNOBERICHT", () => _management.BuildStornoReportAsync());
        Add("WARENBESTAND", () => _management.BuildInventoryReportAsync());
        Add("Z-ARCHIV · vorhandene Abschlüsse", () => _management.BuildZArchiveSummaryAsync());

        var exportAll = new Button
        {
            Content = "ALLE BERICHTE ALS PDF SPEICHERN",
            MinHeight = 54,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontWeight = FontWeight.Bold,
            Background = new SolidColorBrush(Color.Parse("#2CC4A7")),
            Foreground = new SolidColorBrush(Color.Parse("#07140F"))
        };
        exportAll.Click += async (_, _) =>
        {
            if (!exportAll.IsEnabled) return;
            exportAll.IsEnabled = false;
            try
            {
                var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Zielordner für alle Berichte wählen",
                    AllowMultiple = false
                });
                var selected = folders.FirstOrDefault();
                if (selected is null) return;
                var reports = await _management.BuildCurrentReportBundleAsync();
                var folder = _management.CreatePdfPackage(reports, selected.Path.LocalPath, $"TOR-Berichte-{DateTime.Now:yyyyMMdd-HHmmss}");
                panel.Children.Add(new TextBlock { Text = "Alle Berichte gespeichert: " + folder, TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.AccentTeal });
                BusinessManagementService.OpenFile(folder);
            }
            catch (Exception ex)
            {
                CrashLog.WriteException("Report bundle export", ex);
                panel.Children.Add(new TextBlock { Text = "PDF-Export fehlgeschlagen: " + ex.Message, TextWrapping = TextWrapping.Wrap });
            }
            finally { exportAll.IsEnabled = true; }
        };
        panel.Children.Add(exportAll);

        var close = new Button { Content = "SCHLIESSEN", MinHeight = 48 };
        close.Click += (_, _) => window.Close(); panel.Children.Add(close);
        await window.ShowDialog(this);
    }

    private async void OnTurnoverReportsMenuClick(object? sender, RoutedEventArgs e)
    {
        if (!RequireFinancialReport("UMSATZBERICHTE", zPermissionAllowed: false))
            return;
        await ShowReportAsync(_management.BuildTurnoverSummaryAsync());
    }

    private async void OnMonthlyTurnoverMenuClick(object? sender, RoutedEventArgs e)
    {
        if (!RequireFinancialReport("MONATSUMSATZ", zPermissionAllowed: false))
            return;
        await ShowReportAsync(_management.BuildMonthlyTurnoverAsync());
    }

    private async void OnSalesStatisticsMenuClick(object? sender, RoutedEventArgs e)
    {
        if (!RequireFinancialReport("VERKAUFSSTATISTIK", zPermissionAllowed: false))
            return;
        await ShowReportAsync(_management.BuildSalesStatisticsAsync());
    }

    private async void OnOperatorSettlementMenuClick(object? sender, RoutedEventArgs e)
    {
        if (!RequireFinancialReport("BEDIENERABRECHNUNG", zPermissionAllowed: false))
            return;
        await ShowReportAsync(_management.BuildOperatorSettlementAsync());
    }

    private async void OnPersonnelMonitoringMenuClick(object? sender, RoutedEventArgs e)
    {
        if (!_currentUser.IsAdmin)
        {
            ScannerStatus.Text = "Personalüberwachung ist nur für Admin verfügbar.";
            return;
        }
        await ShowReportAsync(
            _management.BuildPersonnelMonitoringAsync(),
            "Es werden ausschließlich protokollierte POS-Aktionen ausgewertet. Keine Tastatur-, Bildschirm- oder private Aktivitätsüberwachung.");
    }

    private async void OnStornoReportMenuClick(object? sender, RoutedEventArgs e)
    {
        if (!RequireFinancialReport("STORNOBERICHT", zPermissionAllowed: false))
            return;
        await ShowReportAsync(_management.BuildStornoReportAsync());
    }

    /// <summary>R144: the data for the notification under § 146a Abs. 4 AO (Mein ELSTER).</summary>
    private async void OnKassenmeldungMenuClick(object? sender, RoutedEventArgs e)
    {
        if (!_currentUser.IsAdmin)
        {
            ScannerStatus.Text = "Kassenmeldung ist nur für Admin verfügbar.";
            return;
        }

        try
        {
            var report = await _management.BuildKassenmeldungAsync();
            var path = _management.CreatePdf(report);
            await _audit.WriteAsync(_currentUser.Username, "KASSENMELDUNG_PDF_CREATED", "FISCAL_DOCUMENTATION", Path.GetFileName(path), path);
            ScannerStatus.Text = $"Kassenmeldung erstellt: {path}";
            await new TextReportWindow(
                _management,
                report,
                _receiptPrinter,
                _settings,
                $"Daten für Mein ELSTER - TOR übermittelt nichts selbst. PDF gespeichert: {path}.")
                .ShowDialog(this);
        }
        catch (Exception ex)
        {
            ShowOperationalError("KASSENMELDUNG", ex);
        }
    }

    private async void OnProgrammingProtocolMenuClick(object? sender, RoutedEventArgs e)
    {
        if (!_currentUser.IsAdmin)
        {
            ScannerStatus.Text = "Programmierungsprotokoll ist nur für Admin verfügbar.";
            return;
        }

        try
        {
            var report = await _management.BuildProgrammingProtocolAsync(
                _commercialLicense,
                _tseProvider);
            var path = _management.CreatePdf(report);
            await _audit.WriteAsync(
                _currentUser.Username,
                "PROGRAMMING_PROTOCOL_PDF_CREATED",
                "FISCAL_DOCUMENTATION",
                Path.GetFileName(path),
                path);
            ScannerStatus.Text = $"Programmierungsprotokoll erstellt: {path}";
            await new TextReportWindow(
                _management,
                report,
                _receiptPrinter,
                _settings,
                $"Dokumentations-PDF wurde zusätzlich gespeichert: {path}. Für einen Papierausdruck kann 58 mm, 80 mm oder A4 und der gewünschte Windows-Drucker gewählt werden.")
                .ShowDialog(this);
        }
        catch (Exception ex)
        {
            ShowOperationalError("PROGRAMMIERUNGSPROTOKOLL", ex);
        }
    }

    private async void OnFiscalAuditMenuClick(object? sender, RoutedEventArgs e)
    {
        if (!_currentUser.IsAdmin)
            return;

        try
        {
            var fiscal = await _compliance.CheckAsync();
            var lines = new List<string>
            {
                $"Modus: {fiscal.Mode}",
                $"Produktiv freigegeben: {(fiscal.ProductionAllowed ? "JA" : "NEIN")}",
                $"eAS: {fiscal.EasSerial}",
                $"DSFinV-K: {fiscal.DsfinvkVersion}",
                $"Blockierende Punkte: {fiscal.BlockingCount}",
                ""
            };
            lines.AddRange(fiscal.Items.Select(x =>
                $"{(x.Ready ? "OK" : "OFFEN")} | {x.Code} | {x.Title} | {x.Detail}"));
            var report = new ReportDocument("FISKAL-PRÜFUNG", lines, DateTimeOffset.Now);
            await new TextReportWindow(
                _management,
                report,
                _receiptPrinter,
                _settings,
                fiscal.ProductionAllowed
                    ? "Fiskal-Prüfung meldet produktionsbereit."
                    : "Produktive Freigabe bleibt gesperrt, solange Pflichtpunkte offen sind.")
                .ShowDialog(this);
        }
        catch (Exception ex)
        {
            ShowOperationalError("FISKAL-PRÜFUNG", ex);
        }
    }

    private async void OnGdpduToolsMenuClick(object? sender, RoutedEventArgs e)
    {
        if (!_currentUser.IsAdmin)
            return;

        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Zielordner für GDPdU/GoBD Prüf-Unterlagen",
                AllowMultiple = false
            });
        var folder = folders.FirstOrDefault();
        if (folder is null)
            return;

        try
        {
            var path = await _management.ExportGdpduAuditPackageAsync(folder.Path.LocalPath);
            ScannerStatus.Text = $"GDPdU/GoBD Prüf-Unterlagen erstellt: {path}";
        }
        catch (Exception ex)
        {
            ShowOperationalError("GDPDU-TOOLS", ex);
        }
    }

    private async void OnDatevAsciiJournalMenuClick(object? sender, RoutedEventArgs e)
    {
        if (!_currentUser.IsAdmin)
        {
            ScannerStatus.Text = "DATEV Kassenbuch Exportjournal ist nur für Admin verfügbar.";
            return;
        }

        await ShowReportAsync(
            _datevAscii.BuildJournalReportAsync(),
            "Kostenfreier DATEV Kassenbuch Standard-ASCII/CSV Export · inklusive E-Mail-Status.");
    }

    private async void OnDatevJournalMenuClick(object? sender, RoutedEventArgs e)
    {
        if (!_currentUser.IsAdmin)
        {
            ScannerStatus.Text = "DATEV Kassenarchiv Journal ist nur für Admin verfügbar.";
            return;
        }

        await ShowReportAsync(
            _datevKassenarchiv.BuildJournalReportAsync(),
            "DATEV-Outbox: lokale Pakete, Hash, Status und Fehler. Online-Transport ist noch nicht freigeschaltet.");
    }

    private async void OnDsfinvkExportMenuClick(object? sender, RoutedEventArgs e)
    {
        if (!_currentUser.IsAdmin)
            return;

        var from = new DateTimeOffset(
            new DateTime(DateTime.Now.Year, 1, 1),
            TimeZoneInfo.Local.GetUtcOffset(new DateTime(DateTime.Now.Year, 1, 1)));
        var to = DateTimeOffset.Now;
        try
        {
            var preflight = await _dsfinvkExport.ValidateAsync(from, to);
            if (!preflight.Ready)
            {
                var report = new ReportDocument(
                    "DSFINV-K 2.4 PRÜFUNG",
                    new[]
                    {
                        $"Zeitraum: {from:dd.MM.yyyy} - {to:dd.MM.yyyy}",
                        "Exportstatus: GESPERRT",
                        ""
                    }.Concat(preflight.Issues.Select(x => $"{x.Code}: {x.Message}")).ToArray(),
                    DateTimeOffset.Now);
                await new TextReportWindow(
                    _management,
                    report,
                    _receiptPrinter,
                    _settings,
                    "TOR erzeugt absichtlich keinen unvollständigen oder nur scheinbar DSFinV-K-konformen Prüfdatensatz.")
                    .ShowDialog(this);
                return;
            }

            // R131: the export exists now. What TOR does not record yet is
            // shown before the folder is chosen and also written into the
            // export's own TOR-EXPORTPROTOKOLL.txt.
            if (preflight.Issues.Count > 0)
            {
                var hints = new ReportDocument(
                    "DSFINV-K 2.4 EXPORT - HINWEISE",
                    new[]
                    {
                        $"Zeitraum: {from:dd.MM.yyyy} - {to:dd.MM.yyyy}",
                        "Exportstatus: BEREIT",
                        "Die folgenden Hinweise werden mit in den Export geschrieben.",
                        ""
                    }.Concat(preflight.Issues.Select(x => $"{x.Code}: {x.Message}")).ToArray(),
                    DateTimeOffset.Now);
                await new TextReportWindow(
                    _management,
                    hints,
                    _receiptPrinter,
                    _settings,
                    "Nach dem Schließen dieses Fensters wird der Zielordner gewählt.")
                    .ShowDialog(this);
            }

            var folders = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = "DSFinV-K Zielordner",
                    AllowMultiple = false
                });
            var folder = folders.FirstOrDefault();
            if (folder is null)
                return;

            var path = await _dsfinvkExport.ExportAsync(from, to, folder.Path.LocalPath);
            await _audit.WriteAsync(_currentUser.Username, "DSFINVK_EXPORT", "DSFINVK", Path.GetFileName(path),
                $"from={from:O}; to={to:O}; hints={preflight.Issues.Count}");
            ScannerStatus.Text = $"DSFinV-K Export erstellt: {path}";
        }
        catch (Exception ex)
        {
            ShowOperationalError("DSFINV-K", ex);
        }
    }

    private async void OnTseExportMenuClick(object? sender, RoutedEventArgs e)
    {
        if (!_currentUser.IsAdmin)
            return;

        if (!_tseProvider.ExportAvailable)
        {
            ScannerStatus.Text = "TSE Export nicht verfügbar - Swissbit SDK / TSE prüfen.";
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "TSE TAR-Export speichern",
                SuggestedFileName = $"TSE-Export-{DateTime.Now:yyyyMMdd-HHmmss}.tar",
                DefaultExtension = "tar",
                FileTypeChoices = [new FilePickerFileType("TSE TAR") { Patterns = ["*.tar"] }]
            });
        if (file is null)
            return;

        try
        {
            var result = await _tseProvider.ExportTarAsync(file.Path.LocalPath);
            var masterData = "";
            if (result.Success)
            {
                // R133: the export carries the TSE's certificate, public key,
                // signature algorithm and log time format (Stamm_TSE).
                var serials = await new TseMasterDataRepository(new SqliteDatabase(AppPaths.DatabasePath), _audit)
                    .ImportFromTarAsync(file.Path.LocalPath, _currentUser.Username);
                masterData = serials.Count > 0
                    ? " · TSE-Stammdaten übernommen"
                    : " · keine TSE-Stammdaten im Export gefunden";
            }
            ScannerStatus.Text = result.Success
                ? $"TSE Export erstellt: {result.FilePath}{masterData}"
                : "TSE Export fehlgeschlagen: " + result.Message;
        }
        catch (Exception ex)
        {
            ShowOperationalError("TSE EXPORT", ex);
        }
    }

    /// <summary>
    /// R113: shows an open TSE outage at the register. Every state below is
    /// reached through TseFailSafeService, which is now the single place that
    /// opens and closes outages - a failed probe, a failed signing call, or a
    /// sale that had to be completed with the TSE inactive.
    /// </summary>
    private void SetFiscalModeLabel(string full, string compact, string minimal)
    {
        SetHeaderLabel(FiscalModeText, full, compact, minimal);
        ToolTip.SetTip(FiscalModeBadge, full);
    }

    private async Task RefreshTseOutageBadgeAsync()
    {
        try
        {
            var outage = await _tseFailSafe.GetOpenOutageAsync();
            if (outage is null)
            {
                TseOutageBadge.IsVisible = false;
                return;
            }

            // R126: the outage stays named at every header density - it is a
            // legal state - only the start time moves into the tooltip.
            var since = $"TSE-AUSFALL · seit {outage.StartedAt.LocalDateTime:dd.MM. HH:mm}";
            SetHeaderLabel(TseOutageText, since, "TSE-AUSFALL", "TSE-AUSFALL");
            ToolTip.SetTip(TseOutageBadge, since + "\n" + outage.Reason);
            TseOutageBadge.IsVisible = true;
        }
        catch (Exception ex)
        {
            CrashLog.WriteException("TSE outage badge", ex);
        }
    }

    private async Task RefreshFiscalStatusAsync()
    {
        try
        {
            await RefreshTseOutageBadgeAsync();
            _fiscalReadiness = await _compliance.CheckAsync();
            try
            {
                _tseMasterData = await new TseMasterDataRepository(new SqliteDatabase(AppPaths.DatabasePath), _audit).LoadAllAsync();
            }
            catch (Exception ex)
            {
                // Without them the receipt prints the TSE data as text.
                CrashLog.WriteException("TSE master data for receipts", ex);
            }

            if (_currentUser.IsTraining)
            {
                SetFiscalModeLabel("TRAINING · KEINE ECHTE BUCHUNG", "TRAINING", "TRAINING");
                FiscalModeText.Foreground = AppTheme.WarningAmber;
                CashButtonText.Text = "BAR · TRAINING";
                CardButtonText.Text = "KARTE · TRAINING";
                return;
            }

            var edition = InstallationEdition.ReadLocked() ?? "";
            var license = _commercialLicense.Check(edition);
            if (!license.IsActive)
            {
                if (TorDistribution.IsDemoBuild && TrialRuntime.Current?.IsActive == true)
                {
                    var days = TrialRuntime.Current.RemainingDays;
                    SetFiscalModeLabel(
                        $"DEMO · {days} TAGE · KEINE ECHTE BUCHUNG",
                        $"DEMO · {days} T",
                        "DEMO");
                    FiscalModeText.Foreground = AppTheme.WarningAmber;
                    CashButtonText.Text = "BAR · DEMO";
                    CardButtonText.Text = "KARTE · DEMO";
                    return;
                }

                // Source-candidate / development builds must remain testable without
                // a customer license. No real sale is committed in this mode.
                SetFiscalModeLabel("TESTBETRIEB · KEINE LIZENZ · KEINE ECHTE BUCHUNG", "TEST · KEINE LIZENZ", "TEST");
                FiscalModeText.Foreground = AppTheme.WarningAmber;
                CashButtonText.Text = "BAR · TEST";
                CardButtonText.Text = "KARTE · TEST";
                return;
            }

            if (_fiscalReadiness.ProductionAllowed)
                SetFiscalModeLabel("PRODUKTIV · FISKAL FREIGEGEBEN", "PRODUKTIV", "PRODUKTIV");
            else
                SetFiscalModeLabel(
                    $"TESTBETRIEB · {_fiscalReadiness.BlockingCount} FISKAL-SPERREN",
                    $"TEST · {_fiscalReadiness.BlockingCount} SPERREN",
                    "TEST");

            FiscalModeText.Foreground =
                _fiscalReadiness.ProductionAllowed ? AppTheme.AccentTeal : AppTheme.WarningAmber;

            var cashLabel=_settingsCache.GetText("pay.cash.label","Bar").ToUpperInvariant();
            var cardLabel=_settingsCache.GetText("pay.card.label","Karte").ToUpperInvariant();

            CashButtonText.Text = _fiscalReadiness.ProductionAllowed
                ? cashLabel
                : cashLabel + " · TEST";

            CardButtonText.Text = _fiscalReadiness.ProductionAllowed
                ? cardLabel
                : cardLabel + " · TEST";
        }
        catch (Exception ex)
        {
            SetFiscalModeLabel("TESTBETRIEB · FISKALSTATUS FEHLER", "FISKALSTATUS FEHLER", "FISKAL ?");
            ShowOperationalError("FISKALSTATUS", ex);
        }
    }

    private async Task AutoProbeTseAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            // R113: goes through TseFailSafeService, not the raw provider, so
            // an unreachable/failed TSE actually opens a documented outage and
            // a later success closes it again. TseFailSafeService.ProbeAsync
            // existed from the start but had no caller anywhere - every probe
            // site used the raw ITseProvider, so the operational probe could
            // see a dead TSE and record nothing at all.
            var probe = _tseFailSafe.ProbeAsync(_currentUser.Username, timeout.Token);
            var completed = await Task.WhenAny(
                probe,
                Task.Delay(TimeSpan.FromSeconds(10)));

            if (completed != probe)
            {
                ScannerStatus.Text =
                    "TSE antwortet nicht · USB/SDK prüfen · Kasse bleibt bedienbar";
                ReportOperationalError("TSE","TSE-Geräteprüfung: Timeout nach 10 Sekunden.");
                return;
            }

            var result = await probe;

            if (result.State == TseConnectionState.Ready)
            {
                ScannerStatus.Text =
                    $"TSE bereit · {result.Device?.SerialNumber}";
            }
            else if (result.State == TseConnectionState.Connected)
            {
                ScannerStatus.Text =
                    "Swissbit TSE erkannt · Einrichtung/Status prüfen";
            }
        }
        catch(Exception ex)
        {
            CrashLog.WriteException("MainWindow operation", ex);
            ReportOperationalError("TSE","TSE-Geräteprüfung fehlgeschlagen.",ex);
        }
    }

    private async void OnSettingsClick(object? s,RoutedEventArgs e)
    {
        if(!_currentUser.IsAdmin)
        {
            ScannerStatus.Text="Keine Berechtigung für Einstellungen.";
            return;
        }

        await _windowFactory
            .CreateSettingsWindow(_currentUser)
            .ShowDialog<bool>(this);
        await ReloadSettingsAsync();
        await RefreshFiscalStatusAsync();
        _ = CheckForUpdateInBackgroundAsync(force: true);
    }

    private async Task ReloadSettingsAsync()
    {
        _settingsCache=await _settings.LoadAllAsync();
        UiLanguage.Set(_settingsCache.GetText("ui.language","DE"));

        var registerName=_settingsCache.GetText("cash.register.name","Kasse 1");
        var business=InstallationEdition.ReadLocked()
            ?? _settingsCache.GetText("business.mode","IMBISS").ToUpperInvariant();
        var businessDisplay = InstallationEdition.DisplayName(business);

        CompanyNameText.Text="TOR-POS";
        RegisterInfoText.Text=$"{registerName} · {businessDisplay}";
        EditionText.Text=businessDisplay.ToUpperInvariant();
        EditionActionText.Text = business == "IMBISS" ? "EXTRA" : "PFAND";
        var pickupMode=GetImbissPickupMode();
        ParkButtonText.Text = business=="IMBISS" && pickupMode=="ORDER" ? "BESTELLUNG\nANNEHMEN" : "PARKEN";
        ParkButtonSubText.Text = business=="IMBISS" && pickupMode=="ORDER" ? (_settingsCache.GetBool("imbiss.order.number_enabled",true) ? "F3 · ABHOLNR." : "F3 · BESTELLUNG") : "F3 · BON";
        Title = $"TOR POS Pro · {businessDisplay}";

        // Internal edition codes remain KIOSK/IMBISS for compatibility.
        // Customer-facing labels use the broader sectors Einzelhandel/Gastronomie.
        if (business == "KIOSK")
        {
            CategoryHeaderText.Text = "SCHNELLWAHL · EINZELHANDEL";
            BackToCategoriesButton.Content = "◀ SCHNELLWAHL";
        }
        else
        {
            CategoryHeaderText.Text = "WARENGRUPPEN · GASTRONOMIE";
            BackToCategoriesButton.Content = "◀ WARENGRUPPEN";
        }
        var userLabel = _currentUser.IsTraining
            ? $"{_currentUser.Username} · TRAINING"
            : _currentUser.IsAdmin
                ? $"{_currentUser.Username} · Vollzugriff"
                : _currentUser.Username;
        // R126: at Minimal density the user badge is hidden entirely; ABMELDEN
        // next to it still says someone is logged in, and the tooltip says who.
        SetHeaderLabel(UserModeText, userLabel, _currentUser.Username, _currentUser.Username);
        ToolTip.SetTip(UserModeBadge, userLabel);
        UserModeText.Foreground = _currentUser.IsTraining ? AppTheme.WarningAmber : AppTheme.AccentBlue;

        if (business == "KIOSK")
        {
            ScannerStatus.Text = "EINZELHANDEL · SCANNER BEREIT · Barcode scannen";
            CategoryModeHintText.Text = "OPTIONAL · Scanner ist der Hauptweg";
            ProductModeHintText.Text = "Schnellwahl optional · Scanner bleibt aktiv";
            EmptyCartHintText.Text = "BARCODE SCANNEN";
            CheckoutHintText.Text = "SCANNEN → F5 KASSIEREN";
        }
        else
        {
            ScannerStatus.Text = "GASTRONOMIE · TOUCH-SCHNELLWAHL BEREIT";
            CategoryModeHintText.Text = "TOUCH · Warengruppe → Artikel";
            ProductModeHintText.Text = "TOUCH · Artikel → direkt im Bon";
            EmptyCartHintText.Text = "WARENGRUPPE ODER ARTIKEL ANTIPPEN";
            CheckoutHintText.Text = "TOUCH → ARTIKEL → F5 KASSIEREN";
        }

        // Änderungen an Spalten/Zeilen werden nach dem Schließen der
        // Programmeinstellungen sofort auf die Hauptkasse angewendet.
        BuildCategories();

        if (ProductArea.IsVisible)
        {
            if (_categoryId != 0 && _catalog.Categories.Any(x => x.Id == _categoryId))
                BuildProducts();
            else
            {
                _categoryId = 0;
                ShowCategoryOverview();
            }
        }

        if (_settingsCache.GetBool("tse.auto_connect", true))
        {
            _ = AutoProbeTseAsync();
        }

        var license = _commercialLicense.Check(business);
        RefreshLicenseWarning(license);
        // R116: selling is gated by the cashier's permission alone. Whether the
        // sale is booked for real or completed as a marked simulation is
        // decided by SaleModePolicy at checkout time - the buttons no longer
        // go dead just because the fiscal release gate is still closed.
        _saleAllowed = _currentUser.Can(UserPermissions.Sale);
        _cashEnabledBySettings = _settingsCache.GetBool("pay.cash.enabled", true);
        _cardEnabledBySettings = _settingsCache.GetBool("pay.card.enabled", true);

        // R156: F5 is now the single payment hub. It always opens the
        // payment page so Verkaufsart (AUSSER HAUS / IM HAUS) and the three
        // tender choices BAR / KARTE / GEMISCHT remain visible together.
        QuickCheckoutText.Text = "KASSIEREN";
        QuickCheckoutSubText.Text = "F5 · ZAHLART";

        ClearButton.IsEnabled = _currentUser.Can(UserPermissions.Sale);
        EditionActionButton.IsEnabled = _currentUser.Can(UserPermissions.Sale) &&
            (business == "IMBISS" || _settingsCache.GetBool("function.pfand_buttons", true));
        RefreshSalesActionState();
        ParkedReceiptsButton.IsEnabled = (!_currentUser.IsTraining || GetImbissPickupMode()=="ORDER") && _currentUser.Can(UserPermissions.ParkReceipts);
        BonStornoButton.IsEnabled = !_currentUser.IsTraining && _currentUser.Can(UserPermissions.ReceiptStorno);
        ReceiptHistoryButton.IsEnabled = !_currentUser.IsTraining && _currentUser.Can(UserPermissions.ViewReceiptHistory);
        ReceiptModeButton.IsEnabled = !_currentUser.IsTraining && _currentUser.Can(UserPermissions.Sale);
        CashMovementButton.IsEnabled = !_currentUser.IsTraining && _currentUser.Can(UserPermissions.CashMovement);
        ZReportButton.IsEnabled = !_currentUser.IsTraining && _currentUser.Can(UserPermissions.ZReport);
        ProductsButton.IsEnabled = _currentUser.Can(UserPermissions.ManageProducts);
        SettingsButton.IsEnabled = _currentUser.IsAdmin;
        ReportsMenu.IsEnabled = _currentUser.IsAdmin ||
            _currentUser.Can(UserPermissions.ZReport) ||
            _currentUser.Can(UserPermissions.ViewReceiptHistory);
        var cashLabel=_settingsCache.GetText("pay.cash.label","Bar").ToUpperInvariant();
        var cardLabel=_settingsCache.GetText("pay.card.label","Karte").ToUpperInvariant();

        CashButtonText.Text = _fiscalReadiness?.ProductionAllowed == true
            ? cashLabel
            : cashLabel + " · TEST";

        CardButtonText.Text = _fiscalReadiness?.ProductionAllowed == true
            ? cardLabel
            : cardLabel + " · TEST";

        if (!license.IsActive)
        {
            CashButtonText.Text = cashLabel + " · TEST";
            CardButtonText.Text = cardLabel + " · TEST";
        }

        if (_currentUser.IsTraining)
        {
            CashButtonText.Text = "BAR · TRAINING";
            CardButtonText.Text = "KARTE · TRAINING";
        }

        RefreshReceiptModeButtons();
        UiLanguage.Apply(this);
        RefreshOrderDisplayWindow(business);
        RefreshCustomerDisplayWindow();
    }

    // R104: mirrors RefreshOrderDisplayWindow's exact lifecycle pattern
    // (open/close a fullscreen window on a configured second screen), but
    // for the genuine customer-facing display, available in any business
    // mode (not IMBISS-only like the order board).
    private void RefreshCustomerDisplayWindow()
    {
        var enabled = _settingsCache.GetBool("device.customer_display.enabled", false);
        var screen = _settingsCache.GetInt("device.customer_display.screen_index", 0);
        var signature = enabled ? screen.ToString() : "off";

        if (_customerDisplayWindow is not null && _customerDisplaySignature == signature) return;
        if (_customerDisplayWindow is not null)
        {
            try { _customerDisplayWindow.Close(); } catch { }
            _customerDisplayWindow = null;
        }
        _customerDisplaySignature = signature;
        if (!enabled) return;
        if (screen <= 0 && Screens.All.Count < 2)
        {
            ScannerStatus.Text = "KUNDENDISPLAY: Kein zweiter Bildschirm erkannt. In Einstellungen einen Bildschirm wählen oder zweiten Monitor anschließen.";
            return;
        }

        var window = new CustomerDisplayWindow(screen, _settingsCache.GetText("company.name", "TOR POS"));
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_customerDisplayWindow, window)) _customerDisplayWindow = null;
        };
        _customerDisplayWindow = window;
        window.Show();
        if (_engine.Cart.Count > 0)
            window.ShowCart(_engine.Cart, _engine.DiscountCents, _engine.TotalCents);
    }

    private void RefreshOrderDisplayWindow(string business)
    {
        var enabled = business == "IMBISS" && _settingsCache.GetBool("order_display.enabled", false);
        var screen = _settingsCache.GetInt("order_display.screen_index", 0);
        var refresh = Math.Clamp(_settingsCache.GetInt("order_display.refresh_seconds", 2), 1, 10);
        var signature = enabled ? $"{screen}:{refresh}:{_currentUser.IsTraining}" : "off";

        if (_orderDisplayWindow is not null && _orderDisplaySignature == signature) return;
        if (_orderDisplayWindow is not null)
        {
            try { _orderDisplayWindow.Close(); } catch { }
            _orderDisplayWindow = null;
        }
        _orderDisplaySignature = signature;
        if (!enabled) return;
        if (screen <= 0 && Screens.All.Count < 2)
        {
            ScannerStatus.Text = "BESTELLMONITOR: Kein zweiter Bildschirm erkannt. In Einstellungen einen Bildschirm wählen oder zweiten Monitor anschließen.";
            return;
        }

        var window = new OrderCustomerDisplayWindow(OrderWorkflow, _currentUser.IsTraining, screen, refresh);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_orderDisplayWindow, window)) _orderDisplayWindow = null;
        };
        _orderDisplayWindow = window;
        window.Show();
    }

    private void RefreshLicenseWarning(CommercialLicenseStatus license)
    {
        if (TorDistribution.IsDemoBuild && TrialRuntime.Current?.IsActive == true)
        {
            var trial = TrialRuntime.Current;
            var trialDays = trial.RemainingDays;
            LicenseWarningBadge.IsVisible = true;
            SetHeaderLabel(
                LicenseWarningText,
                $"DEMO · {trialDays} TAGE",
                $"DEMO · {trialDays} T",
                "DEMO");
            LicenseWarningText.Foreground =
                trialDays <= 1
                    ? new SolidColorBrush(Color.Parse("#FF8A80"))
                    : new SolidColorBrush(Color.Parse("#FFB74D"));
            LicenseWarningBadge.Background =
                new SolidColorBrush(Color.Parse(trialDays <= 1 ? "#5C2525" : "#5A3A10"));
            ToolTip.SetTip(
                LicenseWarningBadge,
                $"7-Tage-Demo bis {trial.ExpiresAtUtc?.ToLocalTime():dd.MM.yyyy HH:mm}. " +
                "Eine Neuinstallation auf diesem PC startet keine neue Demo.");
            return;
        }

        if (!license.IsExpiringSoon || string.IsNullOrWhiteSpace(license.RenewalNotice))
        {
            LicenseWarningBadge.IsVisible = false;
            SetHeaderLabel(LicenseWarningText, "LIZENZ", "LIZENZ", "LIZENZ");
            return;
        }

        var days = license.RemainingDays ?? 30;
        LicenseWarningBadge.IsVisible = true;
        if (days <= 1)
            SetHeaderLabel(LicenseWarningText, "LIZENZ · HEUTE/MORGEN", "LIZENZ · 1 T", "LIZENZ!");
        else
            SetHeaderLabel(LicenseWarningText, $"LIZENZ · {days} TAGE", $"LIZENZ · {days} T", "LIZENZ");
        LicenseWarningText.Foreground = days <= 3 ? new SolidColorBrush(Color.Parse("#FF8A80")) : days <= 7 ? new SolidColorBrush(Color.Parse("#FFB74D")) : AppTheme.WarningAmber;
        LicenseWarningBadge.Background = new SolidColorBrush(Color.Parse(days <= 3 ? "#5C2525" : "#5A3A10"));
        ToolTip.SetTip(LicenseWarningBadge, license.RenewalNotice + " · Bitte rechtzeitig verlängern.");
    }

    private async Task<string?> AskControlledReasonAsync(
        string actionTitle,
        string settingKey,
        string fallback,
        string summary)
    {
        var configured = _settingsCache.GetText(
            settingKey,
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
                .Where(x => x.Length > 0)
                .ToArray();
        }

        return await new PosActionReasonWindow(
            actionTitle,
            summary,
            reasons)
            .ShowDialog<string?>(this);
    }

    private Task WriteControlledActionAsync(
        string actionId,
        string phase,
        string actionType,
        string reason,
        string entityType,
        string entityId,
        long beforeTotalCents,
        long afterTotalCents,
        long amountCents,
        string details,
        string operationId)
    {
        return _controlledActions.AppendAsync(
            new PosActionLogRequest
            {
                ActionId = actionId,
                Phase = phase,
                Actor = _currentUser.Username,
                RegisterId = _settingsCache.GetText(
                    "cash.register.number",
                    "1"),
                OperationId = string.IsNullOrWhiteSpace(operationId)
                    ? "NO-OPERATION-ID"
                    : operationId,
                ActionType = actionType,
                Reason = reason,
                EntityType = entityType,
                EntityId = entityId,
                // R149: with returned deposit a cart total can be negative.
                BeforeTotalCents = beforeTotalCents,
                AfterTotalCents = afterTotalCents,
                AmountCents = Math.Max(0, amountCents),
                Details = details
            });
    }

    private async Task RecordControlledDeniedAsync(
        string actionType,
        string reason)
    {
        if (CartLocked)
            reason = "CHECKOUT_LOCKED";

        try
        {
            await WriteControlledActionAsync(
                Guid.NewGuid().ToString("N"),
                "DENIED",
                actionType,
                reason,
                "CURRENT_CART",
                "",
                _engine.TotalCents,
                _engine.TotalCents,
                0,
                $"permission_user={_currentUser.Username}",
                _operationId);
        }
        catch (Exception ex)
        {
            // The sensitive action is already denied. Logging failure must still
            // become visible to technical support without allowing the action.
            CrashLog.WriteException(
                $"R70 denied-action audit failed: {actionType}",
                ex);
        }
    }

    private bool RequirePermission(UserPermissions permission, string function)
    {
        if (CartLocked) { ScannerStatus.Text="Vorgang gesperrt · Zahlung / Wiederherstellung prüfen"; return false; }
        if (_currentUser.Can(permission))
            return true;

        ScannerStatus.Text = $"Keine Berechtigung für {function}.";
        return false;
    }

    private async Task CheckForUpdateInBackgroundAsync(bool force = false)
    {
        try
        {
            var updater = new TorUpdateService(_settings, _backup);
            if (!await updater.EnabledAsync()) return;
            if (!force && !await updater.ShouldBackgroundCheckAsync()) return;
            var result = await updater.CheckAsync(CurrentBusinessMode(), force: true);
            if (!result.UpdateAvailable || result.Manifest is null) return;
            _availableUpdate = result.Manifest;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                UpdateButton.Content = result.Manifest.Mandatory
                    ? $"UPDATE · {result.Manifest.Revision} !"
                    : $"UPDATE · {result.Manifest.Revision}";
                UpdateButton.Background = new SolidColorBrush(Color.Parse(result.Manifest.Mandatory ? "#6E2730" : "#17466A"));
                UpdateButton.IsVisible = _currentUser.IsAdmin;
                ToolTip.SetTip(UpdateButton, "Neue TOR POS Version verfügbar. Update wird erst nach sicherem Kassenabschluss installiert.");
            });
        }
        catch (Exception ex)
        {
            CrashLog.WriteException("Background update check failed", ex);
        }
    }

    private async void OnUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (!_currentUser.IsAdmin || _availableUpdate is null) return;
        if (_paymentInProgress || _pendingCheckout is not null || _recoveryFault)
        {
            ScannerStatus.Text = "UPDATE GESPERRT · Zahlung / Wiederherstellung zuerst abschließen.";
            return;
        }
        if (_engine.Cart.Count > 0)
        {
            ScannerStatus.Text = "UPDATE GESPERRT · Aktuellen Verkauf zuerst kassieren, PARKEN oder mit C leeren.";
            return;
        }

        UpdateButton.IsEnabled = false;
        try
        {
            ScannerStatus.Text = "TOR UPDATE · Download und Sicherheitsprüfung …";
            var updater = new TorUpdateService(_settings, _backup);
            var staged = await updater.DownloadAndStageAsync(_availableUpdate);
            updater.ScheduleInstallAfterExit(staged);
            await _audit.WriteAsync(_currentUser.Username, "UPDATE_STAGED", "SYSTEM", _availableUpdate.Version,
                $"TOR update staged; backup={Path.GetFileName(staged.BackupPath)}; installer hash verified.");
            ScannerStatus.Text = $"UPDATE BEREIT · Backup erstellt · TOR POS wird beendet und {staged.Manifest.Revision} installiert.";
            Close();
        }
        catch (Exception ex)
        {
            ShowOperationalError("UPDATE", ex);
            UpdateButton.IsEnabled = true;
        }
    }

    private async void OnLogoutClick(object? sender, RoutedEventArgs e)
    {
        if (_paymentInProgress) return;
        if (_pendingCheckout is not null || _recoveryFault) { LogoutRequested?.Invoke(); return; }
        // An open cart must not disappear silently during a user switch.
        if (_engine.Cart.Count > 0)
        {
            ScannerStatus.Text =
                "ABMELDEN: Aktuellen Verkauf zuerst kassieren, PARKEN oder mit C leeren.";
            return;
        }

        try
        {
            await _audit.WriteAsync(
                _currentUser.Username,
                "LOGOUT",
                "SESSION",
                _currentUser.Id.ToString(),
                _currentUser.IsTraining ? "training=true" : "training=false");
        }
        catch
        {
            // Logout itself must still remain possible if the audit write fails.
        }

        LogoutButton.IsEnabled = false;
        LogoutRequested?.Invoke();
    }

    private bool RequireRealMode(string function)
    {
        if (!_currentUser.IsTraining)
            return true;

        ScannerStatus.Text = $"{function} ist im TRAININGSMODUS deaktiviert.";
        return false;
    }

    private bool CanCompleteSale()
    {
        if (!RequirePermission(UserPermissions.Sale, "KASSIEREN"))
            return false;

        // R57: ORDER mode no longer blocks a direct sale. BAR/KARTE completes the current
        // cart as a normal sale; BESTELLUNG ANNEHMEN remains the separate action that stores
        // the cart as an open order.

        // TRAINING is a local simulation and must work without a commercial
        // license, TSE transaction or payment terminal.
        if (_currentUser.IsTraining)
            return true;

        // R116: a till that cannot book a production sale now sells in
        // simulation mode (TEST receipt, no fiscal booking) instead of being
        // refused outright. The old rule was inverted - it let an UNLICENSED
        // install through as a "development test mode" and blocked a LICENSED
        // one with "KASSIEREN GESPERRT" whenever the fiscal release gate was
        // off, which it always is today, so a paying customer could not even
        // try their own register. Which mode a completed sale actually takes
        // is decided by IsSimulation in CheckoutAsync.
        return true;
    }

    /// <summary>
    /// R116: single source of truth for "this till may book a real, fiscal
    /// sale". The rule itself lives in TorPos.Core.SaleModePolicy so it can be
    /// asserted in the safety suite without constructing a window.
    /// </summary>
    /// <summary>R142: see SaleModePolicy.SecuresVorgaenge.</summary>
    private bool SecuresVorgaengeFiscally() =>
        SaleModePolicy.SecuresVorgaenge(_currentUser.IsTraining, RecordsTrainingFiscally(), !IsSimulation);

    /// <summary>R135: see SaleModePolicy.RecordsTrainingFiscally.</summary>
    private bool RecordsTrainingFiscally()
    {
        var edition = InstallationEdition.ReadLocked()
            ?? _settingsCache.GetText("business.mode", "IMBISS").ToUpperInvariant();

        return SaleModePolicy.RecordsTrainingFiscally(
            _currentUser.IsTraining,
            _commercialLicense.Check(edition).IsActive,
            FiscalRelease.Enabled,
            _fiscalReadiness?.ProductionAllowed == true);
    }

    private bool CanCommitProductionSale()
    {
        var edition = InstallationEdition.ReadLocked()
            ?? _settingsCache.GetText("business.mode", "IMBISS").ToUpperInvariant();

        return SaleModePolicy.CanCommitProductionSale(
            _currentUser.IsTraining,
            _commercialLicense.Check(edition).IsActive,
            FiscalRelease.Enabled,
            _fiscalReadiness?.ProductionAllowed == true);
    }

    protected override void OnClosed(EventArgs e)
    {
        _scanNoEnterTimer.Stop();
        if (_orderDisplayWindow is not null)
        {
            try { _orderDisplayWindow.Close(); } catch { }
            _orderDisplayWindow = null;
        }

        foreach(var bmp in _imageCache.Values)bmp.Dispose();
        _imageCache.Clear();

        // Backup and shared hardware cleanup are handled once by the application
        // lifetime. This is important because ABMELDEN closes only the cashier
        // window and immediately opens a fresh login window.

        base.OnClosed(e);
    }
}
