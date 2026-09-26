namespace TorPos.Core;

/// <summary>
/// Which German fiscal rule set a behaviour is decided by. Fiscal obligations
/// are read from one <see cref="FiscalComplianceProfile"/> instead of growing
/// as scattered if-blocks, so a change in the law is a new profile, not a
/// rewrite of the checkout.
/// </summary>
public enum GermanFiscalRulesetVersion
{
    /// <summary>The law in force today (AO § 146a, KassenSichV, AEAO zu § 146a).</summary>
    Current,

    /// <summary>
    /// "Zweites Kassengesetz" as decided by the Federal Cabinet on 23.09.2026
    /// (Regierungsentwurf, not enacted). Preview only - never the production
    /// default until the Bundestag/Bundesrat text is final and promulgated.
    /// </summary>
    Planned2028
}

/// <summary>
/// The fiscal obligations one rule set imposes. A property here is an
/// obligation of the law, not a product feature switch: whether TOR may use a
/// feature at all is still decided by <see cref="FiscalRelease"/> and the
/// provider release gates.
/// </summary>
public sealed record FiscalComplianceProfile(
    GermanFiscalRulesetVersion Version,
    string Country,
    DateOnly EffectiveFrom,
    bool Enacted,
    bool TseRequired,
    bool DsfinvkRequired,
    bool PaperReceiptRequired,
    bool DigitalReceiptRequired,
    bool PaperReceiptOnRequest,
    bool RegisterNotificationRequired,
    bool TseChangeNotificationRequired,
    string LegalBasis,
    string VerifiedStatus);

public static class GermanFiscalRulesets
{
    /// <summary>
    /// Set to true only in a reviewed release once the Zweites Kassengesetz is
    /// promulgated in the Bundesgesetzblatt AND its final text has been checked
    /// against <see cref="Planned2028"/>. Until then the planned profile can be
    /// previewed but never resolved for a real till.
    /// </summary>
    public const bool Planned2028Enacted = false;

    public static readonly FiscalComplianceProfile Current = new(
        GermanFiscalRulesetVersion.Current,
        Country: "DE",
        EffectiveFrom: new DateOnly(2020, 1, 1),
        Enacted: true,
        TseRequired: true,
        DsfinvkRequired: true,
        // § 146a Abs. 2 AO: a receipt must be issued; AEAO zu § 146a:
        // electronic provision is allowed if the customer agrees. Paper is
        // therefore the safe default whenever no digital channel is accepted.
        PaperReceiptRequired: true,
        DigitalReceiptRequired: false,
        PaperReceiptOnRequest: false,
        // § 146a Abs. 4 AO: Mitteilung über ELSTER since 01.01.2025 (BMF
        // 28.06.2024). TOR prepares the data; the operator submits it.
        RegisterNotificationRequired: true,
        // A separate TSE-change notification is not an obligation of its own
        // under the current text; the draft law would widen it.
        TseChangeNotificationRequired: false,
        LegalBasis: "AO § 146a, KassenSichV, AEAO zu § 146a, DSFinV-K 2.4, BMF-Schreiben 28.06.2024 (Mitteilungspflicht)",
        VerifiedStatus: "In Kraft.");

    public static readonly FiscalComplianceProfile Planned2028 = new(
        GermanFiscalRulesetVersion.Planned2028,
        Country: "DE",
        EffectiveFrom: new DateOnly(2028, 1, 1),
        Enacted: Planned2028Enacted,
        TseRequired: true,
        DsfinvkRequired: true,
        PaperReceiptRequired: false,
        DigitalReceiptRequired: true,
        PaperReceiptOnRequest: true,
        RegisterNotificationRequired: true,
        TseChangeNotificationRequired: true,
        LegalBasis: "Regierungsentwurf \"Zweites Kassengesetz\" (Kabinettsbeschluss 23.09.2026): Kassenpflicht ab 100.000 EUR Umsatz, elektronische Belegbereitstellung (z. B. QR/NFC) ab 01.01.2028",
        VerifiedStatus: "ENTWURF - nicht verkündet. Vor Aktivierung Bundestag/Bundesrat-Endfassung und BGBl. prüfen.");

    public static IReadOnlyList<FiscalComplianceProfile> All { get; } = new[] { Current, Planned2028 };

    /// <summary>
    /// The profile that decides behaviour on a real till at <paramref name="today"/>.
    /// An unenacted profile is never returned, whatever the date or setting.
    /// </summary>
    public static FiscalComplianceProfile Resolve(DateOnly today) =>
        Planned2028.Enacted && today >= Planned2028.EffectiveFrom
            ? Planned2028
            : Current;

    /// <summary>
    /// A profile for preview screens and tests. It must not reach a fiscal
    /// decision: <see cref="RequireEnacted"/> refuses it.
    /// </summary>
    public static FiscalComplianceProfile Preview(GermanFiscalRulesetVersion version) => version switch
    {
        GermanFiscalRulesetVersion.Current => Current,
        GermanFiscalRulesetVersion.Planned2028 => Planned2028,
        _ => throw new ArgumentOutOfRangeException(nameof(version))
    };

    /// <summary>Fail-closed guard for code that turns a profile into behaviour.</summary>
    public static FiscalComplianceProfile RequireEnacted(FiscalComplianceProfile profile)
    {
        if (!profile.Enacted)
            throw new InvalidOperationException(
                $"Regelwerk {profile.Version} ist nicht in Kraft ({profile.VerifiedStatus}) und darf das fiskalische Verhalten nicht bestimmen.");
        return profile;
    }
}
