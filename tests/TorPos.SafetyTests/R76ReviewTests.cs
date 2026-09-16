using TorPos.Infrastructure;

public static class R76ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool,string> assert,
        Func<Func<Task>,string,Task> reject)
    {
        var dir = Path.Combine(root, "r76-backup-encryption");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r76.db"));
        var settings = new SettingsRepository(db);
        var encryption = new BackupEncryptionService(settings);

        var plainPath = Path.Combine(dir, "plain.txt");
        var originalBytes = System.Text.Encoding.UTF8.GetBytes("TOR POS backup content · äöüß · " + Guid.NewGuid());
        await File.WriteAllBytesAsync(plainPath, originalBytes);

        var statusBefore = await encryption.GetStatusAsync();
        assert(!statusBefore.Enabled, "R76 backup encryption is disabled by default");

        var untouchedPath = await encryption.EncryptIfEnabledAsync(plainPath);
        assert(untouchedPath == plainPath && File.Exists(plainPath), "R76 disabled encryption leaves the backup file untouched");

        var code = await encryption.EnableAndGenerateRecoveryCodeAsync();
        var statusAfter = await encryption.GetStatusAsync();
        assert(statusAfter.Enabled && statusAfter.RecoveryFingerprint.Length == 8, "R76 enabling encryption stores an enabled flag and a recovery fingerprint");

        assert(await encryption.VerifyRecoveryCodeAsync(code), "R76 the freshly generated recovery code verifies against its own fingerprint");
        assert(!await encryption.VerifyRecoveryCodeAsync("0000-0000-0000-0000"), "R76 a wrong recovery code does not verify");

        var encryptedPath = await encryption.EncryptIfEnabledAsync(plainPath);
        assert(encryptedPath == plainPath + ".tpe" && !File.Exists(plainPath), "R76 enabled encryption writes a .tpe container and deletes the plaintext");
        assert(BackupEncryptionService.LooksEncrypted(encryptedPath), "R76 encrypted container is recognized by its signature");
        assert(!BackupEncryptionService.LooksEncrypted(Path.Combine(dir, "r76.db")), "R76 a plain file is not misidentified as encrypted");

        var decryptedViaDpapiPath = Path.Combine(dir, "decrypted-dpapi.txt");
        await BackupEncryptionService.DecryptAsync(encryptedPath, decryptedViaDpapiPath, recoveryCode: null);
        assert((await File.ReadAllBytesAsync(decryptedViaDpapiPath)).SequenceEqual(originalBytes), "R76 automatic same-machine decryption (DPAPI) reproduces the exact original bytes");

        var decryptedViaCodePath = Path.Combine(dir, "decrypted-code.txt");
        await BackupEncryptionService.DecryptAsync(encryptedPath, decryptedViaCodePath, recoveryCode: code);
        assert((await File.ReadAllBytesAsync(decryptedViaCodePath)).SequenceEqual(originalBytes), "R76 recovery-code decryption reproduces the exact original bytes");

        await reject(
            () => BackupEncryptionService.DecryptAsync(encryptedPath, Path.Combine(dir, "should-not-exist.txt"), recoveryCode: "FFFF-FFFF-FFFF-FFFF"),
            "R76 a wrong recovery code fails decryption instead of producing garbage output");

        // Regenerating the code must not break decryption of a backup created under the OLD code
        // (its wrapped key is embedded per-file), while the OLD code stops verifying going forward.
        var newCode = await encryption.EnableAndGenerateRecoveryCodeAsync();
        assert(newCode != code, "R76 regenerating produces a different recovery code");
        assert(!await encryption.VerifyRecoveryCodeAsync(code), "R76 the old recovery code no longer verifies after regeneration");
        assert(await encryption.VerifyRecoveryCodeAsync(newCode), "R76 the new recovery code verifies after regeneration");

        var decryptedOldBackupWithOldCode = Path.Combine(dir, "decrypted-old-code.txt");
        await BackupEncryptionService.DecryptAsync(encryptedPath, decryptedOldBackupWithOldCode, recoveryCode: code);
        assert((await File.ReadAllBytesAsync(decryptedOldBackupWithOldCode)).SequenceEqual(originalBytes),
            "R76 a backup encrypted before regeneration still decrypts with its original recovery code");

        await encryption.DisableAsync();
        var statusDisabled = await encryption.GetStatusAsync();
        assert(!statusDisabled.Enabled, "R76 disabling encryption is reflected in status");

        var plainAgainPath = Path.Combine(dir, "plain-again.txt");
        await File.WriteAllBytesAsync(plainAgainPath, originalBytes);
        var stillPlain = await encryption.EncryptIfEnabledAsync(plainAgainPath);
        assert(stillPlain == plainAgainPath && File.Exists(plainAgainPath), "R76 disabling encryption returns new backups to plain, unencrypted files");

        // End-to-end with the real full-package backup writer + existing restore verification.
        var dataDirectory = Path.Combine(dir, "data");
        Directory.CreateDirectory(dataDirectory);
        var destination = Path.Combine(dir, "packages");
        await encryption.EnableAndGenerateRecoveryCodeAsync();
        var fullBackup = new FullBackupService(db, dataDirectory);
        var package = await fullBackup.CreateAsync(destination);
        var encryptedPackage = await encryption.EncryptIfEnabledAsync(package);
        assert(BackupEncryptionService.LooksEncrypted(encryptedPackage), "R76 a full backup package is encrypted end-to-end when enabled");

        var decryptedPackage = Path.Combine(dir, "decrypted-package.zip");
        await BackupEncryptionService.DecryptAsync(encryptedPackage, decryptedPackage, recoveryCode: null);
        var restoredTo = Path.Combine(dir, "restored-check");
        var verification = await FullBackupService.VerifyRestoreAsync(decryptedPackage, restoredTo);
        assert(verification.Files > 0, "R76 a decrypted full backup package passes the existing hash/integrity restore verification");
    }
}
