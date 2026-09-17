using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// R135: one Kassenbeleg-V1 transaction for a Vorgang whose data are complete -
/// start without data, finish with the Anhang I processData. Shared by the cash
/// movement (R134) and training (R135) signing; a TSE that is not active or not
/// configured, or a failing call, gives a documented outage, never an exception
/// that could undo the recorded Vorgang.
/// </summary>
internal static class TseKassenbelegSigner
{
    public static async Task<SaleTseResult> SignAsync(
        TseFailSafeService tse,
        ISettingsRepository settingsRepository,
        string processData,
        string vorgang,
        string actor,
        CancellationToken ct,
        string processType = FiscalProcessData.KassenbelegProcessType)
    {
        var settings = await settingsRepository.LoadAllAsync(ct);
        var tseStatus = (settings.GetValueOrDefault("tse.status") ?? "").Trim();
        if (!string.Equals(tseStatus, "AKTIV", StringComparison.OrdinalIgnoreCase))
        {
            var reason = $"TSE ist nicht aktiv (Status: {(tseStatus.Length == 0 ? "nicht gesetzt" : tseStatus)}). {vorgang} wurde ohne TSE-Signatur erfasst.";
            await tse.ReportUnavailableAsync(reason, actor, ct);
            return SaleTseResult.Outage(reason);
        }

        var clientId = (settings.GetValueOrDefault("tse.client_id") ?? "").Trim();
        if (clientId.Length == 0)
        {
            var reason = $"TSE-Client-ID ist nicht konfiguriert. {vorgang} wurde ohne TSE-Signatur erfasst.";
            await tse.ReportUnavailableAsync(reason, actor, ct);
            return SaleTseResult.Outage(reason);
        }

        var (start, _) = await tse.StartTransactionAsync(
            new TseTransactionStartRequest(clientId, FiscalProcessData.StartProcessData, FiscalProcessData.StartProcessType),
            actor,
            ct);
        if (!start.Success)
            return SaleTseResult.Outage(start.Message);

        var (finish, _) = await tse.FinishTransactionAsync(
            new TseTransactionFinishRequest(clientId, start.TransactionNumber, System.Text.Encoding.UTF8.GetBytes(processData), processType),
            actor,
            ct);
        if (!finish.Success)
            return SaleTseResult.Outage(finish.Message);

        return SaleTseResult.SignedResult(
            clientId,
            finish.TransactionNumber.ToString(),
            finish.SignatureCounter.ToString(),
            finish.SerialNumber,
            finish.SignatureBase64,
            finish.LogTime,
            start.LogTime);
    }
}

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

        var result = await TseKassenbelegSigner.SignAsync(
            _tse, _settings, FiscalProcessData.CashMovementText(movement), "Kassenbewegung", actor, ct);
        await _movements.RecordTseResultAsync(movement.Id, result, ct);
        return result;
    }
}
