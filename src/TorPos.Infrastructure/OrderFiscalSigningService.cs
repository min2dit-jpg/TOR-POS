using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// R83: post-acceptance TSE fiscal signing for one IMBISS ORDER, as its own
/// "Bestellung-V1" Vorgang - separate from the "Kassenbeleg-V1" signed later
/// at payment time by SaleFiscalSigningService, when the order is actually
/// cashed. This only covers the ORDER-mode acceptance step (pickup number
/// assigned, kitchen informed); ordinary KIOSK/IMBISS SALE checkout has
/// nothing to sign here and never calls this service.
///
/// Runs strictly AFTER the order row is already durably committed via
/// IParkedReceiptRepository.ParkAsync. TSE signing failure (TSE-Ausfall) can
/// never block or undo an already-accepted order - exactly the same legal
/// behavior as SaleFiscalSigningService, via the same TseFailSafeService.
///
/// NOTE: same production gate caveat as SaleFiscalSigningService - this
/// exists to build and test the wiring ahead of FiscalComplianceService's
/// gates being lifted, not to claim current legal production-readiness.
/// </summary>
public sealed class OrderFiscalSigningService
{
    private readonly TseFailSafeService _tse;
    private readonly ISettingsRepository _settings;
    private readonly IParkedReceiptRepository _orders;

    public OrderFiscalSigningService(
        TseFailSafeService tse,
        ISettingsRepository settings,
        IParkedReceiptRepository orders)
    {
        _tse = tse;
        _settings = settings;
        _orders = orders;
    }

    public async Task SignAsync(ParkedReceipt order, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);

        var settings = await _settings.LoadAllAsync(ct);

        // R113: same silent-return bug as SaleFiscalSigningService had - an
        // accepted order left unsigned with no outage record and no trace.
        var tseStatus = (settings.GetValueOrDefault("tse.status") ?? "").Trim();
        if (!string.Equals(tseStatus, "AKTIV", StringComparison.OrdinalIgnoreCase))
        {
            await ReportOutageAsync(
                order,
                $"TSE ist nicht aktiv (Status: {(tseStatus.Length == 0 ? "nicht gesetzt" : tseStatus)}). Bestellung wurde ohne TSE-Signatur angenommen.",
                actor,
                ct);
            return;
        }

        var clientId = (settings.GetValueOrDefault("tse.client_id") ?? "").Trim();
        if (clientId.Length == 0)
        {
            await ReportOutageAsync(
                order,
                "TSE-Client-ID ist nicht konfiguriert. Bestellung wurde ohne TSE-Signatur angenommen.",
                actor,
                ct);
            return;
        }

        var processData = FiscalProcessData.BuildBestellung(order);

        var (startResult, _) = await _tse.StartTransactionAsync(
            new TseTransactionStartRequest(clientId, processData, FiscalProcessData.BestellungProcessType),
            actor,
            ct);

        if (!startResult.Success)
        {
            await ApplyAsync(order, SaleTseResult.Outage(startResult.Message), ct);
            return;
        }

        var (finishResult, _) = await _tse.FinishTransactionAsync(
            new TseTransactionFinishRequest(clientId, startResult.TransactionNumber, processData, FiscalProcessData.BestellungProcessType),
            actor,
            ct);

        if (!finishResult.Success)
        {
            await ApplyAsync(order, SaleTseResult.Outage(finishResult.Message), ct);
            return;
        }

        var result = SaleTseResult.SignedResult(
            clientId,
            finishResult.TransactionNumber.ToString(),
            finishResult.SignatureCounter.ToString(),
            finishResult.SerialNumber,
            finishResult.SignatureBase64,
            finishResult.LogTime);

        await ApplyAsync(order, result, ct);
    }

    /// <summary>R113: see SaleFiscalSigningService.ReportOutageAsync.</summary>
    private async Task ReportOutageAsync(ParkedReceipt order, string reason, string actor, CancellationToken ct)
    {
        await _tse.ReportUnavailableAsync(reason, actor, ct);
        await ApplyAsync(order, SaleTseResult.Outage(reason), ct);
    }

    private async Task ApplyAsync(ParkedReceipt order, SaleTseResult result, CancellationToken ct)
    {
        await _orders.RecordTseResultAsync(order.Id, result, ct);

        order.TseClientId = result.ClientId;
        order.TseTransactionNumber = result.TransactionNumber;
        order.TseSignatureCounter = result.SignatureCounter;
        order.TseSerialNumber = result.SerialNumber;
        order.TseSignature = result.Signature;
        order.TseLogTime = result.LogTime;
        order.TseOutage = !result.Signed;
    }
}
