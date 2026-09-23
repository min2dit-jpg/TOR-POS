using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using CheckoutApplicationService = TorPos.Application.CheckoutApplicationService;
using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.App;

public partial class App : Avalonia.Application
{
    public static TorCloudSyncService? CloudSync { get; private set; }
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // IMPORTANT:
            // Do not make this override async void. Avalonia may complete startup
            // before MainWindow is assigned and the process can exit with no window.
            var loading = new StartupLoadingWindow();
            desktop.MainWindow = loading;

            loading.Opened += async (_, _) =>
            {
                await InitializeDesktopAsync(desktop, loading);
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task InitializeDesktopAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        Window startupWindow)
    {
        try
        {
            // Keep the branded startup screen visible long enough that the
            // customer sees a clean splash instead of a brief blank transition.
            var minimumSplashTime = Task.Delay(900);

            CrashLog.Write("Startup initialization started.");

#if TOR_DEMO_BUILD
            using (var trial = new TrialLicenseService())
            {
                var trialStatus = await trial.CheckOrActivateAsync();
                TrialRuntime.Set(trialStatus);
                CrashLog.Write(
                    $"Demo trial: state={trialStatus.State}; offline={trialStatus.Offline}; " +
                    $"reused={trialStatus.Reused}; expires={trialStatus.ExpiresAtUtc:O}");

                if (!trialStatus.IsActive)
                {
                    var blocked = new TrialBlockedWindow(trialStatus);
                    desktop.MainWindow = blocked;
                    blocked.Closed += (_, _) => desktop.Shutdown();
                    blocked.Show();
                    startupWindow.Close();
                    return;
                }
            }
#endif

            var db = new SqliteDatabase();

            // R69: every existing pre-R69 customer database is backed up before
            // schema adoption/migration. If the backup cannot be verified, startup
            // stops before any migration may change the schema.
            var backup = new DatabaseBackupService(db);
            var schemaMigrations = new SchemaMigrationService(db, backup);
            var migrationResult =
                await schemaMigrations.InitializeDatabaseAsync();

            CrashLog.Write(
                $"Schema migration: {migrationResult.FromVersion} -> " +
                $"{migrationResult.ToVersion}; backup=" +
                $"{migrationResult.BackupPath ?? "not required"}");

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
            await TorUpdateService.CleanupCacheAsync(settings);
            CloudSync = new TorCloudSyncService(db);
            CloudSync.Start();
            var identity = new SystemIdentityRepository(db);
            var management = new BusinessManagementService(db, settings, audit);
            // R132 (DSFinV-K 3.2): Vorgänge recorded under an older TOR
            // version are closed under that version before anything is
            // booked under the new one.
            try
            {
                var masterData = new DsfinvkMasterDataService(db, settings, management, dailyClosingGuard, audit);
                var updateClosing = await masterData.EnsureSoftwareVersionAsync("SYSTEM");
                if (updateClosing is not null)
                    CrashLog.Write($"R132 automatic closing Z {updateClosing.ZNumber} after software update");
            }
            catch (Exception ex)
            {
                CrashLog.WriteException("R132 software version closing check", ex);
            }
            var starterCatalog = new ImbissStarterCatalogService(db);
            var dailyBackup = new DailyBackupScheduler(backup, settings);
            dailyBackup.Start();
            var googleGmail = new GoogleGmailService(settings, CloudSync);
            var reportEmail = new ReportEmailService(settings, management, googleGmail, CloudSync);
            var datevAscii = new DatevKassenbuchAsciiService(
                db,
                settings,
                management,
                reportEmail,
                audit);
            var monthlyReports = new MonthlyReportScheduler(settings, reportEmail);
            monthlyReports.Start();
            var receiptPrinter = new StarMcPrint3PrinterService();
            // R119 follow-up: the outbox is now shared with the Diagnose window,
            // which lists the tickets the queue gave up on and can re-queue them.
            var orderPrintOutbox = new OrderPrintOutbox(db);
            var orderPrintDispatcher = new OrderPrintDispatcher(orderPrintOutbox,new PrintJobJournal(),receiptPrinter.SubmitOrderAsync,ex=>CrashLog.WriteException("Order print dispatch",ex));
            parkedReceipts.PrintCommitted = orderPrintDispatcher.Notify;
            orderPrintDispatcher.Start();
            var restaurantKitchenOutbox = new RestaurantKitchenOutbox(db);
            var restaurantPrintJournal = new PrintJobJournal();
            var restaurantKitchenPrinterRouter =
                new RestaurantKitchenPrinterRouter(
                    restaurantPrintJournal);
            var restaurantKitchenDispatcher = new RestaurantKitchenDispatcher(
                restaurantKitchenOutbox,
                settings,
                restaurantKitchenPrinterRouter,
                restaurantPrintJournal,
                ex => CrashLog.WriteException("Restaurant kitchen dispatch", ex));
            restaurantKitchenDispatcher.Start();
            var commercialLicense = new CommercialLicenseService();
            var tseProvider = new SwissbitHardwareTseProvider();
            var tseOutages = new TseOutageRepository(db, audit);
            var tseFailSafe = new TseFailSafeService(
                tseProvider,
                tseOutages,
                audit);
            // R136: the TSE transaction starts with the first position of a Vorgang.
            var tseVorgaenge = new TseVorgangService(db, tseFailSafe, settings);
            var fiscalSigning = new SaleFiscalSigningService(
                tseFailSafe,
                settings,
                sales)
            { Vorgaenge = tseVorgaenge };
            var orderFiscalSigning = new OrderFiscalSigningService(
                tseFailSafe,
                settings,
                parkedReceipts)
            {
                Vorgaenge = tseVorgaenge,
                // R137: acceptance, change and cancellation of an order, each secured.
                Bestellungen = new OrderBestellungRepository(db),
            };

            var preflight =
                new StartupPreflightService(
                    db,
                    tseProvider);

            var preflightResult =
                await Task.Run(() => preflight.RunAsync()).WaitAsync(TimeSpan.FromSeconds(20));

            foreach (var warning in preflightResult.Warnings)
                CrashLog.Write("Startup warning: " + warning);

            var cashMovements = new CashMovementRepository(db, audit);
            // R145: the digital receipt is published to TOR Cloud with this
            // till's device credentials; the local receipt server of R103 is gone.
            var digitalReceipts = new CloudDigitalReceiptService(settings, CloudSync);
            var checkoutJournal = new CheckoutJournal(db);
            var paymentTerminal = new ZvtPaymentTerminalService(settings, audit, checkoutJournal);
            var dsfinvkExport =
                new DsfinvkExportService(
                    db,
                    settings);

            var datevKassenarchiv =
                new DatevKassenarchivService(
                    db,
                    settings,
                    dsfinvkExport,
                    tseProvider,
                    audit);

            var compliance = new FiscalComplianceService(
                identity,
                settings,
                tseProvider,
                commercialLicense);

            var checkoutApplication =
                new CheckoutApplicationService(
                    compliance,
                    checkoutJournal,
                    paymentTerminal,
                    catalog);

            // R68 DI foundation:
            // Async initialization remains explicit, but shared application
            // services are registered once and windows resolve dependencies
            // through a central factory instead of repeating constructor wiring.
            var appServices = new ServiceCollection();
            appServices.AddSingleton<IProductCatalog>(catalog);
            appServices.AddSingleton<IProductRepository>(repo);
            appServices.AddSingleton<ISaleRepository>(sales);
            appServices.AddSingleton<ICardRefundLockRepository>(cardRefundLocks);
            appServices.AddSingleton<IParkedReceiptRepository>(parkedReceipts);
            appServices.AddSingleton<IDailyClosingGuard>(dailyClosingGuard);
            appServices.AddSingleton<ICashMovementRepository>(cashMovements);
            appServices.AddSingleton<IAuditLog>(audit);
            appServices.AddSingleton<IFiscalComplianceService>(compliance);
            appServices.AddSingleton<IDsfinvkExportService>(dsfinvkExport);
            appServices.AddSingleton(datevAscii);
            appServices.AddSingleton(datevKassenarchiv);
            appServices.AddSingleton<IPaymentTerminalService>(paymentTerminal);
            appServices.AddSingleton<ISettingsRepository>(settings);
            appServices.AddSingleton<ITseProvider>(tseProvider);
            appServices.AddSingleton<IReceiptPrinterService>(receiptPrinter);
            appServices.AddSingleton<IDigitalReceiptPublisher>(digitalReceipts);
            appServices.AddSingleton<ICommercialLicenseService>(commercialLicense);
            var restaurantEntitlements =
                new RestaurantEntitlementService(commercialLicense);
            appServices.AddSingleton(restaurantEntitlements);
            appServices.AddSingleton(
                new RestaurantHandheldPairingService(
                    db,
                    restaurantEntitlements));
            appServices.AddSingleton(auth);
            appServices.AddSingleton<IAuthenticationService>(auth);

            appServices.AddSingleton(perf);
            appServices.AddSingleton(backup);
            appServices.AddSingleton(schemaMigrations);
            appServices.AddSingleton(new DatabaseHealthService(db));
            appServices.AddSingleton(new ControlledPosActionService(db));
            appServices.AddSingleton(new PromotionCampaignService(db));
            appServices.AddSingleton(management);
            appServices.AddSingleton(checkoutJournal);
            appServices.AddSingleton<ICheckoutJournal>(checkoutJournal);
            appServices.AddSingleton(checkoutApplication);
            appServices.AddSingleton(fiscalSigning);
            appServices.AddSingleton(orderFiscalSigning);
            appServices.AddSingleton(tseFailSafe);
            appServices.AddSingleton(orderPrintOutbox);
            appServices.AddSingleton(new ProductImageStore());
            appServices.AddSingleton(new RestaurantRepository(db));
            appServices.AddSingleton(
                new RestaurantReservationService(
                    db,
                    restaurantEntitlements));
            appServices.AddSingleton(
                new RestaurantTerminalRegistry(
                    db,
                    restaurantEntitlements));
            appServices.AddSingleton(
                new RestaurantSyncService(
                    db,
                    restaurantEntitlements));
            appServices.AddSingleton(
                new RestaurantCommandJournal(db));
            appServices.AddSingleton(
                new RestaurantOperatorSessionService(
                    db,
                    restaurantEntitlements,
                    auth));
            appServices.AddSingleton(new RestaurantFiscalOrderService(db, tseVorgaenge));
            appServices.AddSingleton(restaurantKitchenOutbox);
            appServices.AddSingleton(restaurantKitchenDispatcher);
            appServices.AddSingleton<TorPos.Application.IRestaurantHandheldService, RestaurantHandheldService>();
            appServices.AddSingleton<RestaurantLocalApiHost>();
            appServices.AddSingleton<IAppWindowFactory, AppWindowFactory>();

            var serviceProvider = appServices.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true
                });

            var windowFactory =
                serviceProvider.GetRequiredService<IAppWindowFactory>();
            var restaurantLocalApi =
                serviceProvider.GetRequiredService<RestaurantLocalApiHost>();

            await settings.SaveManyAsync(new Dictionary<string,string>
            {
                ["function.login_required"] = "true",
                ["function.single_operator"] = "false"
            });
            var uiSettings = await settings.LoadAllAsync();
            UiLanguage.Set(uiSettings.GetValueOrDefault("ui.language", "DE"));
            TouchKeyboard.Install(uiSettings.GetValueOrDefault("ui.keyboard.auto", "true") != "false");

            await catalog.ReloadAsync();

            _ = Task.Run(async () =>
            {
                try
                {
                    await restaurantLocalApi.StartOrRestartAsync();
                }
                catch (Exception ex)
                {
                    CrashLog.WriteException(
                        "Restaurant local API startup failed",
                        ex);
                }
            });

            // Shared services live for the complete application process. A simple
            // ABMELDEN must not dispose the printer or create an exit backup.
            // The backup is created exactly when the application itself exits.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var exitCleanupDone = false;
            var shutdownStarted = false;
            async Task ShutdownSafelyAsync()
            {
                if(shutdownStarted) return;
                shutdownStarted=true;
                var closing=new Window
                {
                    Title="TOR POS · Beenden", Width=480, Height=170,
                    Content=new TextBlock {Text="Datensicherung wird erstellt …",Margin=new Thickness(25)},
                    WindowStartupLocation=WindowStartupLocation.CenterScreen
                };
                closing.Closing+=(_,e)=> { if(!exitCleanupDone) e.Cancel=true; };
                desktop.MainWindow=closing; closing.Show();
                await dailyBackup.DisposeAsync();
                await monthlyReports.DisposeAsync();
                googleGmail.Dispose();
                if(CloudSync is not null) await CloudSync.DisposeAsync();
                try
                {
                    var allSettings=await settings.LoadAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    if(!allSettings.TryGetValue("backup.on_exit",out var raw) || raw!="false")
                    {
                        var backupPath=await backup.CreateBackupAsync(allSettings.GetValueOrDefault("backup.directory", ""))
                            .WaitAsync(TimeSpan.FromSeconds(10));
                        CrashLog.Write("Exit backup created: "+backupPath);
                    }
                }
                catch(Exception ex) { CrashLog.WriteException("Exit backup failed or timed out",ex); }
                await orderPrintDispatcher.DisposeAsync();
                try { await restaurantLocalApi.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch(Exception ex) { CrashLog.WriteException("Restaurant local API shutdown failed",ex); }
                try { await restaurantKitchenDispatcher.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch(Exception ex) { CrashLog.WriteException("Restaurant kitchen dispatcher shutdown failed",ex); }
                try { await restaurantKitchenPrinterRouter.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch(Exception ex) { CrashLog.WriteException("Restaurant kitchen printer lanes shutdown failed",ex); }
                try { await receiptPrinter.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch(Exception ex) { CrashLog.WriteException("Printer shutdown failed",ex); }
                exitCleanupDone=true;
                CrashLog.MarkCleanShutdown();
                desktop.Shutdown();
            }
            desktop.ShutdownRequested+=(_,e)=>
            {
                if(exitCleanupDone) return;
                e.Cancel=true;
                if(!shutdownStarted) desktop.MainWindow?.Close();
            };
            desktop.Exit+=(_,_)=>
            {
                if(!exitCleanupDone) CrashLog.Write("Exit without completed cleanup; running marker retained.");
            };

            string? ResolveLockedEdition()
            {
                var builtEdition = ProductBuild.FixedEdition;
                var permanent = InstallationEdition.ReadPermanent();
                var kiosk = commercialLicense.Check("KIOSK");
                var imbiss = commercialLicense.Check("IMBISS");
                var restaurant = commercialLicense.Check("RESTAURANT");
                var licensed = kiosk.IsActive
                    ? "KIOSK"
                    : imbiss.IsActive
                        ? "IMBISS"
                        : restaurant.IsActive
                            ? "RESTAURANT"
                            : null;

                // Dedicated TOR Einzelhandel / TOR Gastro / TOR Restaurant builds are
                // authoritative. Another product edition is never offered even in
                // licence-free validation mode.
                if (builtEdition is not null)
                {
                    if (licensed is not null &&
                        !string.Equals(licensed, builtEdition, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Diese Lizenz gehört nicht zu {ProductBuild.ProductName}.");
                    }

                    if (permanent is not null &&
                        !string.Equals(permanent, builtEdition, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Installations-Edition und Produkt-Build widersprechen sich.");
                    }

                    return builtEdition;
                }

                if (licensed is not null &&
                    permanent is not null &&
                    !string.Equals(licensed, permanent, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Installations-Edition und signierte Lizenz-Edition widersprechen sich.");
                }

                return licensed ?? permanent;
            }

            void OpenLogin(Window? closeAfter = null)
            {
                var lockedEdition = ResolveLockedEdition();
                var login = new LoginWindow(auth, settings, lockedEdition);

                login.ExitRequested += () =>
                {
                    CrashLog.Write("Program exit requested from login window.");
                    login.Close();
                };

                login.Closed+=async (_,_)=> { if(desktop.MainWindow==login) await ShutdownSafelyAsync(); };
                login.LoginSucceeded += async user =>
                {
                    try
                    {
                        if (user.IsAdmin && user.MustChangePassword)
                        {
                            if (!await new RequiredAdminCredentialsWindow(auth).ShowDialog<bool>(login)) return;
                            user = user with { MustChangePassword=false };
                        }
                        var selectedEdition = login.SelectedEdition;
                        if (selectedEdition is null)
                        {
                            CrashLog.Write("Login succeeded but no KIOSK/IMBISS edition was selected.");
                            return;
                        }

                        await InstallationEdition.EnforceAsync(
                            settings,
                            selectedEdition,
                            permanentLock: lockedEdition is not null);

                        // R51: apply the edition-specific starter assortment only
                        // after the operator explicitly selected IMBISS. KIOSK never
                        // receives Döner/Burger/Fingerfood/Pizza defaults.
                        await starterCatalog.EnsureAsync(selectedEdition);
                        await catalog.ReloadAsync();

                        // R19: on a fresh installation, guide the operator through
                        // the minimum setup before the first cashier screen opens.
                        // Closing the wizard does not mark it complete; it will be
                        // offered again at the next successful login.
                        var firstRunKey = $"installation.first_run_completed.{selectedEdition}";
                        var firstRunDone = await settings.GetAsync(firstRunKey, "false");
                        if (!string.Equals(firstRunDone, "true", StringComparison.OrdinalIgnoreCase))
                        {
                            var wizard = new FirstRunSetupWindow(
                                settings, receiptPrinter, tseProvider, paymentTerminal, selectedEdition);
                            await wizard.ShowDialog(login);
                            if (!wizard.Completed)
                            {
                                CrashLog.Write("First-run setup closed before completion.");
                                return;
                            }
                        }

                        var main = windowFactory.CreateMainWindow(user);

                        main.Closed+=async (_,_)=> { if(desktop.MainWindow==main) await ShutdownSafelyAsync(); };
                        main.LogoutRequested += () =>
                        {
                            CrashLog.Write($"Logout requested by '{user.Username}'.");
                            OpenLogin(main);
                        };

                        desktop.MainWindow = main;
                        main.Show();
                        login.Close();

                        CrashLog.Write($"Login completed. Main window opened for user '{user.Username}'.");
                    }
                    catch (Exception ex)
                    {
                        CrashLog.WriteException("MainWindow creation failed.", ex);
                        ShowFatalError(desktop, login, ex);
                    }
                };

                desktop.MainWindow = login;
                login.Show();

                if (closeAfter is not null && closeAfter != login)
                    closeAfter.Close();

                CrashLog.Write("Login window opened.");
            }

            await minimumSplashTime;
            OpenLogin(startupWindow);
        }
        catch (Exception ex)
        {
            CrashLog.WriteException("Startup initialization failed.", ex);
            ShowFatalError(desktop, startupWindow, ex);
        }
    }

    private static void ShowFatalError(
        IClassicDesktopStyleApplicationLifetime desktop,
        Window? currentWindow,
        Exception ex)
    {
        try
        {
            var error = new StartupErrorWindow(
                ex.ToString(),
                CrashLog.LogPath);

            desktop.MainWindow = error;
            error.Closed+=(_,_)=>desktop.Shutdown();
            error.Show();

            if (currentWindow is not null && currentWindow != error)
                currentWindow.Close();
        }
        catch
        {
            // If even the graphical error window cannot open, the text log remains.
        }
    }
}
