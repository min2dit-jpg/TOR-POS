using System.Text.Json;

namespace TorPos.Core;

public enum TseKind
{
    /// <summary>Hardware TSE (USB/microSD) driven through the vendor SDK.</summary>
    Hardware,
    Cloud,
    LocalMiddleware,
    Other
}

/// <summary>
/// One TSE as the till knows it. Certificate data is the public part only;
/// PINs, PUK, credential seed and API keys never belong here.
/// </summary>
public sealed record TseIdentity(
    string ProviderId,
    string SerialNumber,
    string BsiCertificationId,
    string CertificateValidUntil,
    string DevicePath)
{
    public static TseIdentity Empty { get; } = new("", "", "", "", "");

    public bool IsEmpty => SerialNumber.Length == 0 && ProviderId.Length == 0;

    public TseKind Kind => TseChangeDetector.KindOf(ProviderId);

    /// <summary>The TSE as configured in the settings (keys tse.*).</summary>
    public static TseIdentity FromSettings(IReadOnlyDictionary<string, string> settings)
    {
        string Get(string key) => settings.TryGetValue(key, out var value) ? (value ?? "").Trim() : "";
        return new TseIdentity(
            Get("tse.provider"),
            Get("tse.serial"),
            Get("tse.bsi_id"),
            Get("tse.expiry_date"),
            Get("tse.device_path"));
    }
}

public enum TseChangeOutcome
{
    Succeeded,
    Failed
}

/// <summary>
/// A TSE-Wechsel as recorded in the append-only audit log. It documents the
/// change only: signed sales, their TSE data and the DSFinV-K records of the
/// old TSE are never rewritten or moved to the new TSE.
/// </summary>
public sealed record TseChangeRecord(
    string ChangeId,
    DateTimeOffset OccurredAt,
    TseIdentity Previous,
    TseIdentity Next,
    string Reason,
    string Actor,
    string KassenId,
    string TerminalId,
    string ClientId,
    string Mandant,
    TseChangeOutcome Outcome,
    string ErrorCode = "",
    string ErrorMessage = "")
{
    public const string AuditEventType = "TSE_WECHSEL";
    public const string AuditEntityType = "TSE_CHANGE";

    public TseKind PreviousKind => Previous.Kind;
    public TseKind NextKind => Next.Kind;
    public bool InitialSetup => Previous.SerialNumber.Length == 0;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static TseChangeRecord FromJson(string json) =>
        JsonSerializer.Deserialize<TseChangeRecord>(json, Json)
        ?? throw new InvalidOperationException("TSE-Wechsel-Eintrag ist leer.");
}

public static class TseChangeDetector
{
    public static TseKind KindOf(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return TseKind.Other;
        try
        {
            return TseProviderCatalog.Get(providerId).Transport switch
            {
                TseProviderTransport.HardwareSdk => TseKind.Hardware,
                TseProviderTransport.DirectCloudApi => TseKind.Cloud,
                TseProviderTransport.LocalMiddleware => TseKind.LocalMiddleware,
                _ => TseKind.Other
            };
        }
        catch (InvalidOperationException)
        {
            return TseKind.Other;
        }
    }

    /// <summary>
    /// A change is a different TSE serial or a different provider. Reading the
    /// same TSE again (probe, re-activation, expiry date refreshed) is not a
    /// change; a serial that disappears is not a new TSE either.
    /// </summary>
    public static bool IsChange(TseIdentity before, TseIdentity after)
    {
        var serialBefore = Normalize(before.SerialNumber);
        var serialAfter = Normalize(after.SerialNumber);
        if (serialAfter.Length == 0)
            return false;
        return !string.Equals(serialBefore, serialAfter, StringComparison.Ordinal) ||
               !string.Equals(before.ProviderId.Trim(), after.ProviderId.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string serial) =>
        new string((serial ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
}

/// <summary>
/// State of a (future) notification of a TSE change to the tax office. The
/// states are an append-only history per change; the current state is the last.
/// </summary>
public enum TseChangeNotificationStatus
{
    NotRequired,
    Pending,
    ReadyForSubmission,
    Submitted,
    Failed,
    Superseded
}

public sealed record TseChangeNotificationRecord(
    string ChangeId,
    TseChangeNotificationStatus Status,
    DateTimeOffset At,
    string Actor,
    string Note = "",
    string SubmissionReference = "")
{
    public const string AuditEventType = "TSE_WECHSEL_MELDUNG";
}

public static class TseChangeNotificationRules
{
    /// <summary>
    /// The first state. Decided by the rule set in force, never by a draft:
    /// under <see cref="GermanFiscalRulesets.Current"/> no separate TSE-change
    /// notification is due, so the record starts as NotRequired and only keeps
    /// the data ready.
    /// </summary>
    public static TseChangeNotificationStatus InitialFor(FiscalComplianceProfile profile, TseChangeOutcome outcome)
    {
        if (outcome != TseChangeOutcome.Succeeded)
            return TseChangeNotificationStatus.NotRequired;
        return profile.Enacted && profile.TseChangeNotificationRequired
            ? TseChangeNotificationStatus.Pending
            : TseChangeNotificationStatus.NotRequired;
    }

    public static bool CanTransition(TseChangeNotificationStatus from, TseChangeNotificationStatus to) => (from, to) switch
    {
        (TseChangeNotificationStatus.NotRequired, TseChangeNotificationStatus.Pending) => true,
        (TseChangeNotificationStatus.NotRequired, TseChangeNotificationStatus.Superseded) => true,
        (TseChangeNotificationStatus.Pending, TseChangeNotificationStatus.ReadyForSubmission) => true,
        (TseChangeNotificationStatus.Pending, TseChangeNotificationStatus.NotRequired) => true,
        (TseChangeNotificationStatus.Pending, TseChangeNotificationStatus.Superseded) => true,
        (TseChangeNotificationStatus.ReadyForSubmission, TseChangeNotificationStatus.Submitted) => true,
        (TseChangeNotificationStatus.ReadyForSubmission, TseChangeNotificationStatus.Failed) => true,
        (TseChangeNotificationStatus.ReadyForSubmission, TseChangeNotificationStatus.Pending) => true,
        (TseChangeNotificationStatus.ReadyForSubmission, TseChangeNotificationStatus.Superseded) => true,
        (TseChangeNotificationStatus.Failed, TseChangeNotificationStatus.ReadyForSubmission) => true,
        (TseChangeNotificationStatus.Failed, TseChangeNotificationStatus.Superseded) => true,
        _ => false
    };
}

/// <summary>
/// Gate for automatic submission to ELSTER/Finanzamt. Stays false until the
/// law requiring it is in force and the submission path has been accepted in a
/// reviewed release (tools/Verify-Fiscal-Release-Gates.ps1 checks it). A
/// manual submission by the operator, recorded with its ELSTER reference, is
/// not affected.
/// </summary>
public static class FiscalNotificationRelease
{
    public const bool AutomaticSubmissionEnabled = false;

    public const string NotReleasedMessage =
        "Automatische Meldung an das Finanzamt ist nicht freigegeben. Die Daten stehen für eine manuelle Mitteilung über Mein ELSTER bereit.";

    public static void RequireAutomaticSubmission()
    {
        if (!AutomaticSubmissionEnabled)
            throw new InvalidOperationException(NotReleasedMessage);
    }
}

public sealed record FiscalNotificationSubmission(bool Success, string Reference, string ErrorCode, string Message);

/// <summary>A channel that could one day submit fiscal notifications (e.g. ELSTER ERiC).</summary>
public interface IFiscalNotificationProvider
{
    string ProviderId { get; }
    ProviderReadiness GetReadiness();
    Task<FiscalNotificationSubmission> SubmitAsync(TseChangeRecord change, CancellationToken ct = default);
}

/// <summary>The only provider today: reports itself as not configured and refuses to submit.</summary>
public sealed class DisabledFiscalNotificationProvider : IFiscalNotificationProvider
{
    public string ProviderId => "NONE";

    public ProviderReadiness GetReadiness() =>
        ProviderReadiness.NotConfigured(ProviderId, ProviderCapability.FiscalNotification, FiscalNotificationRelease.NotReleasedMessage);

    public Task<FiscalNotificationSubmission> SubmitAsync(TseChangeRecord change, CancellationToken ct = default)
    {
        FiscalNotificationRelease.RequireAutomaticSubmission();
        throw new InvalidOperationException(FiscalNotificationRelease.NotReleasedMessage);
    }
}

public interface ITseChangeJournal
{
    /// <summary>Appends the change and its first notification state in one transaction.</summary>
    Task RecordAsync(TseChangeRecord change, FiscalComplianceProfile profile, CancellationToken ct = default);

    /// <summary>Appends a notification state; rejects transitions the rules do not allow.</summary>
    Task SetNotificationStatusAsync(
        string changeId,
        TseChangeNotificationStatus status,
        string actor,
        string note = "",
        string submissionReference = "",
        CancellationToken ct = default);

    Task<IReadOnlyList<TseChangeRecord>> ListAsync(CancellationToken ct = default);

    Task<IReadOnlyList<TseChangeNotificationRecord>> NotificationHistoryAsync(string changeId, CancellationToken ct = default);
}
