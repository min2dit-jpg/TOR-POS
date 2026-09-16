namespace TorPos.Core;

public enum CommercialLicenseState
{
    Missing,
    Invalid,
    WrongInstallation,
    WrongEdition,
    Expired,
    Deactivated,
    Active
}

public sealed record CommercialLicensePayload(
    string LicenseId,
    string CustomerNumber,
    string CustomerName,
    string InstallationId,
    string DeviceCode,
    string Edition,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ValidUntilUtc,
    IReadOnlyList<string> Features);

public sealed record CommercialLicenseEnvelope(
    string PayloadJson,
    string SignatureBase64);

public sealed record CommercialActivationRequest(
    string Product,
    string Version,
    string CustomerNumber,
    string CustomerName,
    string InstallationId,
    string DeviceCode,
    string Edition,
    DateTimeOffset CreatedAtUtc);

public sealed record CommercialLicenseDeactivationRecord(
    string Product,
    string LicenseId,
    string CustomerNumber,
    string CustomerName,
    string InstallationId,
    string DeviceCode,
    string Edition,
    string DeactivatedBy,
    DateTimeOffset DeactivatedAtUtc,
    string LicenseFileSha256);

public sealed record CommercialLicenseStatus(
    CommercialLicenseState State,
    string Message,
    string CustomerNumber = "",
    string CustomerName = "",
    string LicenseId = "",
    DateTimeOffset? ValidUntilUtc = null)
{
    public bool IsActive => State == CommercialLicenseState.Active;
    public TimeSpan? Remaining => ValidUntilUtc is null ? null : ValidUntilUtc.Value - DateTimeOffset.UtcNow;
    public int? RemainingDays => Remaining is null ? null : Math.Max(0, (int)Math.Ceiling(Remaining.Value.TotalDays));
    public bool IsExpiringSoon => IsActive && RemainingDays is >= 0 and <= 30;
    public string RenewalNotice
    {
        get
        {
            if (!IsExpiringSoon || RemainingDays is null || ValidUntilUtc is null) return "";
            var days = RemainingDays.Value;
            return days switch
            {
                0 => $"Lizenz läuft heute ab · {ValidUntilUtc:dd.MM.yyyy}",
                1 => $"Lizenz läuft morgen ab · {ValidUntilUtc:dd.MM.yyyy}",
                _ => $"Lizenz läuft in {days} Tagen ab · {ValidUntilUtc:dd.MM.yyyy}"
            };
        }
    }
}

public interface ICommercialLicenseService
{
    string InstallationId { get; }
    string DeviceCode { get; }
    string LicenseFilePath { get; }

    CommercialLicenseStatus Check(string edition);
    CommercialLicenseStatus Import(string sourcePath, string edition);
    CommercialLicenseStatus Deactivate(
        string edition,
        string deactivatedBy,
        string receiptTargetPath);

    void ExportActivationRequest(
        string targetPath,
        string edition,
        string customerNumber,
        string customerName,
        string productVersion);
}
