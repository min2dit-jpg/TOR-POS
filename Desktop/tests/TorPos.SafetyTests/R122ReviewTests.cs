using System.Security.Cryptography;
using System.Text;
using TorPos.Core;
using TorPos.Infrastructure;

// R122: the audit's medium/low security findings.
//
// G4 - the factory admin (admin/admin, PIN 1234) had must_change_password set
//      correctly, but only the UI acted on it. Any caller that forgot the check
//      got a FULL admin session on factory credentials.
//      R182 temporarily turned the shipped access into a usable default.
//      K-3 restores the fail-closed contract: factory admin/admin or PIN 1234
//      may authenticate only far enough to reach the mandatory credential
//      replacement dialog. The resulting session remains powerless and the
//      audit trace records the unconfigured credential use.
//
// G5 - the backup recovery code was turned into a key with one unsalted
//      SHA-256. Also: a corrupt .tpe could declare any blob length it liked and
//      the restore attempt allocated it.
//
// low - the training-mode entry code was the constant "0000", printed on the
//       login screen next to its own input box.
public static class R122ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert,
        Func<Func<Task>, string, Task> reject)
    {
        // ---------- G4: an unconfigured session can do nothing ----------
        var factoryAdmin = new AuthenticatedUser(
            Id: 1, Username: "admin", Role: "ADMIN", IsAdmin: true, MustChangePassword: true);

        assert(
            !factoryAdmin.Can(UserPermissions.Sale) &&
            !factoryAdmin.Can(UserPermissions.ZReport) &&
            !factoryAdmin.Can(UserPermissions.ManageProducts),
            "R122 an admin still on the factory credentials can do nothing - IsAdmin no longer unlocks everything by itself");

        assert(
            (factoryAdmin with { MustChangePassword = false }).Can(UserPermissions.ZReport),
            "R122 the same admin regains full rights the moment the credentials are replaced");

        var unconfiguredStaff = new AuthenticatedUser(
            Id: 2, Username: "kasse1", Role: "STAFF", IsAdmin: false, MustChangePassword: true,
            Permissions: UserPermissions.Sale);
        assert(
            !unconfiguredStaff.Can(UserPermissions.Sale),
            "R122 an explicitly granted permission does not survive an unconfigured account either");

        var configuredStaff = unconfiguredStaff with { MustChangePassword = false };
        assert(
            configuredStaff.Can(UserPermissions.Sale) && !configuredStaff.Can(UserPermissions.ZReport),
            "R122 a configured staff account keeps exactly the permissions it was granted");

        // The admin login itself MUST still succeed - it is the only way to
        // reach the dialog that replaces the factory credentials.
        var dir = Path.Combine(root, "r122-auth");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r122.db"));
        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        var auth = new AuthenticationService(db, audit);
        await auth.InitializeAsync();

        var login = await auth.LoginWithPasswordAsync("admin", "admin");
        assert(
            login.Success && login.User is { IsAdmin: true, MustChangePassword: true },
            "K-3 the shipped admin access authenticates only into mandatory credential replacement");
        assert(
            login.User is not null && !login.User.Can(UserPermissions.Sale),
            "K-3 factory admin credentials cannot authorize normal POS work before replacement");

        using (var c = db.OpenConnection())
        {
            using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM audit_log WHERE event_type='ADMIN_LOGIN_CREDENTIALS_UNCONFIGURED';";
            assert(
                Convert.ToInt64(q.ExecuteScalar()) >= 1,
                "R122 an admin session running on factory credentials leaves a trace in the audit log");
        }

        // ---------- training access code ----------
        assert(
            TrainingAccessPolicy.Effective(null) == "0000" &&
            TrainingAccessPolicy.IsFactoryDefault(""),
            "R122 an installation that never set a training code keeps the shipped 0000, so nobody is locked out by the upgrade");

        assert(
            TrainingAccessPolicy.Matches("7391", "7391") &&
            !TrainingAccessPolicy.Matches("7391", "0000"),
            "R122 a configured training code replaces 0000 rather than being accepted alongside it");

        assert(
            TrainingAccessPolicy.Effective("abc") == "0000" &&
            TrainingAccessPolicy.Effective("12") == "0000" &&
            !TrainingAccessPolicy.IsValidCode("12a4"),
            "R122 a malformed stored code falls back to the factory code instead of accepting anything");

        assert(
            !TrainingAccessPolicy.IsFactoryDefault("7391"),
            "R122 once an own code is set the login screen no longer names it");

        // ---------- G5: salted PBKDF2 for the backup recovery code ----------
        var backupDir = Path.Combine(root, "r122-backup");
        Directory.CreateDirectory(backupDir);
        var encryption = new BackupEncryptionService(settings);

        var payload = Encoding.UTF8.GetBytes("TOR POS Sicherung · äöüß · " + Guid.NewGuid());
        var plainPath = Path.Combine(backupDir, "plain.bin");
        await File.WriteAllBytesAsync(plainPath, payload);

        var code = await encryption.EnableAndGenerateRecoveryCodeAsync();
        var container = await encryption.EncryptIfEnabledAsync(plainPath);

        var header = await File.ReadAllBytesAsync(container);
        assert(
            header[4] == 2,
            $"R122 a newly written container declares format 2 (salted PBKDF2), not the unsalted format 1 (actual: {header[4]})");

        var status = await encryption.GetStatusAsync();
        assert(
            !status.UsesLegacyKeyDerivation,
            "R122 a freshly generated recovery code is not reported as legacy");

        var restored = Path.Combine(backupDir, "restored.bin");
        await BackupEncryptionService.DecryptAsync(container, restored, recoveryCode: code);
        assert(
            (await File.ReadAllBytesAsync(restored)).SequenceEqual(payload),
            "R122 the recovery code still restores the exact original bytes through the new derivation");

        // The salt's real job: two installations that happened to receive the
        // same code must not end up with the same key material.
        var otherDb = await SafetyDatabase.CreateCurrentAsync(Path.Combine(backupDir, "other.db"));
        var otherSettings = new SettingsRepository(otherDb);
        var otherEncryption = new BackupEncryptionService(otherSettings);
        await otherEncryption.EnableAndGenerateRecoveryCodeAsync();
        assert(
            !await otherEncryption.VerifyRecoveryCodeAsync(code),
            "R122 a recovery code from one installation does not verify against another");

        // ---------- G5: every backup already in a customer's hands still restores ----------
        var legacyCode = "ABCD-EF01-2345-6789";
        var legacyContainer = Path.Combine(backupDir, "legacy.tpe");
        WriteLegacyContainer(legacyContainer, payload, legacyCode);

        var legacyRestored = Path.Combine(backupDir, "legacy-restored.bin");
        await BackupEncryptionService.DecryptAsync(legacyContainer, legacyRestored, recoveryCode: legacyCode);
        assert(
            (await File.ReadAllBytesAsync(legacyRestored)).SequenceEqual(payload),
            "R122 a format-1 backup written before this release still restores with its original recovery code");

        await reject(
            () => BackupEncryptionService.DecryptAsync(
                legacyContainer, Path.Combine(backupDir, "nope.bin"), recoveryCode: "FFFF-FFFF-FFFF-FFFF"),
            "R122 a wrong code against a legacy container still fails instead of producing garbage");

        // An installation whose code predates R122 is reported so the Settings
        // page can offer the one action that upgrades it.
        var legacyDb = await SafetyDatabase.CreateCurrentAsync(Path.Combine(backupDir, "legacy-install.db"));
        var legacySettings = new SettingsRepository(legacyDb);
        await legacySettings.SaveManyAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["backup.encryption.enabled"] = "true",
            ["backup.encryption.recovery_fingerprint"] = LegacyFingerprint(legacyCode)
        });
        var legacyStatus = await new BackupEncryptionService(legacySettings).GetStatusAsync();
        assert(
            legacyStatus is { Enabled: true, UsesLegacyKeyDerivation: true },
            "R122 an installation set up before this release is reported as using the old derivation, not silently left as-is");
        assert(
            await new BackupEncryptionService(legacySettings).VerifyRecoveryCodeAsync(legacyCode),
            "R122 that installation's existing recovery code still verifies");

        // ---------- a damaged .tpe fails with a message, not an allocation ----------
        var hostile = Path.Combine(backupDir, "hostile.tpe");
        var bytes = await File.ReadAllBytesAsync(legacyContainer);
        // dpapiLen sits right after magic(4) + version(1) + nonce(12) + tag(16).
        BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, 4 + 1 + 12 + 16);
        await File.WriteAllBytesAsync(hostile, bytes);

        await reject(
            () => BackupEncryptionService.DecryptAsync(
                hostile, Path.Combine(backupDir, "never.bin"), recoveryCode: legacyCode),
            "R122 a container declaring an absurd key length is rejected as damaged instead of allocating it");

        var truncated = Path.Combine(backupDir, "truncated.tpe");
        await File.WriteAllBytesAsync(truncated, bytes.Take(20).ToArray());
        await reject(
            () => BackupEncryptionService.DecryptAsync(
                truncated, Path.Combine(backupDir, "never2.bin"), recoveryCode: legacyCode),
            "R122 a truncated container is rejected as incomplete rather than read past its end");

        // ---------- F5: the digital receipt checks the same fields the printed one does ----------
        // R145: the digital receipt is built from the print job itself.
        var line = new CartLine { ProductName = "R122 Artikel", Quantity = 1, UnitPriceCents = 1190, VatRate = 19m };

        DigitalReceiptDocument DocumentWith(string transactionNumber, bool outage, DateTimeOffset? logTime, string address = "Musterstr. 1, 10115 Berlin") =>
            DigitalReceiptDocument.From(
                new ReceiptPrintJob(
                    122001, DateTimeOffset.Now, "R122 Laden", address, "", "", "", "", "Bar", 0, 1190, new[] { line },
                    FiscalTestMode: false,
                    EasSerial: "TORPOS-R122",
                    TseSerial: outage ? "" : "TSE-122",
                    TseTransactionNumber: transactionNumber,
                    SignatureCounter: outage ? 0 : 42,
                    ProcessStart: DateTimeOffset.Now.AddSeconds(-10),
                    ProcessEnd: logTime,
                    VerificationValue: outage ? "" : "SIG-122",
                    TseOutage: outage),
                DigitalReceiptDocument.PaymentsFor(PaymentMethod.Cash, 1190, 0));

        var complete = DocumentWith("4711", outage: false, logTime: DateTimeOffset.Now);
        assert(
            complete.MissingFields.Count == 0 && complete.Notes.Contains(DigitalReceiptDocument.CompleteNote),
            "R122 a fully signed sale still renders the §6 KassenSichV statement with no warning");

        var incomplete = DocumentWith("", outage: false, logTime: DateTimeOffset.Now);
        assert(
            incomplete.MissingFields.Contains("Transaktionsnummer"),
            "R122 a digital receipt missing its Transaktionsnummer says so instead of silently leaving the field out");
        assert(
            !incomplete.Notes.Contains(DigitalReceiptDocument.CompleteNote),
            "R122 ... and stops claiming §6 KassenSichV compliance it cannot show - this is the actual F5 defect");

        var outageDocument = DocumentWith("", outage: true, logTime: null);
        assert(
            outageDocument.Notes.Contains(DigitalReceiptDocument.OutageNote) && outageDocument.MissingFields.Count == 0,
            "R122 a genuine TSE outage is explained by the outage note, not reported as missing fields");

        var missingCompanyAddress = DocumentWith("4711", outage: false, logTime: DateTimeOffset.Now, address: "");
        assert(
            missingCompanyAddress.MissingFields.Contains("Anschrift"),
            "R122 the digital receipt also notices a missing company address, exactly as the printer does");

        // Both consumers really do share one list - a field the printer demands
        // is a field the digital page demands.
        var printerMissing = FiscalReceiptFields.Missing(
            "Laden", "Anschrift", "EAS", tseOutage: false,
            tseSerial: "TSE", tseTransactionNumber: "", hasSignatureCounter: true,
            verificationValue: "SIG", hasProcessStart: true, hasProcessEnd: true);
        assert(
            printerMissing.Count == 1 && printerMissing[0] == "Transaktionsnummer",
            "R122 the shared mandatory-field rule names exactly the field that is missing");

        var outageMissing = FiscalReceiptFields.Missing(
            "Laden", "Anschrift", "EAS", tseOutage: true,
            tseSerial: "", tseTransactionNumber: "", hasSignatureCounter: false,
            verificationValue: "", hasProcessStart: true, hasProcessEnd: false);
        assert(
            outageMissing.Count == 0,
            "R122 during an outage the TSE-generated fields are not demanded - inventing them is what R121 removed");

        var noStart = FiscalReceiptFields.Missing(
            "Laden", "Anschrift", "EAS", tseOutage: true,
            tseSerial: "", tseTransactionNumber: "", hasSignatureCounter: false,
            verificationValue: "", hasProcessStart: false, hasProcessEnd: false);
        assert(
            noStart.Contains("Vorgangsbeginn"),
            "R122 Vorgangsbeginn stays mandatory even during an outage - it is the register's own record, not the TSE's");

        // ---------- F6: the TSE record is final, and says so ----------
        var fiscalDir = Path.Combine(root, "r122-fiscal");
        Directory.CreateDirectory(fiscalDir);
        var fiscalDb = await SafetyDatabase.CreateCurrentAsync(Path.Combine(fiscalDir, "r122-fiscal.db"));
        var saleRepo = new SaleRepository(fiscalDb);

        long signedSaleId;
        using (var c = fiscalDb.OpenConnection())
        using (var q = c.CreateCommand())
        {
            q.CommandText = """
                INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,cash_portion_cents,card_portion_cents)
                VALUES(122099,$now,'CASH',500,500,'TEST_TSE_NOT_CONNECTED','SALE',500,0);
                SELECT last_insert_rowid();
                """;
            q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
            signedSaleId = Convert.ToInt64(q.ExecuteScalar());
        }

        await saleRepo.RecordTseResultAsync(signedSaleId, SaleTseResult.Outage("TSE nicht erreichbar"));

        using (var c = fiscalDb.OpenConnection())
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT outage FROM sale_tse_signatures WHERE sale_id=$id;";
            q.Parameters.AddWithValue("$id", signedSaleId);
            assert(
                Convert.ToInt64(q.ExecuteScalar()) == 1,
                "R122 an outage DOES write a record - the migration comment claiming it never gets a row was wrong, and that record is what puts the TSE-AUSFALL note on the receipt");
        }

        await reject(
            () => saleRepo.RecordTseResultAsync(signedSaleId, SaleTseResult.Outage("zweiter Versuch")),
            "R122 a second TSE result for the same sale is refused with a stated rule, not a raw UNIQUE-constraint database error");

        // ---------- İ6: the dead method with the wrong period rule is gone ----------
        assert(
            typeof(ISaleRepository).GetMethod("GetDailySinceLastZAsync") is null,
            "R122 GetDailySinceLastZAsync is removed - it had no caller and started its period at midnight, the very bug R117 fixed elsewhere");
    }

    /// <summary>
    /// Builds a format-1 container exactly the way releases before R122 wrote
    /// them: unsalted SHA-256 over the normalized code, and no DPAPI blob (so
    /// the restore path is forced through the recovery code, which is the part
    /// that has to keep working on a replacement machine).
    /// </summary>
    private static void WriteLegacyContainer(string path, byte[] payload, string recoveryCode)
    {
        var kek = LegacyKek(recoveryCode);
        var dek = RandomNumberGenerator.GetBytes(32);

        var payloadNonce = RandomNumberGenerator.GetBytes(12);
        var payloadTag = new byte[16];
        var payloadCipher = new byte[payload.Length];
        using (var aes = new AesGcm(dek, 16))
            aes.Encrypt(payloadNonce, payload, payloadCipher, payloadTag);

        var recoveryNonce = RandomNumberGenerator.GetBytes(12);
        var recoveryTag = new byte[16];
        var wrappedDek = new byte[32];
        using (var aes = new AesGcm(kek, 16))
            aes.Encrypt(recoveryNonce, dek, wrappedDek, recoveryTag);

        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(output);
        writer.Write(Encoding.ASCII.GetBytes("TPEB"));
        writer.Write((byte)1);
        writer.Write(payloadNonce);
        writer.Write(payloadTag);
        writer.Write(0);                 // no DPAPI blob
        writer.Write(recoveryNonce);
        writer.Write(recoveryTag);
        writer.Write(wrappedDek);
        writer.Write(Convert.FromHexString(LegacyFingerprint(recoveryCode)));
        writer.Write(payloadCipher);
    }

    private static byte[] LegacyKek(string recoveryCode) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(
            new string(recoveryCode.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant()));

    private static string LegacyFingerprint(string recoveryCode) =>
        Convert.ToHexString(SHA256.HashData(LegacyKek(recoveryCode))).AsSpan(0, 8).ToString();
}
