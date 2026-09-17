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

    /// <summary>R136: see SaleFiscalSigningService.Vorgaenge.</summary>
    public TseVorgangService? Vorgaenge { get; init; }

    /// <summary>
    /// R137: the immutable order records (acceptance, change, cancellation).
    /// Null where they are not kept (tests, tools); an acceptance is then only
    /// signed, as before R137.
    /// </summary>
    public OrderBestellungRepository? Bestellungen { get; init; }

    /// <summary>R137: the TSE has secured positions of this order - records, or a signature from before R137.</summary>
    public async Task<bool> IsSecuredAsync(ParkedReceipt order, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.TseTransactionNumber.Length > 0 || order.TseOutage)
            return true;
        return Bestellungen is not null && (await Bestellungen.SecuredAsync(order.Id, ct)).Count > 0;
    }

    /// <summary>
    /// R137: an accepted order was changed (DSFinV-K 4.2.3). The difference
    /// between the secured positions and the order as it is now becomes its own
    /// Bestellung-V1 transaction - finishing the Vorgang that began with the
    /// first change. No difference: nothing is signed, and a Vorgang begun for
    /// the change ends as aborted. <paramref name="previousLines"/> are the
    /// positions before the change; for an order accepted before R137 they are
    /// written down as its acceptance record first.
    /// R146: <paramref name="cancelledLines"/> are the positions cancelled while the
    /// change was captured (see <see cref="SecureAsync"/>).
    /// </summary>
    public Task<DsfinvkOrderRecord?> SecureChangeAsync(ParkedReceipt order, IReadOnlyList<CartLine> previousLines, string vorgangId, DateTimeOffset? startedAt, string actor, CancellationToken ct = default,
        IReadOnlyList<CartLine>? cancelledLines = null) =>
        SecureAsync(order, order.Lines, previousLines, vorgangId, startedAt, actor, cancelledLines, ct);

    /// <summary>
    /// R137: an accepted order was cancelled. DSFinV-K 4.2.3: "ein neuer
    /// Datensatz mit umgekehrtem Vorzeichen, der wiederum abgesichert werden
    /// muss" - everything secured so far, negated, as its own transaction.
    /// </summary>
    public Task<DsfinvkOrderRecord?> SecureCancellationAsync(ParkedReceipt order, string vorgangId, DateTimeOffset? startedAt, string actor, CancellationToken ct = default,
        IReadOnlyList<CartLine>? cancelledLines = null) =>
        SecureAsync(order, Array.Empty<CartLine>(), order.Lines, vorgangId, startedAt, actor, cancelledLines, ct);

    private async Task<DsfinvkOrderRecord?> SecureAsync(
        ParkedReceipt order,
        IReadOnlyList<CartLine> orderLines,
        IReadOnlyList<CartLine>? legacyLines,
        string vorgangId,
        DateTimeOffset? startedAt,
        string actor,
        IReadOnlyList<CartLine>? cancelledLines,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (Bestellungen is not { } records)
            throw new InvalidOperationException("Bestelländerungen können ohne Bestellaufzeichnung nicht abgesichert werden.");

        var (count, secured) = await records.SecuredAsync(order.Id, ct);
        if (count == 0 && (order.TseTransactionNumber.Length > 0 || order.TseOutage))
        {
            // An order accepted before R137 has no records. Its acceptance is
            // written down first - with the TSE result it received then and the
            // positions it had before this change - so that every later record,
            // a cancellation too, accounts for it.
            var accepted = new SaleTseResult(
                !order.TseOutage, order.TseClientId, order.TseTransactionNumber, order.TseSignatureCounter,
                order.TseSerialNumber, order.TseSignature, order.TseLogTime,
                order.TseOutage ? "TSE-Ausfall bei der Bestellannahme (vor R137)" : "", order.TseStartLogTime);
            await records.InsertAsync(
                order, OrderBestellungKind.Annahme, order.VorgangStartedAt ?? order.CreatedAt, order.CreatedBy,
                CheckoutSnapshot.CopyLines(legacyLines ?? order.Lines, order.ImHaus), accepted, ct, createdAt: order.CreatedAt);
            (count, secured) = await records.SecuredAsync(order.Id, ct);
        }

        // The positions with the VAT rate that applies to them (Im Haus), and
        // the receipt discount as positions (R138), so the records add up to
        // the gross amount that is paid (BMF Kassen-FAQ).
        var target = OrderBestellungDelta.WithDiscount(
            CheckoutSnapshot.CopyLines(orderLines, order.ImHaus),
            order.DiscountCents);
        var kind = count == 0
            ? OrderBestellungKind.Annahme
            : target.Count == 0 ? OrderBestellungKind.Storno : OrderBestellungKind.Aenderung;
        var delta = kind == OrderBestellungKind.Storno
            ? OrderBestellungDelta.Reverse(secured)
            : OrderBestellungDelta.Compute(kind == OrderBestellungKind.Annahme ? Array.Empty<CartLine>() : secured, target);

        // R146: DSFinV-K 4.2.3 - a position cancelled during capture is shown as
        // the captured position plus "ein zusätzlicher Positionsdatensatz …, bei dem
        // MENGE mit negiertem Vorzeichen dargestellt wird". R143 did this for
        // receipts and aborts; the positions cancelled before an order was accepted
        // or while it was changed were dropped. They belong to the record the
        // Vorgang ends in, behind its positions - secured with it in Bestellung-V1,
        // adding up to nothing, so what the records add up to is unchanged.
        var pairs = TseVorgangCartTracker.CancellationPairs(cancelledLines);

        if (delta.Count == 0)
        {
            // Nothing changed in the end: the Vorgang ends as aborted and keeps
            // what was cancelled in it (R143).
            if (!string.IsNullOrEmpty(vorgangId) && Vorgaenge is { } open)
                await open.AbortAsync(vorgangId, pairs, 0, actor, actor, ct);
            return null;
        }

        delta = delta.Concat(pairs).ToList();
        var processData = FiscalProcessData.BestellungText(delta);
        var result = Vorgaenge is { } vorgaenge
            ? await vorgaenge.FinishAsync(vorgangId ?? "", FiscalProcessData.BestellungProcessType, processData, actor, $"ORDER:{order.Id}", ct)
            : await TseKassenbelegSigner.SignAsync(_tse, _settings, processData, "Bestellung", actor, ct, FiscalProcessData.BestellungProcessType);

        var record = await records.InsertAsync(order, kind, startedAt ?? DateTimeOffset.Now, actor, delta, result, ct);

        if (kind == OrderBestellungKind.Annahme)
        {
            // The order row keeps its acceptance as before, for the order's own views.
            if (startedAt is { } started)
            {
                if (Vorgaenge is { } withStart)
                    await withStart.RecordOrderStartAsync(order.Id, started, ct);
                order.VorgangStartedAt = started;
            }

            await ApplyAsync(order, result, ct);
        }

        return record;
    }

    /// <summary>
    /// R136: the order Vorgang began with its first position; its TSE
    /// transaction is finished here with the Bestellung-V1 data, and the start
    /// is stored for BON_START.
    /// </summary>
    public async Task SignInVorgangAsync(ParkedReceipt order, string vorgangId, DateTimeOffset? startedAt, string actor, CancellationToken ct = default,
        IReadOnlyList<CartLine>? cancelledLines = null)
    {
        ArgumentNullException.ThrowIfNull(order);

        // R137: with the order records kept, the acceptance is their first record.
        // R146: with the positions cancelled before the order was accepted.
        if (Bestellungen is not null)
        {
            await SecureAsync(order, order.Lines, null, vorgangId, startedAt, actor, cancelledLines, ct);
            return;
        }

        if (string.IsNullOrEmpty(vorgangId) || Vorgaenge is not { } vorgaenge)
        {
            await SignAsync(order, actor, ct);
            return;
        }

        if (startedAt is { } started)
        {
            await vorgaenge.RecordOrderStartAsync(order.Id, started, ct);
            order.VorgangStartedAt = started;
        }

        var result = await vorgaenge.FinishAsync(
            vorgangId,
            FiscalProcessData.BestellungProcessType,
            FiscalProcessData.BestellungText(order),
            actor,
            $"ORDER:{order.Id}",
            ct);
        await ApplyAsync(order, result, ct);
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

        // R130: DSFinV-K Anhang I - StartTransaction carries neither
        // processType nor processData; both are handed over at Finish.
        var (startResult, _) = await _tse.StartTransactionAsync(
            new TseTransactionStartRequest(clientId, FiscalProcessData.StartProcessData, FiscalProcessData.StartProcessType),
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
            finishResult.LogTime,
            startResult.LogTime);

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
        order.TseStartLogTime = result.StartLogTime;
        order.TseOutage = !result.Signed;
    }
}
