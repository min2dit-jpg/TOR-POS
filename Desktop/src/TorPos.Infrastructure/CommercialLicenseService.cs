using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed class CommercialLicenseService : ICommercialLicenseService
{
    private const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAsVvzl32YVqkJfhoGgatf
        NGml5A1MNhI231Cz8tQYhNoXbSaqvVZWnQS4f53DaBzqprldL0OEiQGK74tXzrCS
        MzAlbuG+S5QpsmmUXZxXiirtoigcwyidaOdnwydohh8PrHc47zeSmWNYlRWS1pDs
        bj6skap3mr7Jk99zrzy+J1664Se7IG7GGj2F4ktyenrgT5GheNDw8QlS6RTIJqgu
        Z23fOVpS/xnl5rCkq9iGTPyorwIgBMwHQ2v+kGypfjW6JlUZtrScTConSvMY/HrX
        UTnH30rZhXYzJjv6SQ6D3IoKQQjcXPL2a2XznD+RsFr9/deYEf3uATsfamLWbjSy
        cVYskvoSr0GAXt9/kJxGapBqXv4z11F8Hw1iMQ+H5FULFqRwfwfViCaswp+cF1ia
        Oi2xkxFnmN9vI//7kkfEvR/2QCjqsX1qWeKFVYGVIe4pYVWjitvCnRVhiig8YO5t
        SgJh09PWzJs0G6MDfEX5YAf6VduKElrzyR69wA958FAxAgMBAAE=
        -----END PUBLIC KEY-----
        """;

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

    private readonly LicenseTamperStore _tamper;

    public CommercialLicenseService()
        : this(null)
    {
    }

    /// <param name="tamper">G-4: deactivation tombstones and clock high-water mark (tests pass their own).</param>
    public CommercialLicenseService(LicenseTamperStore? tamper)
    {
        InstallationId = LoadOrCreateInstallationId();
        DeviceCode = CreateDeviceCode(InstallationId);
        _tamper = tamper ?? LicenseTamperStore.Default();
    }

    public string InstallationId { get; }
    public string DeviceCode { get; }
    public string LicenseFilePath => AppPaths.CommercialLicensePath;

    public CommercialLicenseStatus Check(string edition)
    {
        if (!File.Exists(LicenseFilePath))
        {
            return new CommercialLicenseStatus(
                CommercialLicenseState.Missing,
                "Keine kommerzielle Lizenzdatei installiert.");
        }

        return ValidateFile(LicenseFilePath, edition);
    }

    public CommercialLicenseStatus Import(string sourcePath, string edition)
    {
        var status = ValidateFile(sourcePath, edition);

        if (!status.IsActive)
            return status;

        if (string.Equals(
                Path.GetFullPath(sourcePath),
                Path.GetFullPath(LicenseFilePath),
                StringComparison.OrdinalIgnoreCase))
        {
            return status;
        }

        Directory.CreateDirectory(AppPaths.DataDirectory);
        var temporary = LicenseFilePath + ".new";
        File.Copy(sourcePath, temporary, true);
        File.Move(temporary, LicenseFilePath, true);

        return Check(edition);
    }

    public CommercialLicenseStatus Deactivate(
        string edition,
        string deactivatedBy,
        string receiptTargetPath)
    {
        if (!File.Exists(LicenseFilePath))
        {
            return new CommercialLicenseStatus(
                CommercialLicenseState.Missing,
                "Keine installierte Lizenz vorhanden.");
        }

        var current = ValidateFile(
            LicenseFilePath,
            edition,
            ignoreLocalDeactivation: true);

        if (!current.IsActive)
            return current;

        if (string.IsNullOrWhiteSpace(current.LicenseId))
            return Invalid("Die installierte Lizenz besitzt keine gültige Lizenz-ID.");

        Directory.CreateDirectory(AppPaths.DataDirectory);

        var hash = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(LicenseFilePath)));

        var record = new CommercialLicenseDeactivationRecord(
            "TOR POS Pro",
            current.LicenseId,
            current.CustomerNumber,
            current.CustomerName,
            InstallationId,
            DeviceCode,
            NormalizeEdition(edition),
            string.IsNullOrWhiteSpace(deactivatedBy) ? "ADMIN" : deactivatedBy.Trim(),
            DateTimeOffset.UtcNow,
            hash);

        var json = JsonSerializer.Serialize(record, JsonOptions);
        // G-4: also as tombstone in the machine-wide store and the registry, so
        // deleting the journal file alone does not bring the licence back.
        _tamper.RecordDeactivation(current.LicenseId);
        File.AppendAllText(
            AppPaths.LicenseDeactivationJournalPath,
            json.Replace(Environment.NewLine, "") + Environment.NewLine,
            new UTF8Encoding(false));

        if (!string.IsNullOrWhiteSpace(receiptTargetPath))
        {
            var directory = Path.GetDirectoryName(receiptTargetPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(
                receiptTargetPath,
                json,
                new UTF8Encoding(false));
        }

        return Check(edition);
    }

    public void ExportActivationRequest(
        string targetPath,
        string edition,
        string customerNumber,
        string customerName,
        string productVersion)
    {
        customerNumber = NormalizeCustomerNumber(customerNumber);
        if (customerNumber.Length < 3)
        {
            throw new ArgumentException(
                "Eine gültige Kunden-Nr. mit mindestens drei Zeichen ist erforderlich.",
                nameof(customerNumber));
        }

        var request = new CommercialActivationRequest(
            "TOR POS Pro",
            productVersion,
            customerNumber,
            customerName.Trim(),
            InstallationId,
            DeviceCode,
            NormalizeEdition(edition),
            DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(request, JsonOptions);
        File.WriteAllText(targetPath, json, new UTF8Encoding(false));
    }

    private CommercialLicenseStatus ValidateFile(
        string path,
        string edition,
        bool ignoreLocalDeactivation = false)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new CommercialLicenseStatus(
                    CommercialLicenseState.Missing,
                    "Lizenzdatei wurde nicht gefunden.");
            }

            var envelope = JsonSerializer.Deserialize<CommercialLicenseEnvelope>(
                File.ReadAllText(path, Encoding.UTF8),
                JsonOptions);

            if (envelope is null ||
                string.IsNullOrWhiteSpace(envelope.PayloadJson) ||
                string.IsNullOrWhiteSpace(envelope.SignatureBase64))
            {
                return Invalid("Lizenzdatei ist unvollständig.");
            }

            byte[] signature;
            try
            {
                signature = Convert.FromBase64String(envelope.SignatureBase64);
            }
            catch (FormatException)
            {
                return Invalid("Lizenzsignatur ist nicht gültig codiert.");
            }

            using var rsa = RSA.Create();
            rsa.ImportFromPem(PublicKeyPem);

            var verified = rsa.VerifyData(
                Encoding.UTF8.GetBytes(envelope.PayloadJson),
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            if (!verified)
                return Invalid("Lizenzsignatur ist ungültig oder die Datei wurde verändert.");

            var payload = JsonSerializer.Deserialize<CommercialLicensePayload>(
                envelope.PayloadJson,
                JsonOptions);

            if (payload is null)
                return Invalid("Lizenzinhalt kann nicht gelesen werden.");

            if (NormalizeCustomerNumber(payload.CustomerNumber).Length < 3)
                return Invalid("Die Lizenz enthält keine gültige Kunden-Nr.");

            if (!string.Equals(
                    payload.InstallationId,
                    InstallationId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new CommercialLicenseStatus(
                    CommercialLicenseState.WrongInstallation,
                    "Die Lizenz gehört zu einer anderen TOR-POS-Installation.",
                    payload.CustomerNumber,
                    payload.CustomerName,
                    payload.LicenseId,
                    payload.ValidUntilUtc);
            }

            if (!string.Equals(
                    payload.DeviceCode,
                    DeviceCode,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new CommercialLicenseStatus(
                    CommercialLicenseState.WrongInstallation,
                    "Die Lizenz ist für einen anderen PC ausgestellt.",
                    payload.CustomerNumber,
                    payload.CustomerName,
                    payload.LicenseId,
                    payload.ValidUntilUtc);
            }

            if (!string.Equals(
                    NormalizeEdition(payload.Edition),
                    NormalizeEdition(edition),
                    StringComparison.Ordinal))
            {
                return new CommercialLicenseStatus(
                    CommercialLicenseState.WrongEdition,
                    $"Die Lizenz gilt für {payload.Edition}, installiert ist {edition}.",
                    payload.CustomerNumber,
                    payload.CustomerName,
                    payload.LicenseId,
                    payload.ValidUntilUtc);
            }

            if (!ignoreLocalDeactivation && IsLocallyDeactivated(payload.LicenseId))
            {
                return new CommercialLicenseStatus(
                    CommercialLicenseState.Deactivated,
                    "Die TOR-POS-Lizenz wurde auf diesem PC deaktiviert. Für eine erneute Aktivierung ist eine neue Lizenz erforderlich.",
                    payload.CustomerNumber,
                    payload.CustomerName,
                    payload.LicenseId,
                    payload.ValidUntilUtc);
            }

            // G-4: judged at the latest time this PC has seen, so turning the
            // Windows clock back does not extend the licence.
            if (payload.ValidUntilUtc <= _tamper.EffectiveUtcNow(DateTimeOffset.UtcNow))
            {
                return new CommercialLicenseStatus(
                    CommercialLicenseState.Expired,
                    $"Die kommerzielle Lizenz ist am {payload.ValidUntilUtc:dd.MM.yyyy} abgelaufen.",
                    payload.CustomerNumber,
                    payload.CustomerName,
                    payload.LicenseId,
                    payload.ValidUntilUtc);
            }

            return new CommercialLicenseStatus(
                CommercialLicenseState.Active,
                $"Kommerzielle Lizenz aktiv bis {payload.ValidUntilUtc:dd.MM.yyyy}.",
                payload.CustomerNumber,
                payload.CustomerName,
                payload.LicenseId,
                payload.ValidUntilUtc,
                payload.Features ?? Array.Empty<string>());
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            JsonException or
            CryptographicException)
        {
            return Invalid("Lizenzprüfung fehlgeschlagen: " + ex.Message);
        }
    }

    private bool IsLocallyDeactivated(string licenseId)
    {
        if (string.IsNullOrWhiteSpace(licenseId))
            return false;
        if (_tamper.IsDeactivated(licenseId))
            return true;
        if (!File.Exists(AppPaths.LicenseDeactivationJournalPath))
            return false;

        try
        {
            foreach (var line in File.ReadLines(
                         AppPaths.LicenseDeactivationJournalPath,
                         Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var record = JsonSerializer.Deserialize<CommercialLicenseDeactivationRecord>(
                        line,
                        JsonOptions);

                    if (record is not null &&
                        string.Equals(
                            record.LicenseId,
                            licenseId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (JsonException)
                {
                    // A damaged historic line must not prevent reading later records.
                }
            }
        }
        catch (IOException)
        {
            // License validation remains deterministic if the journal cannot be read.
        }
        catch (UnauthorizedAccessException)
        {
            // License validation remains deterministic if the journal cannot be read.
        }

        return false;
    }

    private static CommercialLicenseStatus Invalid(string message) =>
        new(CommercialLicenseState.Invalid, message);

    private static string NormalizeEdition(string edition) =>
        edition?.Trim().ToUpperInvariant() ?? "";

    private static string NormalizeCustomerNumber(string customerNumber) =>
        new(
            (customerNumber ?? "")
                .Trim()
                .ToUpperInvariant()
                .Where(x => char.IsLetterOrDigit(x) || x is '-' or '_')
                .Take(40)
                .ToArray());

    private static string CreateDeviceCode(string installationId)
    {
        var machineGuid = "";
        var volumeSerial = "";

        if (OperatingSystem.IsWindows())
        {
            try
            {
                machineGuid = Registry.LocalMachine
                    .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography")?
                    .GetValue("MachineGuid")?
                    .ToString() ?? "";
            }
            catch
            {
                // The installation ID remains as the non-empty fallback.
            }

            try
            {
                var root = Path.GetPathRoot(Environment.SystemDirectory);
                if (!string.IsNullOrWhiteSpace(root) &&
                    GetVolumeInformation(
                        root,
                        null,
                        0,
                        out var serial,
                        out _,
                        out _,
                        null,
                        0))
                {
                    volumeSerial = serial.ToString("X8");
                }
            }
            catch
            {
                // The fingerprint still includes MachineGuid and installation ID.
            }
        }

        var raw = Encoding.UTF8.GetBytes(
            $"TOR-POS-PC-V1|{installationId}|{machineGuid}|{volumeSerial}");
        var hash = SHA256.HashData(raw);
        return "PC-" + Convert.ToHexString(hash)[..24];
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(
        string rootPathName,
        StringBuilder? volumeNameBuffer,
        uint volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder? fileSystemNameBuffer,
        uint fileSystemNameSize);

    private static string LoadOrCreateInstallationId()
    {
        var path = AppPaths.LicenseInstallationIdPath;

        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path, Encoding.UTF8).Trim();
            if (existing.StartsWith("TOR-", StringComparison.Ordinal) && existing.Length >= 20)
                return existing;
        }

        var created = "TOR-" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        File.WriteAllText(path, created, new UTF8Encoding(false));
        return created;
    }
}
