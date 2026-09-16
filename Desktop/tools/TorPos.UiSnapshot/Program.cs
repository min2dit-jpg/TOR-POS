using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
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
    if (check && failures.Count == 0) Console.WriteLine($"LAYOUT CHECK PASSED ({sizes.Count} sizes)");
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
    var fiscalSigning = new SaleFiscalSigningService(tseFailSafe, settings, sales);
    var orderFiscalSigning = new OrderFiscalSigningService(tseFailSafe, settings, parkedReceipts);
    var cashMovements = new CashMovementRepository(db, audit);
    var digitalReceipts = new DigitalReceiptService(db, settings, sales);
    var checkoutJournal = new CheckoutJournal(db);
    var paymentTerminal = new ZvtPaymentTerminalService(settings, audit, checkoutJournal);
    var dsfinvkExport = new DsfinvkExportService(db, settings);
    var compliance = new FiscalComplianceService(identity, settings, tseProvider, commercialLicense);
    var checkoutApplication = new CheckoutApplicationService(compliance, checkoutJournal, paymentTerminal);

    // A realistic worst case for header width: IMBISS, a long company name, and
    // an open TSE outage (the badge R113 added).
    await settings.SaveManyAsync(new Dictionary<string, string>
    {
        ["company.name"] = "Imbiss Beispiel GmbH",
        ["register.name"] = "Kasse 1"
    });
    await InstallationEdition.EnforceAsync(settings, "IMBISS");
    await new ImbissStarterCatalogService(db).EnsureAsync("IMBISS");
    await catalog.ReloadAsync();
    await tseOutages.OpenAsync("UI-Snapshot: TSE nicht erreichbar", "snapshot");

    var admin = new AuthenticatedUser(1, "admin", "ADMIN", IsAdmin: true, MustChangePassword: false);

    foreach (var (width, height) in sizes)
    {
        var window = new MainWindow(
            catalog, repo, sales, parkedReceipts, dailyClosingGuard, cashMovements, audit,
            compliance, dsfinvkExport, new ProductImageStore(), perf, settings, backup,
            tseProvider, receiptPrinter, digitalReceipts, cardRefundLocks, commercialLicense,
            auth, management, admin, checkoutJournal, checkoutApplication,
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
        frame.Save(file);
        Console.WriteLine($"saved {file}");
        window.Close();
    }
}

// R126: the defects the HP till showed, as rules. Buttons a cashier needs to
// finish a sale or hand over the till must be entirely on screen; the TSE
// outage badge (a legal state) must not be clipped by the header; and the
// numpad keys must stay tall enough to hit and to read.
static void CheckLayout(Window window, int width, int height, List<string> failures)
{
    var size = $"{width}x{height}";
    var host = window.FindControl<Control>("HeaderMainHost");

    foreach (var name in new[] { "LogoutButton", "MixedPaymentButton", "ImHausToggleButton", "TseOutageBadge" })
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
    public DiagnosticsWindow CreateDiagnosticsWindow() => throw new NotSupportedException();
}
