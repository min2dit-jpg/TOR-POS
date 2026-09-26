namespace TorPos.Core;

/// <summary>
/// Operations an AI feature could ask for. Listed up front so that no future AI
/// integration can reach a fiscal, payment or security operation by accident:
/// every operation has a fixed classification below.
/// </summary>
public enum AiOperation
{
    // Read / analyse / suggest
    ReadReports,
    AnalyzeSales,
    SuggestProductText,
    SuggestPrice,
    SuggestTaxSetting,
    SuggestStockOrder,

    // Critical - never directly by AI
    FinalizeTseTransaction,
    ChangeTseRecord,
    CompletePayment,
    ModifyPastSale,
    ModifyFinalInvoice,
    ModifyDsfinvkRecord,
    DeleteOrModifyAuditLog,
    ElevateUserPermission,
    ChangePriceOrTaxSetting
}

public enum AiOperationClass
{
    /// <summary>Read, analyse, suggest, show to the user. No state change.</summary>
    ReadAndSuggest,

    /// <summary>
    /// Only as an accepted suggestion executed by the deterministic service:
    /// AI suggestion → deterministic service → authorization → validation → audit → action.
    /// </summary>
    HumanApprovedDeterministicOnly,

    /// <summary>Never initiated or executed by AI, not even with confirmation.</summary>
    Forbidden
}

public static class AiSafetyBoundary
{
    public static AiOperationClass Classify(AiOperation operation) => operation switch
    {
        AiOperation.ReadReports or
        AiOperation.AnalyzeSales or
        AiOperation.SuggestProductText or
        AiOperation.SuggestPrice or
        AiOperation.SuggestTaxSetting or
        AiOperation.SuggestStockOrder => AiOperationClass.ReadAndSuggest,

        AiOperation.ChangePriceOrTaxSetting => AiOperationClass.HumanApprovedDeterministicOnly,

        AiOperation.FinalizeTseTransaction or
        AiOperation.ChangeTseRecord or
        AiOperation.CompletePayment or
        AiOperation.ModifyPastSale or
        AiOperation.ModifyFinalInvoice or
        AiOperation.ModifyDsfinvkRecord or
        AiOperation.DeleteOrModifyAuditLog or
        AiOperation.ElevateUserPermission => AiOperationClass.Forbidden,

        // An operation added later without a decision is forbidden.
        _ => AiOperationClass.Forbidden
    };

    /// <summary>
    /// Decides whether an AI-originated request may proceed. A forbidden
    /// operation is refused even when the user confirms; a critical change
    /// proceeds only when a human accepted it, the user holds the permission
    /// and the deterministic service (not the AI) runs it.
    /// </summary>
    public static AiAuthorization Authorize(AiOperation operation, bool userAccepted, bool userHasPermission, bool executedByDeterministicService)
    {
        return Classify(operation) switch
        {
            AiOperationClass.ReadAndSuggest => AiAuthorization.Allow("Nur lesen/analysieren/vorschlagen."),
            AiOperationClass.HumanApprovedDeterministicOnly when !userAccepted =>
                AiAuthorization.Deny("KI-Vorschlag wurde nicht vom Benutzer bestätigt."),
            AiOperationClass.HumanApprovedDeterministicOnly when !userHasPermission =>
                AiAuthorization.Deny("Benutzer hat keine Berechtigung für diese Änderung."),
            AiOperationClass.HumanApprovedDeterministicOnly when !executedByDeterministicService =>
                AiAuthorization.Deny("Die Änderung darf nur über den regulären TOR-Dienst mit Prüfung und Audit ausgeführt werden."),
            AiOperationClass.HumanApprovedDeterministicOnly =>
                AiAuthorization.Allow("Bestätigter Vorschlag über regulären Dienst."),
            _ => AiAuthorization.Deny("Diese Operation ist für KI gesperrt (Fiskal-, Zahlungs- oder Sicherheitskern).")
        };
    }
}

public sealed record AiAuthorization(bool Allowed, string Reason)
{
    public static AiAuthorization Allow(string reason) => new(true, reason);
    public static AiAuthorization Deny(string reason) => new(false, reason);
}

public static class AiAuditEventTypes
{
    public const string RecommendationCreated = "AiRecommendationCreated";
    public const string RecommendationAccepted = "AiRecommendationAccepted";
    public const string RecommendationRejected = "AiRecommendationRejected";
    public const string ActionExecuted = "AiActionExecuted";
    public const string ActionFailed = "AiActionFailed";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        RecommendationCreated, RecommendationAccepted, RecommendationRejected, ActionExecuted, ActionFailed
    };
}

public enum AiUserDecision
{
    None,
    Accepted,
    Rejected
}

/// <summary>
/// An AI audit entry. It goes to the audit log only - AI-generated free text
/// is never written into a fiscal record (sale, TSE data, DSFinV-K, invoice).
/// <see cref="DataUsed"/> is metadata about which data were used (e.g.
/// "sales 2026-09, aggregated"), not the data themselves.
/// </summary>
public sealed record AiAuditRecord(
    string EventType,
    DateTimeOffset At,
    string Actor,
    string ModelProvider,
    string Model,
    AiOperation Operation,
    string DataUsed,
    string Recommendation,
    AiUserDecision Decision,
    string Result)
{
    public const string AuditEntityType = "AI";
    public const int MaxTextLength = 2000;

    public AiAuditRecord Validate()
    {
        if (!AiAuditEventTypes.All.Contains(EventType))
            throw new ArgumentException($"Unbekannter KI-Audit-Typ: {EventType}", nameof(EventType));
        if (string.IsNullOrWhiteSpace(Actor))
            throw new ArgumentException("KI-Audit ohne Benutzer.", nameof(Actor));
        if (string.IsNullOrWhiteSpace(ModelProvider) || string.IsNullOrWhiteSpace(Model))
            throw new ArgumentException("KI-Audit ohne Modell/Anbieter.", nameof(Model));
        return this with
        {
            DataUsed = Clip(DataUsed),
            Recommendation = Clip(Recommendation),
            Result = Clip(Result)
        };
    }

    private static string Clip(string? text)
    {
        var clean = new string((text ?? "").Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray());
        return clean.Length <= MaxTextLength ? clean : clean[..MaxTextLength];
    }
}
