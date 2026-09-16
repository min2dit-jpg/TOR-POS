using Microsoft.Extensions.DependencyInjection;
using CheckoutApplicationService = TorPos.Application.CheckoutApplicationService;
using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.App;

/// <summary>
/// R68 DI foundation.
/// Shared application services are resolved through Microsoft.Extensions.DependencyInjection.
/// Runtime state such as the currently logged-in user is supplied explicitly.
/// </summary>
public interface IAppWindowFactory
{
    MainWindow CreateMainWindow(AuthenticatedUser user);

    SettingsWindow CreateSettingsWindow(
        AuthenticatedUser user,
        string initialPage = "Allgemein");

    DiagnosticsWindow CreateDiagnosticsWindow();
}

internal sealed class AppWindowFactory : IAppWindowFactory
{
    private readonly IServiceProvider _services;

    public AppWindowFactory(IServiceProvider services)
    {
        _services = services;
    }

    public MainWindow CreateMainWindow(AuthenticatedUser user) =>
        new(
            _services.GetRequiredService<IProductCatalog>(),
            _services.GetRequiredService<IProductRepository>(),
            _services.GetRequiredService<ISaleRepository>(),
            _services.GetRequiredService<IParkedReceiptRepository>(),
            _services.GetRequiredService<IDailyClosingGuard>(),
            _services.GetRequiredService<ICashMovementRepository>(),
            _services.GetRequiredService<IAuditLog>(),
            _services.GetRequiredService<IFiscalComplianceService>(),
            _services.GetRequiredService<IDsfinvkExportService>(),
            _services.GetRequiredService<ProductImageStore>(),
            _services.GetRequiredService<PerformanceCounters>(),
            _services.GetRequiredService<ISettingsRepository>(),
            _services.GetRequiredService<DatabaseBackupService>(),
            _services.GetRequiredService<ITseProvider>(),
            _services.GetRequiredService<IReceiptPrinterService>(),
            _services.GetRequiredService<IDigitalReceiptService>(),
            _services.GetRequiredService<ICardRefundLockRepository>(),
            _services.GetRequiredService<ICommercialLicenseService>(),
            _services.GetRequiredService<IAuthenticationService>(),
            _services.GetRequiredService<BusinessManagementService>(),
            user,
            _services.GetRequiredService<ICheckoutJournal>(),
            _services.GetRequiredService<CheckoutApplicationService>(),
            _services.GetRequiredService<ControlledPosActionService>(),
            _services.GetRequiredService<PromotionCampaignService>(),
            _services.GetRequiredService<SaleFiscalSigningService>(),
            _services.GetRequiredService<OrderFiscalSigningService>(),
            _services.GetRequiredService<TseFailSafeService>(),
            this);

    public SettingsWindow CreateSettingsWindow(
        AuthenticatedUser user,
        string initialPage = "Allgemein") =>
        new(
            _services.GetRequiredService<ISettingsRepository>(),
            _services.GetRequiredService<DatabaseBackupService>(),
            _services.GetRequiredService<PerformanceCounters>(),
            _services.GetRequiredService<ITseProvider>(),
            _services.GetRequiredService<IReceiptPrinterService>(),
            _services.GetRequiredService<IPaymentTerminalService>(),
            _services.GetRequiredService<IFiscalComplianceService>(),
            _services.GetRequiredService<IDsfinvkExportService>(),
            _services.GetRequiredService<IAuditLog>(),
            _services.GetRequiredService<ICommercialLicenseService>(),
            _services.GetRequiredService<IAuthenticationService>(),
            user,
            this,
            initialPage);

    public DiagnosticsWindow CreateDiagnosticsWindow() =>
        new(
            _services.GetRequiredService<PerformanceCounters>(),
            _services.GetRequiredService<ISettingsRepository>(),
            _services.GetRequiredService<IReceiptPrinterService>(),
            _services.GetRequiredService<IPaymentTerminalService>(),
            _services.GetRequiredService<ITseProvider>(),
            _services.GetRequiredService<SchemaMigrationService>(),
            _services.GetRequiredService<ControlledPosActionService>(),
            _services.GetRequiredService<PromotionCampaignService>(),
            _services.GetRequiredService<AuthenticationService>(),
            _services.GetRequiredService<ICheckoutJournal>(),
            _services.GetRequiredService<DatabaseHealthService>(),
            _services.GetRequiredService<ICardRefundLockRepository>(),
            _services.GetRequiredService<OrderPrintOutbox>());
}
