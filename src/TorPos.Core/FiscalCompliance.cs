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

public sealed record CashMovementRequest(
    CashMovementKind Kind,
    long AmountCents,
    string Reason);

public sealed record CashMovement(
    long Id,
    DateTimeOffset CreatedAt,
    CashMovementKind Kind,
    long AmountCents,
    string Reason,
    string Actor,
    string FiscalMode);

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
}
