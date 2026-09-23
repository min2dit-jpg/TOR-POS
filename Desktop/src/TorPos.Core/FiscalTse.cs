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
