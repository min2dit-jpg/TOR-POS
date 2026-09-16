using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// R134: TSE signing of a production Einlage / Entnahme.
///
/// AEAO zu § 146a Nr. 1.10.2 lists Privatentnahme, Privateinlage,
/// Wechselgeld-Einlage, Lohnzahlung aus der Kasse and Geldtransit as
/// Geschäftsvorfälle, so they are secured like a sale: one Kassenbeleg-V1
/// transaction, start without data, finish with the Anhang I processData
/// (0 % container, Bar). No receipt has to be issued for them (AEAO Nr. 2.5.5).
///
/// Runs after the movement is stored and, like SaleFiscalSigningService, can
/// never undo it: a TSE failure is recorded as a documented outage.
/// </summary>
public sealed class CashMovementFiscalSigningService
{
    private readonly TseFailSafeService _tse;
    private readonly ISettingsRepository _settings;
    private readonly ICashMovementRepository _movements;

    public CashMovementFiscalSigningService(
        TseFailSafeService tse,
        ISettingsRepository settings,
        ICashMovementRepository movements)
    {
        _tse = tse;
        _settings = settings;
        _movements = movements;
    }

    public async Task<SaleTseResult> SignAsync(CashMovement movement, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(movement);
        if (movement.FiscalMode != CashMovement.ProductionMode)
            throw new InvalidOperationException("Nur echte (Produktiv-)Kassenbewegungen werden von der TSE abgesichert.");

        var settings = await _settings.LoadAllAsync(ct);
        var tseStatus = (settings.GetValueOrDefault("tse.status") ?? "").Trim();
        if (!string.Equals(tseStatus, "AKTIV", StringComparison.OrdinalIgnoreCase))
        {
            return await OutageAsync(
                movement,
                $"TSE ist nicht aktiv (Status: {(tseStatus.Length == 0 ? "nicht gesetzt" : tseStatus)}). Kassenbewegung wurde ohne TSE-Signatur gebucht.",
                actor,
                reportUnavailable: true,
                ct);
        }

        var clientId = (settings.GetValueOrDefault("tse.client_id") ?? "").Trim();
        if (clientId.Length == 0)
        {
            return await OutageAsync(
                movement,
                "TSE-Client-ID ist nicht konfiguriert. Kassenbewegung wurde ohne TSE-Signatur gebucht.",
                actor,
                reportUnavailable: true,
                ct);
        }

        var processData = FiscalProcessData.BuildCashMovement(movement);

        var (start, _) = await _tse.StartTransactionAsync(
            new TseTransactionStartRequest(clientId, FiscalProcessData.StartProcessData, FiscalProcessData.StartProcessType),
            actor,
            ct);
        if (!start.Success)
            return await OutageAsync(movement, start.Message, actor, reportUnavailable: false, ct);

        var (finish, _) = await _tse.FinishTransactionAsync(
            new TseTransactionFinishRequest(clientId, start.TransactionNumber, processData, FiscalProcessData.KassenbelegProcessType),
            actor,
            ct);
        if (!finish.Success)
            return await OutageAsync(movement, finish.Message, actor, reportUnavailable: false, ct);

        var result = SaleTseResult.SignedResult(
            clientId,
            finish.TransactionNumber.ToString(),
            finish.SignatureCounter.ToString(),
            finish.SerialNumber,
            finish.SignatureBase64,
            finish.LogTime);
        await _movements.RecordTseResultAsync(movement.Id, result, ct);
        return result;
    }

    private async Task<SaleTseResult> OutageAsync(CashMovement movement, string reason, string actor, bool reportUnavailable, CancellationToken ct)
    {
        // The TSE calls themselves already opened the outage record on
        // failure; an inactive or unconfigured TSE has to report it here.
        if (reportUnavailable)
            await _tse.ReportUnavailableAsync(reason, actor, ct);

        var result = SaleTseResult.Outage(reason);
        await _movements.RecordTseResultAsync(movement.Id, result, ct);
        return result;
    }
}
