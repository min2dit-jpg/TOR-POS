namespace TorPos.Core;

public enum TseConnectionState
{
    NotConfigured,
    SdkMissing,
    NotFound,
    Connected,
    Ready,
    Error
}

public sealed record TseRuntimeStatus(
    bool SdkLoaded,
    bool RequiredApiAvailable,
    string SdkVersion,
    string LibraryPath,
    string Message);

public sealed record TseDeviceInfo(
    string Manufacturer,
    string ProductFamily,
    string Generation,
    string FormFactor,
    string SerialNumber,
    string BsiCertificationId,
    DateOnly? CertificateValidUntil,
    string DevicePath,
    string SdkVersion = "",
    string HardwareVersion = "",
    string SoftwareVersion = "",
    string InitializationState = "",
    bool SelfTestPassed = false,
    bool ValidTime = false,
    bool CtssActive = false);

public enum TseCertificateState
{
    Unknown,
    Valid,
    Warning,
    Critical,
    Expired
}

public sealed record TseCertificateAssessment(
    TseCertificateState State,
    DateOnly? ValidUntil,
    int? RemainingDays,
    bool ShowBadge,
    bool ShowDialog,
    string Message);

/// <summary>
/// Proactive TSE certificate lifecycle policy. The device already exposes its
/// certificate end date; TOR must surface it before signing suddenly stops.
/// 90 days gives the operator a procurement window, while 30 days escalates
/// to one prominent warning per process. Expiry is fail-closed.
/// </summary>
public static class TseCertificatePolicy
{
    public const int WarningDays = 90;
    public const int CriticalDays = 30;

    public static TseCertificateAssessment Evaluate(
        DateOnly? validUntil,
        DateOnly today)
    {
        if (validUntil is null)
        {
            return new TseCertificateAssessment(
                TseCertificateState.Unknown,
                null,
                null,
                false,
                false,
                "TSE-Zertifikatsende ist nicht verfügbar.");
        }

        var remaining = validUntil.Value.DayNumber - today.DayNumber;
        var date = validUntil.Value.ToString("dd.MM.yyyy");

        if (remaining < 0)
        {
            return new TseCertificateAssessment(
                TseCertificateState.Expired,
                validUntil,
                remaining,
                true,
                true,
                $"TSE-Zertifikat ist seit {date} abgelaufen · TSE ersetzen.");
        }

        if (remaining <= CriticalDays)
        {
            var remainingText = remaining == 0
                ? "läuft heute ab"
                : $"läuft in {remaining} Tagen ab";

            return new TseCertificateAssessment(
                TseCertificateState.Critical,
                validUntil,
                remaining,
                true,
                true,
                $"TSE-Zertifikat {remainingText} ({date}) · Ersatz-TSE jetzt bestellen.");
        }

        if (remaining <= WarningDays)
        {
            return new TseCertificateAssessment(
                TseCertificateState.Warning,
                validUntil,
                remaining,
                true,
                false,
                $"TSE-Zertifikat läuft am {date} ab · noch {remaining} Tage · Ersatz-TSE einplanen.");
        }

        return new TseCertificateAssessment(
            TseCertificateState.Valid,
            validUntil,
            remaining,
            false,
            false,
            $"TSE-Zertifikat gültig bis {date}.");
    }
}

public sealed record TseProbeResult(
    TseConnectionState State,
    string Message,
    TseDeviceInfo? Device = null);

public sealed record TseActivationRequest(
    string ClientId,
    string AdminPin,
    string Puk,
    string TimeAdminPin = "",
    string CredentialSeed = "");

public sealed record TseActivationResult(
    bool Success,
    string Message,
    TseDeviceInfo? Device = null);

public sealed record TseTransactionStartRequest(
    string ClientId,
    byte[] ProcessData,
    string ProcessType,
    string TimeAdminPin = "",
    string StableTransactionId = "");

public sealed record TseTransactionUpdateRequest(
    string ClientId,
    ulong TransactionNumber,
    byte[] ProcessData,
    string ProcessType,
    string TimeAdminPin = "");

public sealed record TseTransactionFinishRequest(
    string ClientId,
    ulong TransactionNumber,
    byte[] ProcessData,
    string ProcessType,
    string TimeAdminPin = "");

public sealed record TseTransactionResult(
    bool Success,
    string Message,
    ulong TransactionNumber = 0,
    ulong SignatureCounter = 0,
    DateTimeOffset? LogTime = null,
    string SerialNumber = "",
    string SignatureBase64 = "");

public sealed record TseExportResult(
    bool Success,
    string Message,
    string FilePath = "");

public interface ITseProvider
{
    string ProviderId { get; }
    string DisplayName { get; }
    string PreferredProduct { get; }

    bool SdkAvailable { get; }
    bool ActivationAvailable { get; }
    bool TransactionAvailable { get; }
    bool ExportAvailable { get; }

    TseRuntimeStatus GetRuntimeStatus();

    Task<IReadOnlyList<string>> FindSdkLibrariesAsync(
        CancellationToken ct = default);

    TseRuntimeStatus ConfigureSdkLibrary(
        string libraryPath);

    Task<TseProbeResult> ProbeAsync(
        CancellationToken ct = default);

    Task<TseActivationResult> ActivateAsync(
        TseActivationRequest request,
        CancellationToken ct = default);

    Task<TseTransactionResult> StartTransactionAsync(
        TseTransactionStartRequest request,
        CancellationToken ct = default);

    Task<TseTransactionResult> UpdateTransactionAsync(
        TseTransactionUpdateRequest request,
        CancellationToken ct = default);

    Task<TseTransactionResult> FinishTransactionAsync(
        TseTransactionFinishRequest request,
        CancellationToken ct = default);

    Task<TseExportResult> ExportTarAsync(
        string targetPath,
        CancellationToken ct = default);
}
