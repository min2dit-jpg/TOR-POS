namespace TorPos.Core;

// A build-level gate. A software license never grants fiscal readiness.
//
// Each flag represents an acceptance EVIDENCE decision, not whether code merely
// exists. They stay false until the corresponding verification artifact has
// been reviewed. Production can only open when the complete qualification set
// is true; flipping one historical "release" boolean can no longer bypass the
// other KassenSichV/DSFinV-K/hardware gates.
public static class FiscalRelease
{
    public const bool DsfinvkValidated = false;
    public const bool KassenSichVReceiptValidated = false;
    public const bool ParkedOrderTseValidated = false;
    public const bool PfandTaxValidated = false;
    public const bool PhysicalTseE2EValidated = false;
    public const bool IndependentFiscalReviewValidated = false;

    public static bool Enabled =>
        DsfinvkValidated &&
        KassenSichVReceiptValidated &&
        ParkedOrderTseValidated &&
        PfandTaxValidated &&
        PhysicalTseE2EValidated &&
        IndependentFiscalReviewValidated;

    public static IReadOnlyList<string> MissingQualifications()
    {
        var missing = new List<string>();
        if (!DsfinvkValidated) missing.Add("DSFinV-K-Prüfdatensatz");
        if (!KassenSichVReceiptValidated) missing.Add("§6-Beleg/QR");
        if (!ParkedOrderTseValidated) missing.Add("Bestellung/Parken-TSE");
        if (!PfandTaxValidated) missing.Add("Pfand-Steuerlogik");
        if (!PhysicalTseE2EValidated) missing.Add("physische TSE-E2E-Abnahme");
        if (!IndependentFiscalReviewValidated) missing.Add("unabhängige Fiskalprüfung");
        return missing;
    }

    public static void RequireProduction()
    {
        if (!Enabled)
            throw new InvalidOperationException(
                "Produktivbuchung gesperrt. Fehlende Freigaben: " +
                string.Join(", ", MissingQualifications()) +
                ". TRAINING verwenden.");
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

    /// <summary>
    /// R142: whether this till secures the Vorgänge of the current user - order
    /// acceptance, change and cancellation included. A regular user on a till
    /// that books for real; a training user when training is recorded
    /// (DSFinV-K Anhang B, AVTraining: "Alle Handlungen des Trainingsmodus
    /// müssen dokumentiert, gesondert gekennzeichnet und mittels der DSFinV-K
    /// abgebildet werden"). A test till secures nothing.
    /// </summary>
    public static bool SecuresVorgaenge(bool isTraining, bool recordsTrainingFiscally, bool canCommitProductionSale) =>
        isTraining ? recordsTrainingFiscally : canCommitProductionSale;
}

/// <param name="TseVorgangId">R136: the TSE Vorgang started with the first position (empty when none was started).</param>
/// <param name="StartedAt">R136: when that Vorgang began at the till.</param>
/// <param name="CancelledLines">R143: positions cancelled during capture (DSFinV-K 4.2.3).</param>
public sealed record CheckoutSnapshot(
    string OperationId, CartLine[] Lines, long DiscountCents,
    PaymentMethod Method, string OperatorName, long? ParkedReceiptId,
    bool ImHaus = false, long CashPortionCents = 0,
    string TseVorgangId = "", DateTimeOffset? StartedAt = null,
    CartLine[]? CancelledLines = null)
{
    public long TotalCents => ReceiptTotals.Total(Lines.Sum(x => x.LineTotalCents), DiscountCents);

    // R101: the single source of truth for how much of this sale is cash
    // vs. card, valid for ALL three PaymentMethod values - callers should
    // always read these two instead of branching on Method themselves.
    // CashPortionCents (the constructor parameter) only means anything when
    // Method==Mixed; for Cash/Card the split is implied by Method and any
    // stray value passed there is ignored.
    public long EffectiveCashPortionCents => Method switch
    {
        PaymentMethod.Cash => TotalCents,
        // R149: a payout (negative total) is only ever cash.
        PaymentMethod.Mixed => TotalCents <= 0 ? TotalCents : Math.Clamp(CashPortionCents, 0, TotalCents),
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
