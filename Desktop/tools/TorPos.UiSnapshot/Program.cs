using Avalonia.LogicalTree;
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
//   dotnet run --project tools/TorPos.UiSnapshot -- <output folder> [WIDTHxHEIGHT ...] [--check] [--language DE|TR|EN]
//
// --check additionally fails (exit code 1) when, at any size, a control a
// cashier cannot work without is off-screen or clipped. CI runs it that way.
//
// Safety: everything runs against a throwaway data folder. AppPaths is pointed
// there BEFORE any service touches it, because on Windows %APPDATA% cannot be
// redirected through the environment and a developer PC may also be a till.

var check = args.Contains("--check");

// Layout safety is language-dependent: translated labels can be wider than DE.
var languageIndex = Array.IndexOf(args, "--language");
var language = languageIndex >= 0 && languageIndex + 1 < args.Length
    ? args[languageIndex + 1].Trim().ToUpperInvariant()
    : "DE";
if (!UiLanguage.IsSupported(language))
    throw new ArgumentException($"Unknown interface language '{language}'. Use DE, TR or EN.");
var languageSuffix = language == "DE" ? "" : "-" + language.ToLowerInvariant();

var positional = args
    .Where((a, i) =>
        a != "--check" &&
        (languageIndex < 0 ||
         (i != languageIndex && i != languageIndex + 1)))
    .ToList();
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
Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", ProductBuild.FixedEdition ?? "IMBISS");

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
    if (check && failures.Count == 0) Console.WriteLine($"LAYOUT CHECK PASSED ({sizes.Count} sizes, 10 dialogs, language {language})");
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
        ["register.name"] = "Kasse 1",
        ["ui.language"] = language
    });
    UiLanguage.Set(language);
    await InstallationEdition.EnforceAsync(settings, snapshotEdition);
    await new ImbissStarterCatalogService(db).EnsureAsync(snapshotEdition);
    if(snapshotEdition=="RESTAURANT")
    {
        var group=await repo.SaveGroupAsync(new(0,"Speisekarte"));
        var food=await repo.SaveCategoryAsync(new(0,group,"Snapshot Speisen",7m));
        var drinks=await repo.SaveCategoryAsync(new(0,group,"Snapshot Getränke",19m));
        await repo.SaveAsync(new Product { CategoryId=food,Name="Burger",BasePriceCents=1200,VatRate=7m });
        await repo.SaveAsync(new Product { CategoryId=food,Name="Pommes",BasePriceCents=400,VatRate=7m });
        await repo.SaveAsync(new Product { CategoryId=drinks,Name="Cola",BasePriceCents=350,VatRate=19m });
    }
    await catalog.ReloadAsync();
    await tseOutages.OpenAsync("UI-Snapshot: TSE nicht erreichbar", "snapshot");

    var admin = new AuthenticatedUser(1, "admin", "ADMIN", IsAdmin: true, MustChangePassword: false);

    var kitchen = new RestaurantKitchenOutbox(db);
    var printJournal = new PrintJobJournal(Path.Combine(dataDir, "PrintJobs"));
    await using var kitchenRouter = new RestaurantKitchenPrinterRouter(printJournal);
    await using var kitchenDispatcher = new RestaurantKitchenDispatcher(kitchen, settings, kitchenRouter, printJournal);
    foreach (var (width, height) in sizes)
    {
        var windowFactory = new NoWindows(() => new RestaurantWorkspaceControl(
            restaurantRepository, restaurantFiscal, kitchen, kitchenDispatcher, catalog, settings,
            new ControlledPosActionService(db), new RestaurantWaiterSettlementService(db),
            admin, receiptPrinter));
        var window = new MainWindow(
            catalog, repo, sales, parkedReceipts, dailyClosingGuard, cashMovements, audit,
            compliance, dsfinvkExport, datevAscii, datevKassenarchiv, new ProductImageStore(), perf, settings, backup,
            tseProvider, receiptPrinter, digitalReceipts, cardRefundLocks, commercialLicense,
            auth, management, restaurantRepository, restaurantFiscal, restaurantEntitlements, admin, checkoutJournal, checkoutApplication,
            new ControlledPosActionService(db), new PromotionCampaignService(db),
            fiscalSigning, orderFiscalSigning, tseFailSafe, windowFactory);

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

        if (snapshotEdition == "RESTAURANT")
        {
            var workspace = windowFactory.Workspace;
            if (workspace is null)
                throw new InvalidOperationException(
                    "Restaurant startup did not create the embedded workspace.");

            var host = window.FindControl<Control>("RestaurantWorkspaceHost");
            if (host is null || !host.IsVisible)
                throw new InvalidOperationException(
                    "Restaurant startup did not show the embedded Tischplan.");

            CheckNamedActions(
                window,
                new[]
                {
                    "RestaurantCounterButton",
                    "RestaurantSendOrder",
                    "InterimBill",
                    "RestaurantMove",
                    "RestaurantSplit",
                    "TablePayAll"
                },
                failures);

            window.CaptureRenderedFrame()!.Save(
                Path.Combine(output, $"restaurant-main-{width}x{height}.png"),
                new PngBitmapEncoderOptions());

            var tableButton = workspace
                .GetVisualDescendants()
                .OfType<Button>()
                .First(b => b.Tag is RestaurantTable);
            var table = (RestaurantTable)tableButton.Tag!;
            tableButton.RaiseEvent(
                new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(250);
            Dispatcher.UIThread.RunJobs();

            var opened =
                await restaurantRepository.GetLiveSessionForTableAsync(table.Id);
            if (opened is null)
                throw new InvalidOperationException(
                    "Table tap did not open an order.");

            window.CaptureRenderedFrame()!.Save(
                Path.Combine(output, $"restaurant-order-{width}x{height}.png"),
                new PngBitmapEncoderOptions());

            // DEV5: the order screen of an open table keeps every action and
            // the header (TISCHPLAN, THEKE, ABMELDEN) inside the window.
            CheckNamedActions(
                window,
                new[]
                {
                    "RestaurantTablesButton",
                    "RestaurantCounterButton",
                    "LogoutButton",
                    "RestaurantSendOrder",
                    "InterimBill",
                    "RestaurantMove",
                    "RestaurantSplit",
                    "TablePayAll"
                },
                failures);
            CheckLabelsFit(
                window,
                new[] { "RestaurantSendOrder", "InterimBill", "RestaurantMove", "RestaurantSplit", "TablePayAll" },
                failures);

            var sameButton = workspace
                .GetVisualDescendants()
                .OfType<Button>()
                .First(b => b.Tag is RestaurantTable t && t.Id == table.Id);
            sameButton.RaiseEvent(
                new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(100);
            Dispatcher.UIThread.RunJobs();

            if ((await restaurantRepository.GetLiveSessionForTableAsync(table.Id))?.Id != opened.Id)
                throw new InvalidOperationException(
                    "Second table tap replaced an open order.");

            var currentOpen =
                await restaurantRepository.GetLiveSessionForTableAsync(table.Id)
                ?? throw new InvalidOperationException(
                    "Opened table disappeared before cleanup.");
            await restaurantRepository.CloseEmptySessionAsync(
                currentOpen.Id,
                currentOpen.Version,
                admin.Username,
                "snapshot");

            window.FindControl<Button>("RestaurantCounterButton")!
                .RaiseEvent(
                    new Avalonia.Interactivity.RoutedEventArgs(
                        Button.ClickEvent));
            await Task.Delay(100);
            Dispatcher.UIThread.RunJobs();

            if (host.IsVisible)
                throw new InvalidOperationException(
                    "THEKE did not switch the embedded Restaurant workspace back to direct sale.");

            Console.WriteLine(
                "RESTAURANT EMBEDDED STARTUP/TABLE/THEKE CHECK PASSED");
        }
        else if (windowFactory.Workspace is not null)
        {
            throw new InvalidOperationException(
                "Restaurant UI leaked into another edition.");
        }
        if (check) CheckLayout(window, width, height, failures);

        var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("No frame rendered.");
        var file = Path.Combine(output, $"main{languageSuffix}-{width}x{height}.png");
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
                var hubFile = Path.Combine(output, $"hub-{section.ToLowerInvariant()}{languageSuffix}-{width}x{height}.png");
                hubFrame.Save(hubFile, new PngBitmapEncoderOptions());
                Console.WriteLine($"saved {hubFile}");
            }
        }

        window.Close();
    }

    if(snapshotEdition=="RESTAURANT")
    {
        var tableSettings=new RestaurantTableSettingsWindow(restaurantRepository);
        tableSettings.Width=1366;tableSettings.Height=700;tableSettings.Show();
        await Task.Delay(200);Dispatcher.UIThread.RunJobs();
        CheckNamedActions(tableSettings,new[]{"EditorSave","EditorClose"},failures);
        tableSettings.CaptureRenderedFrame()!.Save(Path.Combine(output,"restaurant-table-settings-1366x768.png"),new PngBitmapEncoderOptions());
        tableSettings.Close();
        var recipeProduct=catalog.Products.First();
        var recipeEditor=new RestaurantRecipeWindow(restaurantRepository.Recipes,recipeProduct);
        var recipeSave=recipeEditor.GetLogicalDescendants().OfType<Button>().Single(b=>b.Name=="EditorSave");
        if(recipeSave.IsEnabled) throw new InvalidOperationException("Recipe save enabled before load.");
        recipeEditor.Show();await Task.Delay(150);Dispatcher.UIThread.RunJobs();
        if(!recipeSave.IsEnabled) throw new InvalidOperationException("Loaded recipe cannot be saved.");
        recipeEditor.Close();
        using(var c=db.OpenConnection())using(var q=c.CreateCommand())
        {q.CommandText="ALTER TABLE restaurant_recipes RENAME TO recipe_load_failure_fixture;";await q.ExecuteNonQueryAsync();}
        try
        {
            var failedEditor=new RestaurantRecipeWindow(restaurantRepository.Recipes,recipeProduct);
            failedEditor.Show();await Task.Delay(150);Dispatcher.UIThread.RunJobs();
            if(failedEditor.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="EditorSave").IsEnabled)
                throw new InvalidOperationException("Failed recipe load permits destructive save.");
            failedEditor.Close();
        }
        finally
        {
            using var c=db.OpenConnection();using var q=c.CreateCommand();
            q.CommandText="ALTER TABLE recipe_load_failure_fixture RENAME TO restaurant_recipes;";await q.ExecuteNonQueryAsync();
        }
        Console.WriteLine("RECIPE LOAD/SAVE GUARD PASSED");
    }

    var setup=new FirstRunSetupWindow(settings,receiptPrinter,tseProvider,paymentTerminal,snapshotEdition);
    setup.Show();await Task.Delay(200);Dispatcher.UIThread.RunJobs();
    setup.GetVisualDescendants().OfType<TextBox>().Single(x=>x.Name=="CompanyTaxNo").Text="053/200/06866";
    setup.GetVisualDescendants().OfType<TextBox>().Single(x=>x.Name=="CompanyVatId").Text="DE123456789";
    await (Task)typeof(FirstRunSetupWindow).GetMethod("SaveAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(setup,new object[]{false})!;
    var savedCompany=await new SettingsRepository(db).LoadAllAsync();
    if(savedCompany["company.tax_no"]!="053/200/06866" || savedCompany["company.vat_id"]!="DE123456789" ||
        savedCompany[InstallationEdition.ProfileKey(snapshotEdition,"company.tax_no")]!="053/200/06866" ||
        savedCompany[InstallationEdition.ProfileKey(snapshotEdition,"company.vat_id")]!="DE123456789")
        throw new InvalidOperationException("First-run company tax persistence failed.");
    setup.Close();
    Console.WriteLine("FIRST-RUN COMPANY TAX PERSISTENCE PASSED");

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

async Task SnapshotUserManagementAsync(
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
    var file = Path.Combine(output, $"user-management{languageSuffix}.png");
    frame.Save(file, new PngBitmapEncoderOptions());
    Console.WriteLine($"saved {file}");
    window.Close();
}

// R145: a dialog at its own fixed size; every visible button must be inside it.
async Task SnapshotDialogAsync(Window window, string name, bool check, List<string> failures, string output)
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
    var file = Path.Combine(output, $"{name}{languageSuffix}.png");
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
// DEV5: a button can sit inside the window and still cut its own label
// ("BESTELL", "ZWISCHE" at 1024x640). The label is measured with the
// button's font and compared to the room inside the button.
static void CheckLabelsFit(Window window, string[] names, List<string> failures)
{
    foreach (var name in names)
    {
        var button = window.GetVisualDescendants().OfType<Button>().SingleOrDefault(b => b.Name == name);
        if (button?.Content is not string label) { failures.Add($"{window.Title}: {name} has no text label"); continue; }
        var probe = new TextBlock { Text = label, FontSize = button.FontSize, FontWeight = button.FontWeight, FontFamily = button.FontFamily };
        probe.Measure(Size.Infinity);
        var room = button.Bounds.Width - button.Padding.Left - button.Padding.Right - button.BorderThickness.Left - button.BorderThickness.Right;
        if (probe.DesiredSize.Width > room + 1)
            failures.Add($"{window.Title}: {name} cuts its label \"{label}\" ({probe.DesiredSize.Width:0}px text in {room:0}px)");
    }
}

static void CheckNamedActions(Window window, string[] names, List<string> failures)
{
    foreach(var name in names)
    {
        var action=window.GetVisualDescendants().OfType<Control>().SingleOrDefault(c=>c.Name==name);
        var point=action?.TranslatePoint(new Point(),window);
        if(action is null || !action.IsVisible || point is null || point.Value.X<0 || point.Value.Y<0 ||
            point.Value.X+action.Bounds.Width>window.ClientSize.Width+1 || point.Value.Y+action.Bounds.Height>window.ClientSize.Height+1)
            failures.Add($"{window.Title}: {name} is outside the window");
    }
}

sealed class NoWindows(Func<RestaurantWorkspaceControl> createRestaurantWorkspace) : IAppWindowFactory
{
    public MainWindow CreateMainWindow(AuthenticatedUser user) => throw new NotSupportedException();
    public SettingsWindow CreateSettingsWindow(AuthenticatedUser user, string initialPage = "Allgemein") => throw new NotSupportedException();
    public RestaurantWorkspaceControl? Workspace { get; private set; }
    public RestaurantTablePlanWindow CreateRestaurantTablePlanWindow(
        AuthenticatedUser user) => throw new NotSupportedException();
    public RestaurantWorkspaceControl CreateRestaurantWorkspaceControl(
        AuthenticatedUser user) => Workspace = createRestaurantWorkspace();
    public RestaurantKdsWindow CreateRestaurantKdsWindow(
        AuthenticatedUser user) => throw new NotSupportedException();
    public RestaurantHandheldSetupWindow CreateRestaurantHandheldSetupWindow(
        AuthenticatedUser user) => throw new NotSupportedException();
    public RestaurantReservationsWindow CreateRestaurantReservationsWindow(
        AuthenticatedUser user) => throw new NotSupportedException();
    public DiagnosticsWindow CreateDiagnosticsWindow() => throw new NotSupportedException();
}
