using System.Security.Cryptography;
using System.Text;
using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed record BackupEncryptionStatus(
    bool Enabled,
    string RecoveryFingerprint,
    bool UsesLegacyKeyDerivation = false);

/// <summary>
/// R76: at-rest encryption for backup files (AES-256-GCM), applied as a thin
/// post-processing step around the existing, already-tested backup writers
/// (DatabaseBackupService, FullBackupService) rather than inside them - their
/// own plain-file behavior is unchanged, and internal/ephemeral backups the
/// app immediately reads back (pre-migration safety snapshots) intentionally
/// stay unencrypted so a migration failure never depends on a recovery code.
///
/// The per-backup data key (DEK) is wrapped two ways, mirroring how BitLocker
/// combines an automatic unlock path with a manual recovery key:
///  - Windows DPAPI (LocalMachine scope): silent, automatic decrypt as long as
///    this same machine is available. Chosen over CurrentUser scope so any
///    admin/technician account on the register can restore, not only the
///    Windows login that happened to create the backup.
///  - A one-time recovery code, shown to the admin exactly once when
///    encryption is enabled. This is the only path that still works if the
///    machine itself is gone (disk failure, theft, replacement) - and,
///    fundamentally, if that code is lost AND the machine is gone, the
///    backup is unrecoverable by design; that is what "encrypted" means.
/// </summary>
public sealed class BackupEncryptionService
{
    private const string EnabledKey = "backup.encryption.enabled";
    private const string RecoveryKekProtectedKey = "backup.encryption.recovery_kek_protected";
    private const string RecoveryFingerprintKey = "backup.encryption.recovery_fingerprint";
    private const string RecoverySaltKey = "backup.encryption.recovery_salt";
    private const string RecoveryIterationsKey = "backup.encryption.recovery_iterations";

    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("TOR-POS-BACKUP-DEK-v1");
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("TPEB");

    /// <summary>
    /// Format 1 derived the recovery KEK as a single, unsalted SHA-256 over the
    /// typed code (audit finding G5). Format 2 uses salted PBKDF2-SHA256 and
    /// carries the salt and iteration count IN THE CONTAINER, because a restore
    /// on a replacement machine has the file and the code and nothing else.
    ///
    /// Format 1 stays readable forever: backups already in a customer's hands
    /// must keep restoring. Which format is WRITTEN depends only on whether
    /// this installation has a stored salt - and it only gets one by generating
    /// a new recovery code, since the old KEK cannot be re-derived without the
    /// code, which TOR POS deliberately never stored.
    /// </summary>
    private const byte FormatVersionLegacy = 1;
    private const byte FormatVersionPbkdf2 = 2;
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int FingerprintBytes = 4;
    private const int SaltBytes = 16;
    private const int CurrentKdfIterations = 600_000;
    private const int MinimumAcceptedKdfIterations = 100_000;
    private const int MaximumAcceptedKdfIterations = 10_000_000;

    // A corrupt or hostile .tpe must fail with a clear message, not allocate
    // whatever length its header claims (audit finding, low: unchecked
    // allocation). The DPAPI blob for a 32-byte key is a few hundred bytes.
    private const int MaximumDpapiBlobBytes = 64 * 1024;

    private readonly ISettingsRepository _settings;

    public BackupEncryptionService(ISettingsRepository settings) => _settings = settings;

    public async Task<BackupEncryptionStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var values = await _settings.LoadAllAsync(ct);
        var enabled = string.Equals(values.GetValueOrDefault(EnabledKey, "false"), "true", StringComparison.OrdinalIgnoreCase);
        var fingerprint = values.GetValueOrDefault(RecoveryFingerprintKey, "");
        // R122: an installation set up before this release has a KEK derived
        // the old way and no salt. It still works; it just cannot be upgraded
        // silently, because upgrading means deriving from the code and the code
        // was shown once and never stored. Surfacing it lets the Settings page
        // offer the one action that does fix it: generate a new recovery code.
        var legacy = fingerprint.Length > 0 &&
                     string.IsNullOrWhiteSpace(values.GetValueOrDefault(RecoverySaltKey, ""));
        return new BackupEncryptionStatus(enabled, fingerprint, legacy);
    }

    /// <summary>
    /// Generates a brand-new recovery code, wraps it as the recovery KEK, and
    /// enables encryption. The returned code is shown to the caller exactly
    /// once; TOR POS never stores or displays it again. Calling this again
    /// (e.g. "regenerate") supersedes the previous code for FUTURE backups
    /// only - backups already written can still only be recovered with the
    /// recovery code that was current when they were created, or on the
    /// original machine via DPAPI.
    /// </summary>
    public async Task<string> EnableAndGenerateRecoveryCodeAsync(CancellationToken ct = default)
    {
        var codeBytes = RandomNumberGenerator.GetBytes(16);
        var code = FormatRecoveryCode(codeBytes);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var kek = DeriveKek(code, salt, CurrentKdfIterations);

        var protectedKek = Convert.ToBase64String(
            ProtectedData.Protect(kek, DpapiEntropy, DataProtectionScope.LocalMachine));

        var fingerprint = Fingerprint(kek);

        await _settings.SaveManyAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [EnabledKey] = "true",
            [RecoveryKekProtectedKey] = protectedKek,
            [RecoveryFingerprintKey] = fingerprint,
            [RecoverySaltKey] = Convert.ToHexString(salt),
            [RecoveryIterationsKey] = CurrentKdfIterations.ToString()
        }, ct);

        return code;
    }

    public async Task DisableAsync(CancellationToken ct = default)
    {
        // Keeps the wrapped KEK in place (already-written backups remain
        // recoverable with the recovery code); only stops encrypting new ones.
        await _settings.SaveManyAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [EnabledKey] = "false"
        }, ct);
    }

    /// <summary>
    /// Verifies a typed recovery code against the stored fingerprint without
    /// needing an actual backup file - lets the setup UI catch a typo
    /// immediately instead of only failing much later during a real restore.
    /// </summary>
    public async Task<bool> VerifyRecoveryCodeAsync(string candidate, CancellationToken ct = default)
    {
        var values = await _settings.LoadAllAsync(ct);
        var stored = values.GetValueOrDefault(RecoveryFingerprintKey, "");
        if (string.IsNullOrWhiteSpace(stored)) return false;

        // Derives the way THIS installation's KEK was derived - salted PBKDF2
        // if a salt was stored (R122 onwards), the legacy unsalted SHA-256 if
        // the recovery code predates it.
        var saltHex = values.GetValueOrDefault(RecoverySaltKey, "");
        var kek = string.IsNullOrWhiteSpace(saltHex)
            ? DeriveKekLegacy(candidate)
            : DeriveKek(
                candidate,
                Convert.FromHexString(saltHex),
                ParseIterations(values.GetValueOrDefault(RecoveryIterationsKey, "")));

        return string.Equals(Fingerprint(kek), stored, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// If backup encryption is enabled, encrypts plainPath in place (writes
    /// "{plainPath}.tpe", deletes plainPath) and returns the new path.
    /// If disabled, returns plainPath unchanged - callers should always use
    /// the returned path, not assume the extension.
    /// </summary>
    public async Task<string> EncryptIfEnabledAsync(string plainPath, CancellationToken ct = default)
    {
        var values = await _settings.LoadAllAsync(ct);
        if (!string.Equals(values.GetValueOrDefault(EnabledKey, "false"), "true", StringComparison.OrdinalIgnoreCase))
            return plainPath;

        var protectedKek = values.GetValueOrDefault(RecoveryKekProtectedKey, "");
        if (string.IsNullOrWhiteSpace(protectedKek))
        {
            // Enabled, but the key is missing (never set up, or the setting was
            // lost). The plain backup is kept - losing it would be worse - but
            // it is never reported as the encrypted backup the operator expects.
            throw new BackupNotEncryptedException(plainPath);
        }

        var kek = ProtectedData.Unprotect(Convert.FromBase64String(protectedKek), DpapiEntropy, DataProtectionScope.LocalMachine);
        var fingerprint = values.GetValueOrDefault(RecoveryFingerprintKey, "");

        // The KEK is the KEK whichever way it was derived; the version only
        // tells a future restore how to re-derive it from the typed code.
        var saltHex = values.GetValueOrDefault(RecoverySaltKey, "");
        var legacyKdf = string.IsNullOrWhiteSpace(saltHex);
        var salt = legacyKdf ? Array.Empty<byte>() : Convert.FromHexString(saltHex);
        var iterations = legacyKdf ? 0 : ParseIterations(values.GetValueOrDefault(RecoveryIterationsKey, ""));

        var dek = RandomNumberGenerator.GetBytes(KeyBytes);
        var plain = await File.ReadAllBytesAsync(plainPath, ct);
        var payloadNonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var payloadTag = new byte[TagBytes];
        var payloadCipher = new byte[plain.Length];
        using (var payloadAes = new AesGcm(dek, TagBytes))
            payloadAes.Encrypt(payloadNonce, plain, payloadCipher, payloadTag);

        var dpapiWrapped = ProtectedData.Protect(dek, DpapiEntropy, DataProtectionScope.LocalMachine);

        var recoveryNonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var recoveryTag = new byte[TagBytes];
        var recoveryWrappedDek = new byte[KeyBytes];
        using (var kekAes = new AesGcm(kek, TagBytes))
            kekAes.Encrypt(recoveryNonce, dek, recoveryWrappedDek, recoveryTag);

        var target = plainPath + ".tpe";
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        await using (var writer = new BinaryWriter(output))
        {
            writer.Write(Magic);
            writer.Write(legacyKdf ? FormatVersionLegacy : FormatVersionPbkdf2);
            if (!legacyKdf)
            {
                writer.Write(iterations);
                writer.Write(salt.Length);
                writer.Write(salt);
            }
            writer.Write(payloadNonce);
            writer.Write(payloadTag);
            writer.Write(dpapiWrapped.Length);
            writer.Write(dpapiWrapped);
            writer.Write(recoveryNonce);
            writer.Write(recoveryTag);
            writer.Write(recoveryWrappedDek);
            writer.Write(Convert.FromHexString(fingerprint.PadRight(FingerprintBytes * 2, '0')));
            writer.Write(payloadCipher);
            writer.Flush();
            output.Flush(true);
        }

        File.Delete(plainPath);
        return target;
    }

    public static bool LooksEncrypted(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            if (f.Length < Magic.Length) return false;
            Span<byte> header = stackalloc byte[Magic.Length];
            return f.Read(header) == Magic.Length && header.SequenceEqual(Magic);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Decrypts an encrypted backup container to a plain output file.
    /// Tries the local machine's DPAPI-wrapped key first (works unattended on
    /// the same machine); if that is unavailable/fails and no recovery code
    /// was supplied, throws RecoveryCodeRequiredException so the caller can
    /// prompt for one and retry.
    /// </summary>
    public static async Task DecryptAsync(string encryptedPath, string outputPlainPath, string? recoveryCode, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(encryptedPath, ct);
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream);

        // Every read below goes through ReadExact, which refuses a length the
        // file cannot actually satisfy. Before R122 a corrupt (or deliberately
        // crafted) .tpe could declare a multi-gigabyte DPAPI blob and the
        // restore attempt died with an OutOfMemoryException instead of
        // "damaged backup".
        var magic = ReadExact(reader, stream, Magic.Length, "Signatur");
        if (!magic.SequenceEqual(Magic))
            throw new InvalidDataException("Keine verschlüsselte TOR-POS-Sicherung (Signatur fehlt).");
        var version = reader.ReadByte();
        if (version is not (FormatVersionLegacy or FormatVersionPbkdf2))
            throw new InvalidDataException($"Unbekanntes Verschlüsselungsformat (Version {version}).");

        var iterations = 0;
        var salt = Array.Empty<byte>();
        if (version == FormatVersionPbkdf2)
        {
            iterations = reader.ReadInt32();
            if (iterations is < MinimumAcceptedKdfIterations or > MaximumAcceptedKdfIterations)
                throw new InvalidDataException("Sicherung beschädigt: unplausible Schlüsselableitung.");
            var saltLength = reader.ReadInt32();
            if (saltLength is < 8 or > 64)
                throw new InvalidDataException("Sicherung beschädigt: unplausible Salt-Länge.");
            salt = ReadExact(reader, stream, saltLength, "Salt");
        }

        var payloadNonce = ReadExact(reader, stream, NonceBytes, "Nonce");
        var payloadTag = ReadExact(reader, stream, TagBytes, "Prüfsumme");
        var dpapiLen = reader.ReadInt32();
        if (dpapiLen is < 0 or > MaximumDpapiBlobBytes)
            throw new InvalidDataException("Sicherung beschädigt: unplausible Schlüssellänge.");
        var dpapiWrapped = ReadExact(reader, stream, dpapiLen, "Maschinenschlüssel");
        var recoveryNonce = ReadExact(reader, stream, NonceBytes, "Nonce");
        var recoveryTag = ReadExact(reader, stream, TagBytes, "Prüfsumme");
        var recoveryWrappedDek = ReadExact(reader, stream, KeyBytes, "Wiederherstellungsschlüssel");
        _ = ReadExact(reader, stream, FingerprintBytes, "Kennung");
        var payloadCipher = reader.ReadBytes((int)(stream.Length - stream.Position));

        byte[]? dek = null;

        if (recoveryCode is null && dpapiLen > 0)
        {
            try
            {
                dek = ProtectedData.Unprotect(dpapiWrapped, DpapiEntropy, DataProtectionScope.LocalMachine);
            }
            catch (CryptographicException)
            {
                // Different machine, or the local DPAPI key material is gone; fall through to recovery code.
            }
        }

        if (dek is null)
        {
            if (recoveryCode is null)
                throw new RecoveryCodeRequiredException();

            var kek = version == FormatVersionPbkdf2
                ? DeriveKek(recoveryCode, salt, iterations)
                : DeriveKekLegacy(recoveryCode);
            dek = new byte[KeyBytes];
            try
            {
                using var kekAes = new AesGcm(kek, TagBytes);
                kekAes.Decrypt(recoveryNonce, recoveryWrappedDek, recoveryTag, dek);
            }
            catch (CryptographicException)
            {
                throw new InvalidOperationException("Wiederherstellungscode falsch oder Sicherung beschädigt.");
            }
        }

        var plain = new byte[payloadCipher.Length];
        using (var payloadAes = new AesGcm(dek, TagBytes))
        {
            try
            {
                payloadAes.Decrypt(payloadNonce, payloadCipher, payloadTag, plain);
            }
            catch (CryptographicException)
            {
                throw new InvalidOperationException("Entschlüsselung fehlgeschlagen: falscher Schlüssel oder beschädigte Sicherung.");
            }
        }

        await File.WriteAllBytesAsync(outputPlainPath, plain, ct);
    }

    /// <summary>
    /// R122 (G5): salted PBKDF2-SHA256. The recovery code is 128 bits of
    /// randomness, so the salt is not there to slow a dictionary attack on a
    /// weak passphrase - it is there so that two installations with the same
    /// code never share a KEK, and so that no precomputation is reusable.
    /// </summary>
    private static byte[] DeriveKek(string recoveryCode, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(NormalizeCode(recoveryCode)),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            KeyBytes);

    /// <summary>
    /// Format 1's unsalted single SHA-256. Kept ONLY so backups written before
    /// R122 still restore - never used for anything newly created.
    /// </summary>
    private static byte[] DeriveKekLegacy(string recoveryCode) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeCode(recoveryCode)));

    private static string Fingerprint(byte[] kek) =>
        Convert.ToHexString(SHA256.HashData(kek)).AsSpan(0, FingerprintBytes * 2).ToString();

    private static int ParseIterations(string stored) =>
        int.TryParse(stored, out var value) &&
        value is >= MinimumAcceptedKdfIterations and <= MaximumAcceptedKdfIterations
            ? value
            : CurrentKdfIterations;

    private static byte[] ReadExact(BinaryReader reader, Stream stream, int count, string what)
    {
        if (count < 0 || count > stream.Length - stream.Position)
            throw new InvalidDataException($"Sicherung beschädigt oder unvollständig ({what}).");
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
            throw new InvalidDataException($"Sicherung beschädigt oder unvollständig ({what}).");
        return bytes;
    }

    private static string NormalizeCode(string code) =>
        new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    private static string FormatRecoveryCode(byte[] bytes)
    {
        var hex = Convert.ToHexString(bytes); // 32 hex chars for 16 bytes
        var groups = Enumerable.Range(0, hex.Length / 4).Select(i => hex.Substring(i * 4, 4));
        return string.Join("-", groups);
    }
}

public sealed class RecoveryCodeRequiredException : Exception
{
    public RecoveryCodeRequiredException()
        : base("Diese Sicherung kann auf diesem Computer nicht automatisch entschlüsselt werden. Wiederherstellungscode erforderlich.")
    {
    }
}

/// <summary>
/// Backup encryption is switched on but no recovery key is set up: the backup
/// was written, unencrypted, to <see cref="PlainPath"/>. Callers show this to
/// the operator instead of reporting an encrypted backup.
/// </summary>
public sealed class BackupNotEncryptedException(string plainPath) : InvalidOperationException(
    $"Sicherung ist NICHT verschlüsselt: Die Verschlüsselung ist eingeschaltet, aber kein Wiederherstellungsschlüssel eingerichtet. " +
    $"Die Sicherung liegt unverschlüsselt unter {plainPath}. Bitte unter Einstellungen → Sicherung die Verschlüsselung neu einrichten.")
{
    public string PlainPath { get; } = plainPath;
}
