namespace TorPos.Core;

public sealed record SystemIdentity(
    string EasSerial,
    string Manufacturer,
    string Model,
    DateTimeOffset CreatedAt);

public sealed record FiscalReadinessItem(
    string Code,
    string Title,
    bool Ready,
    string Detail,
    bool Mandatory = true);

public sealed record FiscalReadinessReport(
    bool ProductionAllowed,
    string Mode,
    string EasSerial,
    string DsfinvkVersion,
    IReadOnlyList<FiscalReadinessItem> Items)
{
    public int BlockingCount => Items.Count(x => x.Mandatory && !x.Ready);
}

public enum CashMovementKind
{
    Einlage,
    Entnahme,
    CashCount
}

/// <summary>
/// R134: what an Einlage or Entnahme is. AEAO zu § 146a Nr. 1.10.2 names
/// Privatentnahme, Privateinlage, Wechselgeld-Einlage, Lohnzahlung aus der Kasse
/// and Geldtransit as Geschäftsvorfälle - each has to be recorded as what it is
/// and secured by the TSE. The names are the DSFinV-K GV_TYP values (Anhang C).
/// </summary>
public enum CashBusinessCase
{
    /// <summary>Cash to or from the bank, the safe or another till - also the change float put in.</summary>
    Geldtransit,
    Privateinlage,
    Privatentnahme,
    Lohnzahlung,
    /// <summary>Any other inflow that none of the types above describes.</summary>
    Einzahlung,
    /// <summary>Any other outflow, e.g. a small purchase paid from the till.</summary>
    Auszahlung,

    /// <summary>
    /// R139: the difference between the calculated and the counted cash found at
    /// a Kassensturz (DSFinV-K Anhang C: "Differenzen können so festgestellt,
    /// protokolliert und ausgeglichen werden"). A surplus is booked as an
    /// Einlage, a shortfall as an Entnahme. Only a Kassensturz books it.
    /// </summary>
    DifferenzSollIst
}

public static class CashBusinessCases
{
    public static IReadOnlyList<CashBusinessCase> For(CashMovementKind kind) => kind switch
    {
        CashMovementKind.Einlage => new[] { CashBusinessCase.Geldtransit, CashBusinessCase.Privateinlage, CashBusinessCase.Einzahlung },
        CashMovementKind.Entnahme => new[] { CashBusinessCase.Geldtransit, CashBusinessCase.Privatentnahme, CashBusinessCase.Lohnzahlung, CashBusinessCase.Auszahlung },
        _ => Array.Empty<CashBusinessCase>()
    };

    public static string Label(CashBusinessCase businessCase, CashMovementKind kind) => businessCase switch
    {
        CashBusinessCase.Geldtransit => kind == CashMovementKind.Einlage
            ? "Geldtransit (Wechselgeld / aus Bank oder Tresor)"
            : "Geldtransit (zur Bank oder in den Tresor)",
        CashBusinessCase.Privateinlage => "Privateinlage",
        CashBusinessCase.Privatentnahme => "Privatentnahme",
        CashBusinessCase.Lohnzahlung => "Lohnzahlung aus der Kasse",
        CashBusinessCase.Einzahlung => "Sonstige Einzahlung",
        CashBusinessCase.Auszahlung => "Sonstige Auszahlung (ohne USt)",
        CashBusinessCase.DifferenzSollIst => kind == CashMovementKind.Einlage
            ? "Kassendifferenz (Überschuss beim Kassensturz)"
            : "Kassendifferenz (Fehlbetrag beim Kassensturz)",
        _ => businessCase.ToString()
    };

    /// <summary>
    /// What a movement of this kind may be. A Kassendifferenz fits both
    /// directions but is not offered for manual entry (<see cref="For"/>).
    /// </summary>
    public static bool Allowed(CashMovementKind kind, CashBusinessCase businessCase) =>
        For(kind).Contains(businessCase) ||
        (businessCase == CashBusinessCase.DifferenzSollIst && kind is CashMovementKind.Einlage or CashMovementKind.Entnahme);
}

/// <summary>
/// R139: the calculated cash in the drawer. DSFinV-K counts cash without a break
/// at a closing: the Anfangsbestand plus every cash flow since - money taken to
/// the bank is a Geldtransit, not a reset. The origin is the last confirmed
/// Kassensturz (<paramref name="CountedAt"/>, whose counted amount is
/// <paramref name="BaseCents"/>), or the initial cash of the till before the
/// first one.
/// </summary>
public sealed record CashBalance(long ExpectedCents, DateTimeOffset? CountedAt, long BaseCents);

/// <summary>R139: a confirmed Kassensturz and the difference it booked, if any.</summary>
public sealed record CashCountBooking(long ExpectedCents, long CountedCents, CashMovement? Difference, CashMovement Count)
{
    public long DifferenceCents => CountedCents - ExpectedCents;
}

/// <summary>
/// R134: <paramref name="Production"/> is set by the till only when a real
/// (non-simulation) booking is allowed; such a movement is a fiscal Vorgang
/// and is signed by the TSE. Everything else stays a test entry.
/// </summary>
public sealed record CashMovementRequest(
    CashMovementKind Kind,
    long AmountCents,
    string Reason,
    CashBusinessCase? BusinessCase = null,
    bool Production = false);

public sealed record CashMovement(
    long Id,
    DateTimeOffset CreatedAt,
    CashMovementKind Kind,
    long AmountCents,
    string Reason,
    string Actor,
    string FiscalMode,
    CashBusinessCase? BusinessCase = null)
{
    public const string ProductionMode = "PRODUCTION";
    public const string TestMode = "TEST_ONLY";

    /// <summary>Signed amount: an Einlage adds to the drawer, an Entnahme takes out.</summary>
    public long SignedCents => Kind == CashMovementKind.Entnahme ? -AmountCents : AmountCents;
}

public interface ISystemIdentityRepository
{
    Task<SystemIdentity> GetAsync(CancellationToken ct = default);
}

public interface IFiscalComplianceService
{
    Task<FiscalReadinessReport> CheckAsync(CancellationToken ct = default);
}

public interface IAuditLog
{
    Task WriteAsync(
        string actor,
        string eventType,
        string entityType,
        string entityId,
        string details,
        CancellationToken ct = default);

    Task<string> ExportCsvAsync(
        string targetPath,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default);
}

public sealed record TseOutage(
    long Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string Reason,
    string Actor,
    string State);

public interface ITseOutageRepository
{
    Task<TseOutage?> GetOpenAsync(
        CancellationToken ct = default);

    Task<TseOutage> OpenAsync(
        string reason,
        string actor = "SYSTEM",
        CancellationToken ct = default);

    Task CloseOpenAsync(
        string actor = "SYSTEM",
        CancellationToken ct = default);

    /// <summary>
    /// The outage history, newest first. § 146a AO expects a TSE failure to be
    /// documented; until now the record existed only in the database and in the
    /// DSFinV-K export, so nobody at the till could answer "when was it down
    /// and why" without an export or a database tool.
    /// </summary>
    Task<IReadOnlyList<TseOutage>> ListRecentAsync(
        int limit = 50,
        CancellationToken ct = default);
}

public sealed record DsfinvkPreflightIssue(
    string Code,
    string Message,
    bool Blocking = true);

public sealed record DsfinvkPreflightReport(
    bool Ready,
    string Version,
    IReadOnlyList<DsfinvkPreflightIssue> Issues);

public interface IDsfinvkExportService
{
    Task<DsfinvkPreflightReport> ValidateAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default);

    Task<string> ExportAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        string targetDirectory,
        CancellationToken ct = default);
}

public interface ICashMovementRepository
{
    Task<CashMovement> AddAsync(
        CashMovementRequest request,
        string actor,
        CancellationToken ct = default);

    Task<long> GetExpectedCashCentsAsync(
        long openingBalanceCents,
        CancellationToken ct = default);

    /// <summary>R139: the calculated cash since the last confirmed Kassensturz (see <see cref="CashBalance"/>).</summary>
    Task<CashBalance> GetCashBalanceAsync(
        long initialBalanceCents,
        bool production,
        CancellationToken ct = default);

    /// <summary>
    /// R139: records a confirmed Kassensturz. A difference to the calculated
    /// cash is booked as DifferenzSollIst in the same step, so the counted
    /// amount becomes the new origin.
    /// </summary>
    Task<CashCountBooking> BookCashCountAsync(
        long countedCents,
        long initialBalanceCents,
        string note,
        bool production,
        string actor,
        CancellationToken ct = default);

    /// <summary>R134: the TSE result of a production cash movement, written once.</summary>
    Task RecordTseResultAsync(
        long movementId,
        SaleTseResult result,
        CancellationToken ct = default);
}
