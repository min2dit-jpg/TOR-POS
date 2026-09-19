using System.Diagnostics;
using TorPos.Core;

namespace TorPos.Application;

public enum CheckoutApplicationDisposition
{
    ReadyToCommit,
    NotCharged,
    Unresolved,
    FiscalBlocked
}

public sealed record CheckoutApplicationTimings(
    double? FiscalPreflightMs,
    double? JournalBeginMs,
    double? TerminalRoundtripMs);

public sealed record CheckoutApplicationResult(
    CheckoutApplicationDisposition Disposition,
    CheckoutOperation? Operation,
    PaymentTerminalPaymentResult? TerminalResult,
    FiscalReadinessReport FiscalReadiness,
    CheckoutApplicationTimings Timings);

public sealed record CheckoutReconciliationResult(
    CheckoutOperation Operation,
    bool ShouldCommit,
    FiscalReadinessReport? FiscalReadiness);

/// <summary>
/// Application-layer orchestration for production checkout.
///
/// UI decisions remain in TorPos.App. Persistence and terminal implementations
/// remain behind Core interfaces. This service can be consumed by a future
/// ViewModel without copying payment rules out of the use-case layer.
/// </summary>
public sealed class CheckoutApplicationService
{
    private readonly IFiscalComplianceService _compliance;
    private readonly ICheckoutJournal _journal;
    private readonly IPaymentTerminalService _terminal;
    private readonly IProductCatalog? _catalog;

    public CheckoutApplicationService(
        IFiscalComplianceService compliance,
        ICheckoutJournal journal,
        IPaymentTerminalService terminal,
        IProductCatalog? catalog = null)
    {
        _compliance = compliance;
        _journal = journal;
        _terminal = terminal;
        _catalog = catalog;
    }

    public async Task<CheckoutApplicationResult> PrepareProductionAsync(
        CheckoutSnapshot snapshot,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Lines.Length == 0)
            throw new InvalidOperationException("Leerer Checkout ist nicht zulässig.");

        // R151: menus stay one commercial line, while a validated hidden VAT
        // allocation is attached BEFORE the payment journal / terminal sees the
        // snapshot. Invalid menus still fail closed before any external effect.
        if (_catalog is not null)
        {
            var fiscalMenuLines = snapshot.CancelledLines is { Length: > 0 }
                ? snapshot.Lines.Concat(snapshot.CancelledLines)
                : snapshot.Lines;

            var blockedMenus = MenuVatPolicy.BlockingMenus(
                fiscalMenuLines,
                _catalog.Products,
                snapshot.ImHaus);

            if (blockedMenus.Count > 0)
            {
                var names = string.Join(", ", blockedMenus.Select(x => x.MenuName).Distinct());
                var details = string.Join(
                    " | ",
                    blockedMenus.Select(x => x.IsValid
                        ? $"{x.MenuName}: mehrere MwSt.-Sätze ({string.Join("/", x.Allocations.Select(a => a.VatRate + "%"))})"
                        : $"{x.MenuName}: {x.Message}"));

                var blockedReadiness = new FiscalReadinessReport(
                    false,
                    "TEST_ONLY",
                    "",
                    "2.4",
                    new[]
                    {
                        new FiscalReadinessItem(
                            "MENU_VAT_ALLOCATION",
                            "Menü / Combo MwSt.-Aufteilung",
                            false,
                            $"Produktivverkauf gesperrt: {names}. {details}")
                    });

                return new CheckoutApplicationResult(
                    CheckoutApplicationDisposition.FiscalBlocked,
                    Operation: null,
                    TerminalResult: null,
                    FiscalReadiness: blockedReadiness,
                    Timings: new CheckoutApplicationTimings(null, null, null));
            }

            snapshot = snapshot with
            {
                Lines = MenuVatPolicy.ApplyAllocations(
                    snapshot.Lines,
                    _catalog.Products,
                    snapshot.ImHaus),
                CancelledLines = snapshot.CancelledLines is null
                    ? null
                    : MenuVatPolicy.ApplyAllocations(
                        snapshot.CancelledLines,
                        _catalog.Products,
                        snapshot.ImHaus)
            };
        }

        var fiscalWatch = Stopwatch.StartNew();
        var readiness = await _compliance.CheckAsync(ct);
        fiscalWatch.Stop();

        if (!readiness.ProductionAllowed)
        {
            return new CheckoutApplicationResult(
                CheckoutApplicationDisposition.FiscalBlocked,
                Operation: null,
                TerminalResult: null,
                FiscalReadiness: readiness,
                Timings: new CheckoutApplicationTimings(
                    fiscalWatch.Elapsed.TotalMilliseconds,
                    null,
                    null));
        }

        var journalWatch = Stopwatch.StartNew();
        await _journal.BeginAsync(snapshot);
        journalWatch.Stop();

        var operation =
            await _journal.GetAsync(snapshot.OperationId)
            ?? throw new InvalidOperationException(
                "Zahlungsjournal fehlt nach Checkout-Start.");

        // R101: a Mixed checkout still goes through the terminal below for
        // its card portion - what decides whether the terminal is skipped
        // is "is there anything to charge", not the Method value itself.
        // (A Mixed snapshot with a zero card portion would be degenerate -
        // the UI never produces one - but is handled safely here too.)
        if (snapshot.EffectiveCardPortionCents == 0)
        {
            if (operation.State != "CASH_READY")
            {
                throw new InvalidOperationException(
                    "Bar-Checkout wurde nicht eindeutig als CASH_READY gespeichert.");
            }

            return new CheckoutApplicationResult(
                CheckoutApplicationDisposition.ReadyToCommit,
                operation,
                TerminalResult: null,
                readiness,
                new CheckoutApplicationTimings(
                    fiscalWatch.Elapsed.TotalMilliseconds,
                    journalWatch.Elapsed.TotalMilliseconds,
                    null));
        }

        var terminalWatch = Stopwatch.StartNew();

        var terminalResult =
            await _terminal.PayAsync(
                snapshot.EffectiveCardPortionCents,
                snapshot.OperationId,
                ct);

        terminalWatch.Stop();

        operation =
            await _journal.GetAsync(snapshot.OperationId)
            ?? throw new InvalidOperationException(
                "Zahlungsjournal fehlt nach Terminalaufruf.");

        // If the adapter failed before it wrote SENT, normalize that durable
        // PREPARED state to an explicit NOT_SENT/NOT_CHARGED result.
        if (!terminalResult.Success &&
            operation.State == "PREPARED")
        {
            await _journal.TransitionTerminalAsync(
                snapshot.OperationId,
                "PREPARED",
                "NOT_CHARGED",
                "Payment request not sent to terminal",
                PaymentTerminalOutcome.NotSent,
                requestSubmitted: false,
                terminalCode: terminalResult.OutcomeCode,
                terminalMessage: terminalResult.Message);

            operation =
                await _journal.GetAsync(snapshot.OperationId)
                ?? throw new InvalidOperationException(
                    "Zahlungsjournal fehlt nach NOT_SENT.");
        }

        var timings =
            new CheckoutApplicationTimings(
                fiscalWatch.Elapsed.TotalMilliseconds,
                journalWatch.Elapsed.TotalMilliseconds,
                terminalWatch.Elapsed.TotalMilliseconds);

        if (!terminalResult.Success)
        {
            if (operation.State == "NOT_CHARGED")
            {
                return new CheckoutApplicationResult(
                    CheckoutApplicationDisposition.NotCharged,
                    operation,
                    terminalResult,
                    readiness,
                    timings);
            }

            // SENT/UNKNOWN and any unexpected disagreement remain locked.
            return new CheckoutApplicationResult(
                CheckoutApplicationDisposition.Unresolved,
                operation,
                terminalResult,
                readiness,
                timings);
        }

        if (operation.State != "APPROVED" ||
            operation.TerminalOutcome != PaymentTerminalOutcome.Approved)
        {
            throw new InvalidOperationException(
                "Terminal meldet Erfolg, aber das Zahlungsjournal ist nicht eindeutig APPROVED.");
        }

        return new CheckoutApplicationResult(
            CheckoutApplicationDisposition.ReadyToCommit,
            operation,
            terminalResult,
            readiness,
            timings);
    }

    public async Task<CheckoutReconciliationResult> ReconcileAsync(
        CheckoutOperation operation,
        bool paid,
        string actor,
        string evidence,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await _journal.ResolveAsync(
            operation.Snapshot.OperationId,
            operation.State,
            paid,
            actor,
            evidence);

        var updated =
            await _journal.GetAsync(operation.Snapshot.OperationId)
            ?? throw new InvalidOperationException(
                "Zahlungsjournal fehlt nach Reconciliation.");

        if (!paid)
        {
            return new CheckoutReconciliationResult(
                updated,
                ShouldCommit: false,
                FiscalReadiness: null);
        }

        var readiness =
            await _compliance.CheckAsync(ct);

        return new CheckoutReconciliationResult(
            updated,
            ShouldCommit: readiness.ProductionAllowed,
            FiscalReadiness: readiness);
    }

    /// <summary>
    /// R102: refunds the card portion of an already-completed sale as part
    /// of a BON STORNO/Teilretoure. Unlike a new-sale checkout, there is no
    /// pending checkout_operations journal row for a Storno to update - the
    /// terminal call's own result IS the authorization the caller needs
    /// before it may record the reversal via
    /// ISaleRepository.RecordStornoAsync/RecordReturnAsync. Returns null
    /// when there is nothing to refund (a pure Cash original).
    /// </summary>
    public Task<PaymentTerminalPaymentResult?> RefundStornoCardPortionAsync(
        long cardPortionCents,
        string operationId,
        CancellationToken ct = default) =>
        cardPortionCents <= 0
            ? Task.FromResult<PaymentTerminalPaymentResult?>(null)
            : RefundCoreAsync(cardPortionCents, operationId, ct);

    private async Task<PaymentTerminalPaymentResult?> RefundCoreAsync(
        long cardPortionCents,
        string operationId,
        CancellationToken ct) =>
        await _terminal.RefundAsync(cardPortionCents, operationId, ct);
}
