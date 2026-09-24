namespace TorPos.Core;

public enum PhysicalTseGeneration
{
    Unknown,
    Generation1,
    Generation1_1,
    Generation2
}

// A build-level gate. A software license never grants fiscal readiness.
// Every flag is acceptance EVIDENCE, not merely "code exists". Production
// opens only when the qualification set for the ACTUALLY USED TSE path has
// been reviewed. Evidence for one hardware generation must never unlock
// another generation.
public static class FiscalRelease
{
    public const bool DsfinvkValidated = false;
    public const bool KassenSichVReceiptValidated = false;
    public const bool ParkedOrderTseValidated = false;
    public const bool PfandTaxValidated = false;

    public const bool PhysicalTseGeneration1E2EValidated = false;
    public const bool PhysicalTseGeneration11E2EValidated = false;
    public const bool PhysicalTseGeneration2E2EValidated = false;

    public const bool IndependentFiscalReviewValidated = false;

    public static bool CommonQualificationsValidated =>
        DsfinvkValidated &&
        KassenSichVReceiptValidated &&
        ParkedOrderTseValidated &&
        PfandTaxValidated &&
        IndependentFiscalReviewValidated;

    // Compatibility/overview only: all supported physical generations have
    // evidence. Runtime production code uses EnabledForProvider(), which binds
    // the release to the device that is actually connected.
    public static bool PhysicalTseE2EValidated =>
        PhysicalTseGeneration1E2EValidated &&
        PhysicalTseGeneration11E2EValidated &&
        PhysicalTseGeneration2E2EValidated;

    // Deliberately stricter than the runtime selector so legacy callers can
    // never become fail-open after the split.
    public static bool Enabled =>
        CommonQualificationsValidated &&
        PhysicalTseE2EValidated;

    public static PhysicalTseGeneration DetectPhysicalTseGeneration(
        TseDeviceInfo? device)
    {
        if (device is null ||
            !string.Equals(
                device.Manufacturer?.Trim(),
                "Swissbit",
                StringComparison.OrdinalIgnoreCase))
        {
            return PhysicalTseGeneration.Unknown;
        }

        // Generation evidence is accepted only from the explicitly normalized
        // device.Generation field. Vendor descriptions and hardware/software
        // revisions are not parsed here. Until an adapter has a documented
        // real-device mapping, it must leave Generation empty and we fail closed.
        return ParsePhysicalGeneration(device.Generation);
    }

    public static bool SelectPhysicalTseEvidence(
        PhysicalTseGeneration generation,
        bool generation1Validated,
        bool generation11Validated,
        bool generation2Validated) =>
        generation switch
        {
            PhysicalTseGeneration.Generation1 =>
                generation1Validated,
            PhysicalTseGeneration.Generation1_1 =>
                generation11Validated,
            PhysicalTseGeneration.Generation2 =>
                generation2Validated,
            _ => false
        };

    public static bool IsPhysicalTseValidated(TseDeviceInfo? device) =>
        SelectPhysicalTseEvidence(
            DetectPhysicalTseGeneration(device),
            PhysicalTseGeneration1E2EValidated,
            PhysicalTseGeneration11E2EValidated,
            PhysicalTseGeneration2E2EValidated);

    public static bool EnabledForProvider(
        string providerId,
        TseDeviceInfo? device)
    {
        TseProviderDescriptor provider;
        try
        {
            provider = TseProviderCatalog.Get(providerId);
        }
        catch
        {
            return false;
        }

        var providerValidated = provider.Transport switch
        {
            TseProviderTransport.DirectCloudApi =>
                TseProviderCatalog.IsProviderReleaseValidated(providerId),

            // Local middleware is not automatically "cloud". It may pass the
            // physical gate only if the probed device unambiguously identifies
            // a qualified Swissbit generation.
            TseProviderTransport.HardwareSdk or
            TseProviderTransport.LocalMiddleware =>
                IsPhysicalTseValidated(device),

            _ => false
        };

        return CommonQualificationsValidated &&
               providerValidated;
    }

    public static IReadOnlyList<string> MissingQualifications()
    {
        var missing = CommonMissingQualifications();
        if (!PhysicalTseGeneration1E2EValidated) missing.Add("physische TSE Gen 1 E2E-Abnahme");
        if (!PhysicalTseGeneration11E2EValidated) missing.Add("physische TSE Gen 1.1 E2E-Abnahme");
        if (!PhysicalTseGeneration2E2EValidated) missing.Add("physische TSE Gen 2 E2E-Abnahme");
        return missing;
    }

    public static IReadOnlyList<string> MissingQualificationsForProvider(
        string providerId,
        TseDeviceInfo? device)
    {
        var missing = CommonMissingQualifications();

        TseProviderDescriptor provider;
        try
        {
            provider = TseProviderCatalog.Get(providerId);
        }
        catch
        {
            missing.Add("unbekannter TSE-Provider");
            return missing;
        }

        if (provider.Transport == TseProviderTransport.DirectCloudApi)
        {
            if (!TseProviderCatalog.IsProviderReleaseValidated(providerId))
            {
                var vendor =
                    TseProviderCatalog.CloudVendorForProvider(providerId)
                    ?? provider.ProviderId;

                missing.Add(
                    $"Cloud-TSE-E2E-Abnahme ({vendor})");
            }

            return missing;
        }

        var generation = DetectPhysicalTseGeneration(device);
        switch (generation)
        {
            case PhysicalTseGeneration.Generation1:
                if (!PhysicalTseGeneration1E2EValidated)
                    missing.Add("physische TSE Gen 1 E2E-Abnahme");
                break;
            case PhysicalTseGeneration.Generation1_1:
                if (!PhysicalTseGeneration11E2EValidated)
                    missing.Add("physische TSE Gen 1.1 E2E-Abnahme");
                break;
            case PhysicalTseGeneration.Generation2:
                if (!PhysicalTseGeneration2E2EValidated)
                    missing.Add("physische TSE Gen 2 E2E-Abnahme");
                break;
            default:
                missing.Add("TSE-Generation nicht eindeutig erkannt/qualifiziert");
                break;
        }

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


    private static List<string> CommonMissingQualifications()
    {
        var missing = new List<string>();
        if (!DsfinvkValidated) missing.Add("DSFinV-K-Prüfdatensatz");
        if (!KassenSichVReceiptValidated) missing.Add("§6-Beleg/QR");
        if (!ParkedOrderTseValidated) missing.Add("Bestellung/Parken-TSE");
        if (!PfandTaxValidated) missing.Add("Pfand-Steuerlogik");
        if (!IndependentFiscalReviewValidated) missing.Add("unabhängige Fiskalprüfung");
        return missing;
    }

    private static PhysicalTseGeneration ParsePhysicalGeneration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return PhysicalTseGeneration.Unknown;

        // Only TOR's canonical, adapter-normalized labels are accepted.
        // Do not infer a generation from unverified vendor strings such as
        // descriptions or version numbers. Real Swissbit hardware must first
        // be observed and its documented discriminator mapped by the adapter.
        var normalized = value.Trim();

        if (string.Equals(
                normalized,
                nameof(PhysicalTseGeneration.Generation1),
                StringComparison.OrdinalIgnoreCase))
            return PhysicalTseGeneration.Generation1;

        if (string.Equals(
                normalized,
                nameof(PhysicalTseGeneration.Generation1_1),
                StringComparison.OrdinalIgnoreCase))
            return PhysicalTseGeneration.Generation1_1;

        if (string.Equals(
                normalized,
                nameof(PhysicalTseGeneration.Generation2),
                StringComparison.OrdinalIgnoreCase))
            return PhysicalTseGeneration.Generation2;

        return PhysicalTseGeneration.Unknown;
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

    // imHaus applies the date-aware Im-Haus/Außer-Haus VAT policy
    // (ImHausVat.Effective) once, right here, while copying the live cart
    // into an immutable snapshot. In 2024/2025 the legacy 19% elevation is
    // reproduced; from 01.01.2026 food stays reduced. Everything downstream
    // groups generically by the snapshotted VatRate.
    public static CartLine[] CopyLines(IEnumerable<CartLine> lines, bool imHaus = false) =>
        lines.Select(x => new CartLine(x)
        {
            // A checkout snapshot is a new commercial line identity but keeps
            // any already persisted gross-total snapshot (e.g. recalled park).
            SaleItemId = 0,
            ListUnitPriceCents = x.EffectiveListUnitPriceCents,
            VatRate = ImHausVat.Effective(
                x.VatRate,
                imHaus,
                x.ImHausApplicable)
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
