using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// R78: post-commit TSE fiscal signing for one completed direct sale
/// (KIOSK checkout and IMBISS SALE-mode checkout only - the IMBISS
/// ORDER/Bestellung lifecycle is a separate, not-yet-covered TSE Vorgang).
///
/// Runs strictly AFTER the sale row is already durably committed via
/// ISaleRepository.CommitAsync. TSE signing failure (TSE-Ausfall) can never
/// block, roll back or alter an already-completed sale - it only means the
/// sale's TSE fields stay empty and the failure is logged through
/// TseFailSafeService/ITseOutageRepository, exactly like every other TSE
/// operation in this codebase.
///
/// One Kassenbeleg is signed as a single Start+Finish TSE transaction
/// (process type <see cref="FiscalProcessData.KassenbelegProcessType"/>).
/// See FiscalProcessData for the important caveat about its ProcessData
/// format not yet being legally validated.
///
/// NOTE: FiscalComplianceService currently hard-gates ProductionAllowed to
/// false (fiscalReleaseBuild=false in code), so MainWindow's real
/// (non-simulation) checkout path cannot reach this service in the shipped
/// build today. It exists so the wiring can be built and tested ahead of
/// that gate being lifted - it does not by itself make TOR POS fiscally
/// production-ready.
/// </summary>
public sealed class SaleFiscalSigningService
{
    private readonly TseFailSafeService _tse;
    private readonly ISettingsRepository _settings;
    private readonly ISaleRepository _sales;

    public SaleFiscalSigningService(
        TseFailSafeService tse,
        ISettingsRepository settings,
        ISaleRepository sales)
    {
        _tse = tse;
        _settings = settings;
        _sales = sales;
    }

    /// <summary>
    /// R136: the open TSE transactions of the till. Null where no Vorgang is
    /// tracked (tests, tools); a sale is then signed as one transaction.
    /// </summary>
    public TseVorgangService? Vorgaenge { get; init; }

    /// <summary>
    /// R136: ends the Vorgang whose TSE transaction was started with the first
    /// position of the cart. Without a tracked Vorgang the sale is signed as
    /// before, start and finish together.
    /// </summary>
    /// <summary>
    /// R138: a sale that was booked while its TSE transaction was never ended
    /// (the till stopped in between) is not an aborted Vorgang. AEAO zu § 146a
    /// Nr. 2.2.2 / 2.2.3.3: the transaction is ended when the Vorgang ends - so it
    /// is ended now with the data of that sale. A sale whose TSE record already
    /// exists only has its Vorgang closed. Runs before open Vorgänge without a
    /// cart are aborted (start-up, Z-Bericht).
    /// </summary>
    public async Task<int> FinishCommittedVorgaengeAsync(string actor, CancellationToken ct = default)
    {
        if (Vorgaenge is not { } vorgaenge)
            return 0;

        var count = 0;
        foreach (var (vorgangId, saleId, signed) in await vorgaenge.CommittedSalesWithOpenVorgangAsync(ct))
        {
            if (signed)
            {
                await vorgaenge.CloseUnsignedAsync(vorgangId, $"SALE:{saleId}", ct);
                count++;
                continue;
            }

            if (await _sales.GetByIdAsync(saleId, ct) is not { } sale)
                continue;
            await SignInVorgangAsync(sale, vorgangId, actor, ct);
            count++;
        }

        return count;
    }

    public async Task SignInVorgangAsync(Sale sale, string vorgangId, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sale);
        if (string.IsNullOrEmpty(vorgangId) || Vorgaenge is not { } vorgaenge)
        {
            await SignAsync(sale, actor, ct);
            return;
        }

        var reference = $"SALE:{sale.Id}";
        string processData;
        try
        {
            processData = FiscalProcessData.KassenbelegText(sale);
        }
        catch (UnsupportedVatRateException ex)
        {
            await vorgaenge.CloseUnsignedAsync(vorgangId, reference, ct);
            await ReportOutageAsync(sale, ex.Message, actor, ct);
            return;
        }

        var result = await vorgaenge.FinishAsync(vorgangId, FiscalProcessData.KassenbelegProcessType, processData, actor, reference, ct);
        await ApplyAsync(sale, result, ct);
    }

    public async Task SignAsync(Sale sale, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sale);

        var settings = await _settings.LoadAllAsync(ct);

        // R113: these two states used to `return` silently, which left the
        // sale with TseOutage=false - so nothing was logged AND the printed
        // receipt carried no "TSE-AUSFALL" note, i.e. an unsigned sale looked
        // exactly like a signed one. A productive sale can only reach this
        // method at all when FiscalComplianceService already considered the
        // TSE active, so finding it inactive here is a genuine outage.
        var tseStatus = (settings.GetValueOrDefault("tse.status") ?? "").Trim();
        if (!string.Equals(tseStatus, "AKTIV", StringComparison.OrdinalIgnoreCase))
        {
            await ReportOutageAsync(
                sale,
                $"TSE ist nicht aktiv (Status: {(tseStatus.Length == 0 ? "nicht gesetzt" : tseStatus)}). Beleg wurde ohne TSE-Signatur abgeschlossen.",
                actor,
                ct);
            return;
        }

        var clientId = (settings.GetValueOrDefault("tse.client_id") ?? "").Trim();
        if (clientId.Length == 0)
        {
            await ReportOutageAsync(
                sale,
                "TSE-Client-ID ist nicht konfiguriert. Beleg wurde ohne TSE-Signatur abgeschlossen.",
                actor,
                ct);
            return;
        }

        // R130: built before the TSE is touched. A rate that has no place in
        // the Anhang I tax containers cannot be signed correctly, and a
        // transaction must not be opened for data that will never be valid.
        byte[] processData;
        try
        {
            processData = FiscalProcessData.BuildKassenbeleg(sale);
        }
        catch (UnsupportedVatRateException ex)
        {
            await ReportOutageAsync(sale, ex.Message, actor, ct);
            return;
        }

        // R130: DSFinV-K Anhang I - StartTransaction carries neither
        // processType nor processData; both are handed over at Finish.
        var (startResult, _) = await _tse.StartTransactionAsync(
            new TseTransactionStartRequest(clientId, FiscalProcessData.StartProcessData, FiscalProcessData.StartProcessType),
            actor,
            ct);

        if (!startResult.Success)
        {
            await ApplyAsync(sale, SaleTseResult.Outage(startResult.Message), ct);
            return;
        }

        var (finishResult, _) = await _tse.FinishTransactionAsync(
            new TseTransactionFinishRequest(clientId, startResult.TransactionNumber, processData, FiscalProcessData.KassenbelegProcessType),
            actor,
            ct);

        if (!finishResult.Success)
        {
            await ApplyAsync(sale, SaleTseResult.Outage(finishResult.Message), ct);
            return;
        }

        var result = SaleTseResult.SignedResult(
            clientId,
            finishResult.TransactionNumber.ToString(),
            finishResult.SignatureCounter.ToString(),
            finishResult.SerialNumber,
            finishResult.SignatureBase64,
            finishResult.LogTime,
            startResult.LogTime);

        await ApplyAsync(sale, result, ct);
    }

    /// <summary>
    /// R113: documents a TSE that was unavailable before a transaction could
    /// even be started, and marks the sale as an outage so the receipt prints
    /// its legally required "TSE-AUSFALL" note. Same two effects the
    /// start/finish failure paths already had - they were simply missing on
    /// the "not configured / not active" paths.
    /// </summary>
    private async Task ReportOutageAsync(Sale sale, string reason, string actor, CancellationToken ct)
    {
        await _tse.ReportUnavailableAsync(reason, actor, ct);
        await ApplyAsync(sale, SaleTseResult.Outage(reason), ct);
    }

    private async Task ApplyAsync(Sale sale, SaleTseResult result, CancellationToken ct)
    {
        await _sales.RecordTseResultAsync(sale.Id, result, ct);

        // Mutate the in-memory Sale too so the immediate post-commit receipt
        // print (same request, same object) reflects the signature without
        // a redundant re-read from the database.
        sale.TseClientId = result.ClientId;
        sale.TseTransactionNumber = result.TransactionNumber;
        sale.TseSignatureCounter = result.SignatureCounter;
        sale.TseSerialNumber = result.SerialNumber;
        sale.TseSignature = result.Signature;
        sale.TseLogTime = result.LogTime;
        sale.TseStartLogTime = result.StartLogTime;
        sale.TseOutage = !result.Signed;
    }
}
