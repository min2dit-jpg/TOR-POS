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
    Auszahlung
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
        _ => businessCase.ToString()
    };

    public static bool Allowed(CashMovementKind kind, CashBusinessCase businessCase) => For(kind).Contains(businessCase);
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

    /// <summary>R134: the TSE result of a production cash movement, written once.</summary>
    Task RecordTseResultAsync(
        long movementId,
        SaleTseResult result,
        CancellationToken ct = default);
}
