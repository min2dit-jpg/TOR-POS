using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TorPos.App;
using TorPos.Application;
using TorPos.Core;
using TorPos.Infrastructure;

// R126: renders the real MainWindow off-screen at several screen sizes and saves
// PNGs. Layout problems on the small HP till (header running off the edge,
// numpad digits cut in half) were only ever found from photos of real screens;
// this lets a layout change be looked at before it ships.
//
//   dotnet run --project tools/TorPos.UiSnapshot -- <output folder> [WIDTHxHEIGHT ...] [--check]
//
// --check additionally fails (exit code 1) when, at any size, a control a
// cashier cannot work without is off-screen or clipped. CI runs it that way.
//
// Safety: everything runs against a throwaway data folder. AppPaths is pointed
// there BEFORE any service touches it, because on Windows %APPDATA% cannot be
// redirected through the environment and a developer PC may also be a till.

var check = args.Contains("--check");
var positional = args.Where(a => a != "--check").ToList();
// Default output outside the repository, so a local run never leaves files to commit.
var output = Path.GetFullPath(positional.Count > 0 ? positional[0] : Path.Combine(Path.GetTempPath(), "tor-ui-snapshots"));
var sizes = positional.Skip(1).Select(ParseSize).ToList();
var failures = new List<string>();
if (sizes.Count == 0)
    sizes = new() { (1920, 1080), (1366, 768), (1280, 800), (1280, 720), (1024, 640) };

var dataDir = Path.Combine(Path.GetTempPath(), "tor-ui-snapshot-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(output);
AppPaths.DataDirectoryOverride = dataDir;

AppBuilder.Configure<TorPos.App.App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

var exitCode = 0;
var work = RunAsync();
while (!work.IsCompleted)
{
    Dispatcher.UIThread.RunJobs();
    Thread.Sleep(5);
}
try
{
    await work;
    foreach (var failure in failures) Console.Error.WriteLine("LAYOUT FAIL: " + failure);
    if (check && failures.Count > 0) exitCode = 1;
    if (check && failures.Count == 0) Console.WriteLine($"LAYOUT CHECK PASSED ({sizes.Count} sizes, 10 dialogs)");
}
catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
finally
{
    try { Directory.Delete(dataDir, recursive: true); } catch { /* SQLite may still hold the file briefly */ }
}
return exitCode;

async Task RunAsync()
{
    var db = new SqliteDatabase();
    if (!db.DatabasePath.StartsWith(dataDir, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"Refusing to run: database path {db.DatabasePath} is outside the throwaway folder.");

    var backup = new DatabaseBackupService(db);
    await new SchemaMigrationService(db, backup).InitializeDatabaseAsync();
    var audit = new AuditLogRepository(db);
    var auth = new AuthenticationService(db, audit);
    await auth.InitializeAsync();

    var perf = new PerformanceCounters();
    var repo = new ProductRepository(db);
    var sales = new SaleRepository(db);
    var cardRefundLocks = new CardRefundLockRepository(db, audit);
    var parkedReceipts = new ParkedReceiptRepository(db);
    var dailyClosingGuard = new DailyClosingGuard(parkedReceipts, db);
    var catalog = new ProductCatalogCache(repo, perf);
    var settings = new SettingsRepository(db);
    var identity = new SystemIdentityRepository(db);
    var management = new BusinessManagementService(db, settings, audit);
    var receiptPrinter = new StarMcPrint3PrinterService();
    var commercialLicense = new CommercialLicenseService();
    var tseProvider = new SwissbitHardwareTseProvider();
    var tseOutages = new TseOutageRepository(db, audit);
    var tseFailSafe = new TseFailSafeService(tseProvider, tseOutages, audit);
    var tseVorgaenge = new TseVorgangService(db, tseFailSafe, settings);
    var fiscalSigning = new SaleFiscalSigningService(tseFailSafe, settings, sales)
    {
        Vorgaenge = tseVorgaenge
    };
    var orderFiscalSigning = new OrderFiscalSigningService(tseFailSafe, settings, parkedReceipts)
    {
        Vorgaenge = tseVorgaenge
    };
    var restaurantRepository = new RestaurantRepository(db);
    var restaurantFiscal = new RestaurantFiscalOrderService(db, tseVorgaenge);
    var restaurantEntitlements = new RestaurantEntitlementService(commercialLicense);
    var cashMovements = new CashMovementRepository(db, audit);
    var digitalReceipts = new CloudDigitalReceiptService(settings, null);
    var checkoutJournal = new CheckoutJournal(db);
    var paymentTerminal = new ZvtPaymentTerminalService(settings, audit, checkoutJournal);
    var dsfinvkExport = new DsfinvkExportService(db, settings);
    var reportEmail = new ReportEmailService(settings, management);
    var datevAscii = new DatevKassenbuchAsciiService(
        db, settings, management, reportEmail, audit);
    var datevKassenarchiv = new DatevKassenarchivService(
        db, settings, dsfinvkExport, tseProvider, audit);
    var compliance = new FiscalComplianceService(identity, settings, tseProvider, commercialLicense);
    var checkoutApplication = new CheckoutApplicationService(compliance, checkoutJournal, paymentTerminal);

    // A realistic worst case for header width: a long company name and an open
    // TSE outage (the badge R113 added).
    //
    // R182: a dedicated TOR Einzelhandel / TOR Gastro build fixes its edition and
    // EnforceAsync refuses any other, so the snapshot follows the build it was
    // compiled for. The shared build keeps IMBISS, the wider of the two headers.
    var snapshotEdition = ProductBuild.FixedEdition ?? "IMBISS";
    await settings.SaveManyAsync(new Dictionary<string, string>
    {
        ["company.name"] = "Imbiss Beispiel GmbH",
        ["register.name"] = "Kasse 1"
    });
    await InstallationEdition.EnforceAsync(settings, snapshotEdition);
    await new ImbissStarterCatalogService(db).EnsureAsync(snapshotEdition);
    await catalog.ReloadAsync();
    await tseOutages.OpenAsync("UI-Snapshot: TSE nicht erreichbar", "snapshot");

    var admin = new AuthenticatedUser(1, "admin", "ADMIN", IsAdmin: true, MustChangePassword: false);

    foreach (var (width, height) in sizes)
    {
        var window = new MainWindow(
            catalog, repo, sales, parkedReceipts, dailyClosingGuard, cashMovements, audit,
            compliance, dsfinvkExport, datevAscii, datevKassenarchiv, new ProductImageStore(), perf, settings, backup,
            tseProvider, receiptPrinter, digitalReceipts, cardRefundLocks, commercialLicense,
            auth, management, restaurantRepository, restaurantFiscal, restaurantEntitlements, admin, checkoutJournal, checkoutApplication,
            new ControlledPosActionService(db), new PromotionCampaignService(db),
            fiscalSigning, orderFiscalSigning, tseFailSafe, new NoWindows());

        // The constructor maximizes; a headless window has no screen to fill,
        // so the size stands in for the till's usable desktop area.
        window.WindowState = WindowState.Normal;
        window.Width = width;
        window.Height = height;
        window.Show();

        // Let the window's own async start-up (catalog, badges, TSE probe) settle.
        for (var i = 0; i < 60; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }

        if (check) CheckLayout(window, width, height, failures);

        var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("No frame rendered.");
        var file = Path.Combine(output, $"main-{width}x{height}.png");
        frame.Save(file, new PngBitmapEncoderOptions());
        Console.WriteLine($"saved {file}");

        // R159: the menu workspace changed from a card wall to site-like
        // sidebar navigation. Capture the real overlays at representative
        // till/laptop sizes so the resulting CI artifact can be reviewed.
        if ((width == 1024 && height == 640) || (width == 1366 && height == 768))
        {
            var showHub = typeof(MainWindow).GetMethod(
                "ShowMenuHub",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("ShowMenuHub reflection hook missing.");

            foreach (var section in new[] { "WAREN", "EINSTELLUNGEN", "KASSE", "BERICHTE" })
            {
                showHub.Invoke(window, new object[] { section });
                for (var i = 0; i < 4; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(20);
                }

                var hubFrame = window.CaptureRenderedFrame()
                    ?? throw new InvalidOperationException($"No {section} frame rendered.");
                var hubFile = Path.Combine(output, $"hub-{section.ToLowerInvariant()}-{width}x{height}.png");
                hubFrame.Save(hubFile, new PngBitmapEncoderOptions());
                Console.WriteLine($"saved {hubFile}");
            }
        }

        window.Close();
    }

    // R161: brand-critical startup and login screens are part of the visual
    // regression set so the old TOR placeholder/magnifier cannot return.
    await SnapshotDialogAsync(new StartupLoadingWindow(), "startup-loading", check, failures, output);
    await SnapshotDialogAsync(new LoginWindow(auth, settings), "login", check, failures, output);
    // R182: a dedicated TOR Einzelhandel / TOR Gastro build shows one fixed
    // Kassenart centred across the row. That is the screen a customer actually
    // sees, and the till it runs on has a small display, so it belongs in the
    // layout gate rather than only in a source-level check.
    await SnapshotDialogAsync(new LoginWindow(auth, settings, snapshotEdition), "login-locked", check, failures, output);

    // R164: the real employee-management window is opened and then reloaded
    // once more, exactly matching the refresh path after a successful save.
    // This catches Avalonia visual-parent reuse bugs that static tests cannot.
    await SnapshotUserManagementAsync(auth, admin, check, failures, output);

    // R156: Verkaufsart and all tender choices now live in one payment hub.
    // Snapshot it explicitly so moving controls out of the header cannot turn
    // into an untested dialog overflow on a till.
    await SnapshotDialogAsync(
        new PaymentChoiceWindow(cashEnabled: true, cardEnabled: true, allowImHaus: true),
        "payment-choice",
        check,
        failures,
        output);

    // R145: the receipt choice after a sale and the digital receipt window.
    await SnapshotDialogAsync(new ReceiptChoiceWindow(1500, testReceipt: false), "receipt-choice", check, failures, output);
    var link = new DigitalReceiptWindow();
    link.ShowLink("https://bon.torpos.de/r/f07QSoPVMkkp77T5cS1uM2J2FMSTIoeTGM3gwlUFBVA", new DateTimeOffset(2026, 12, 16, 10, 0, 6, TimeSpan.Zero));
    await SnapshotDialogAsync(link, "digital-receipt-link", check, failures, output);
    var failed = new DigitalReceiptWindow();
    failed.ShowFailure("TOR Cloud ist nicht erreichbar.", "Der Papierbeleg wird ausgegeben.");
    await SnapshotDialogAsync(failed, "digital-receipt-failure", check, failures, output);

    // R149: returned empties and the cash payout.
    await SnapshotDialogAsync(new PfandSelectionWindow(), "pfand-return", check, failures, output);
    await SnapshotDialogAsync(new DepositPayoutWindow(50), "deposit-payout", check, failures, output);
}

static async Task SnapshotUserManagementAsync(
    IAuthenticationService auth,
    AuthenticatedUser admin,
    bool check,
    List<string> failures,
    string output)
{
    var window = new UserManagementWindow(auth, admin);
    window.Show();

    for (var i = 0; i < 20; i++)
    {
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(30);
    }

    void CheckLoadStatus(string phase)
    {
        var failed = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text ?? "")
            .FirstOrDefault(t => t.StartsWith("Laden fehlgeschlagen:", StringComparison.Ordinal));
        if (failed is not null)
            failures.Add($"user-management {phase}: {failed}");
    }

    if (check) CheckLoadStatus("initial load");

    var reload = typeof(UserManagementWindow).GetMethod(
        "LoadAsync",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("UserManagementWindow.LoadAsync reflection hook missing.");

    var reloadTask = reload.Invoke(window, null) as Task
        ?? throw new InvalidOperationException("UserManagementWindow.LoadAsync did not return Task.");
    await reloadTask;

    for (var i = 0; i < 10; i++)
    {
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(20);
    }

    if (check) CheckLoadStatus("second load");

    var frame = window.CaptureRenderedFrame()
        ?? throw new InvalidOperationException("No user-management frame rendered.");
    var file = Path.Combine(output, "user-management.png");
    frame.Save(file, new PngBitmapEncoderOptions());
    Console.WriteLine($"saved {file}");
    window.Close();
}

// R145: a dialog at its own fixed size; every visible button must be inside it.
static async Task SnapshotDialogAsync(Window window, string name, bool check, List<string> failures, string output)
{
    window.Show();
    for (var i = 0; i < 10; i++)
    {
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(30);
    }

    if (check)
    {
        var width = window.ClientSize.Width;
        var height = window.ClientSize.Height;
        foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible))
        {
            var topLeft = button.TranslatePoint(new Point(0, 0), window);
            if (topLeft is null)
            {
                failures.Add($"{name}: button '{button.Content}' is not laid out");
                continue;
            }
            if (topLeft.Value.X < -0.5 || topLeft.Value.Y < -0.5 ||
                topLeft.Value.X + button.Bounds.Width > width + 0.5 || topLeft.Value.Y + button.Bounds.Height > height + 0.5)
                failures.Add($"{name}: button '{button.Content}' extends past the window ({topLeft.Value.X:0},{topLeft.Value.Y:0} {button.Bounds.Width:0}x{button.Bounds.Height:0} in {width:0}x{height:0})");
        }
    }

    var frame = window.CaptureRenderedFrame()
        ?? throw new InvalidOperationException("No frame rendered.");
    var file = Path.Combine(output, $"{name}.png");
    frame.Save(file, new PngBitmapEncoderOptions());
    Console.WriteLine($"saved {file}");
    window.Close();
}

// R126/R156: the defects the HP till showed, as rules. After R156, payment
// choices no longer belong in the header at all; the remaining session action
// and the TSE outage badge must stay entirely on screen, and numpad keys must
// stay tall enough to hit and to read.
static void CheckLayout(Window window, int width, int height, List<string> failures)
{
    var size = $"{width}x{height}";
    var host = window.FindControl<Control>("HeaderMainHost");

    foreach (var name in new[] { "LogoutButton", "TseOutageBadge" })
    {
        var control = window.FindControl<Control>(name);
        if (control is null) { failures.Add($"{size}: {name} not found"); continue; }
        if (!control.IsVisible) { failures.Add($"{size}: {name} is not visible"); continue; }
        var topLeft = control.TranslatePoint(new Point(0, 0), window);
        if (topLeft is null) { failures.Add($"{size}: {name} is not laid out"); continue; }
        var right = topLeft.Value.X + control.Bounds.Width;
        if (topLeft.Value.X < -0.5 || right > width + 0.5)
            failures.Add($"{size}: {name} extends past the window ({topLeft.Value.X:0}..{right:0} of {width})");

        if (name == "TseOutageBadge" && host is not null)
        {
            var hostLeft = host.TranslatePoint(new Point(0, 0), window);
            if (hostLeft is not null && right > hostLeft.Value.X + host.Bounds.Width + 0.5)
                failures.Add($"{size}: TSE outage badge is clipped by the header ({right:0} > {hostLeft.Value.X + host.Bounds.Width:0})");
        }
    }

    const double MinimumKeyHeight = 34;
    foreach (var key in window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("keypad")))
    {
        if (key.Bounds.Height < MinimumKeyHeight)
        {
            failures.Add($"{size}: numpad key '{key.Content}' is only {key.Bounds.Height:0}px tall (minimum {MinimumKeyHeight})");
            break;
        }
    }
}

static (int, int) ParseSize(string text)
{
    var parts = text.ToLowerInvariant().Split('x');
    return (int.Parse(parts[0]), int.Parse(parts[1]));
}

// The snapshot never opens secondary windows; any attempt is a bug in the run.
sealed class NoWindows : IAppWindowFactory
{
    public MainWindow CreateMainWindow(AuthenticatedUser user) => throw new NotSupportedException();
    public SettingsWindow CreateSettingsWindow(AuthenticatedUser user, string initialPage = "Allgemein") => throw new NotSupportedException();
    public RestaurantTablePlanWindow CreateRestaurantTablePlanWindow(
        AuthenticatedUser user) => throw new NotSupportedException();
    public RestaurantKdsWindow CreateRestaurantKdsWindow(
        AuthenticatedUser user) => throw new NotSupportedException();
    public RestaurantHandheldSetupWindow CreateRestaurantHandheldSetupWindow(
        AuthenticatedUser user) => throw new NotSupportedException();
    public DiagnosticsWindow CreateDiagnosticsWindow() => throw new NotSupportedException();
}
