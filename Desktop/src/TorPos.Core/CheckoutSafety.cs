namespace TorPos.Core;

// A build-level gate. A software license never grants fiscal readiness.
public static class FiscalRelease
{
    public static bool Enabled => false;
    public static void RequireProduction()
    {
        if (!Enabled)
            throw new InvalidOperationException("Produktivbuchung gesperrt: TSE, DSFinV-K und Belegfreigabe fehlen. TRAINING verwenden.");
    }
}

/// <summary>
/// R116: the single rule for "may this till book a real, fiscal sale?".
/// Anything that is not a production sale runs as a clearly marked
/// simulation - TEST receipt, no fiscal booking, nothing written to the
/// sales journal.
///
/// The rule used to be inverted in MainWindow: an UNLICENSED till was waved
/// through as a "development test mode" while a LICENSED one was refused
/// outright with "KASSIEREN GESPERRT" whenever <see cref="FiscalRelease"/>
/// was off - which it always is today. A paying customer therefore could not
/// even try their own register, while an unlicensed install could. Selling
/// for real still requires the licence; what changed is that the honest
/// fallback is simulation, not refusal.
/// </summary>
public static class SaleModePolicy
{
    public static bool CanCommitProductionSale(
        bool isTraining,
        bool licenseActive,
        bool fiscalReleaseEnabled,
        bool productionAllowed) =>
        !isTraining && licenseActive && fiscalReleaseEnabled && productionAllowed;

    public static bool IsSimulation(
        bool isTraining,
        bool licenseActive,
        bool fiscalReleaseEnabled,
        bool productionAllowed) =>
        !CanCommitProductionSale(isTraining, licenseActive, fiscalReleaseEnabled, productionAllowed);

    /// <summary>
    /// R135: AEAO zu § 146a Nr. 1.11.1 counts Trainingsbuchungen among the
    /// Vorgänge to be secured, and DSFinV-K 4.2.6 / Anhang B wants them
    /// recorded as AVTraining. That applies to a till that books for real: a
    /// training sale there is recorded and signed, without any effect on the
    /// closing. A till that is not released for real bookings records nothing
    /// fiscal at all, training included.
    /// </summary>
    public static bool RecordsTrainingFiscally(
        bool isTraining,
        bool licenseActive,
        bool fiscalReleaseEnabled,
        bool productionAllowed) =>
        isTraining && licenseActive && fiscalReleaseEnabled && productionAllowed;
}

/// <param name="TseVorgangId">R136: the TSE Vorgang started with the first position (empty when none was started).</param>
/// <param name="StartedAt">R136: when that Vorgang began at the till.</param>
public sealed record CheckoutSnapshot(
    string OperationId, CartLine[] Lines, long DiscountCents,
    PaymentMethod Method, string OperatorName, long? ParkedReceiptId,
    bool ImHaus = false, long CashPortionCents = 0,
    string TseVorgangId = "", DateTimeOffset? StartedAt = null)
{
    public long TotalCents => Math.Max(0, Lines.Sum(x => x.LineTotalCents) - DiscountCents);

    // R101: the single source of truth for how much of this sale is cash
    // vs. card, valid for ALL three PaymentMethod values - callers should
    // always read these two instead of branching on Method themselves.
    // CashPortionCents (the constructor parameter) only means anything when
    // Method==Mixed; for Cash/Card the split is implied by Method and any
    // stray value passed there is ignored.
    public long EffectiveCashPortionCents => Method switch
    {
        PaymentMethod.Cash => TotalCents,
        PaymentMethod.Mixed => Math.Clamp(CashPortionCents, 0, TotalCents),
        _ => 0
    };
    public long EffectiveCardPortionCents => TotalCents - EffectiveCashPortionCents;

    // imHaus applies the Im-Haus/Außer-Haus VAT swap (ImHausVat.Effective)
    // once, right here, while copying the live cart into an immutable
    // snapshot - everything downstream (receipt MwSt breakdown, tax
    // reports, TSE ProcessData VAT-class bucketing) already groups
    // generically by CartLine.VatRate/sale_items.vat_rate, so nothing else
    // needs to know this rule exists.
    public static CartLine[] CopyLines(IEnumerable<CartLine> lines, bool imHaus = false) => lines.Select(x => new CartLine
    {
        ProductId=x.ProductId, ProductName=x.ProductName, VariantName=x.VariantName,
        Barcode=x.Barcode, Quantity=x.Quantity, UnitPriceCents=x.UnitPriceCents,
        ListUnitPriceCents=x.EffectiveListUnitPriceCents,
        VatRate=ImHausVat.Effective(x.VatRate, imHaus, x.ImHausApplicable), PfandCents=x.PfandCents,
        ImHausApplicable=x.ImHausApplicable,
        PromotionId=x.PromotionId, PromotionName=x.PromotionName,
        PromotionPercent=x.PromotionPercent,
        PromotionDiscountUnitCents=x.PromotionDiscountUnitCents,
        PromotionStartDate=x.PromotionStartDate,
        PromotionEndDate=x.PromotionEndDate
    }).ToArray();
}
public sealed record CheckoutOperation(
    CheckoutSnapshot Snapshot,
    string State,
    string Evidence,
    long? SaleId,
    PaymentTerminalOutcome TerminalOutcome = PaymentTerminalOutcome.None,
    string TerminalCode = "",
    string TerminalMessage = "",
    bool TerminalRequestSubmitted = false,
    CheckoutResolution Resolution = CheckoutResolution.None,
    string ResolutionActor = "",
    string ResolutionAt = "");

/// <summary>
/// Durable checkout journal boundary used by Application and device adapters.
/// Infrastructure owns the SQLite implementation; higher layers only know
/// this contract.
/// </summary>
public interface ICheckoutJournal
{
    Task BeginAsync(CheckoutSnapshot snapshot);

    Task MarkTerminalSubmittedAsync(
        string id,
        string evidence);

    Task TransitionTerminalAsync(
        string id,
        string expected,
        string state,
        string evidence,
        PaymentTerminalOutcome outcome,
        bool requestSubmitted,
        string terminalCode,
        string terminalMessage);

    Task ResolveAsync(
        string id,
        string expected,
        bool paid,
        string actor,
        string evidence);

    Task<IReadOnlyList<CheckoutOperation>> GetOpenAsync();

    Task<CheckoutOperation?> GetAsync(string id);

    Task<long?> FindSaleAsync(string id);
}
