using TorPos.Core;
using TorPos.Infrastructure;

public static class K3FactoryAdminSecurityTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "k3-factory-admin");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(
            Path.Combine(dir, "factory-admin.db"));

        var auth = new AuthenticationService(db);
        await auth.InitializeAsync();

        var factoryLogin =
            await auth.LoginWithPasswordAsync("admin", "admin");

        assert(
            factoryLogin.Success &&
            factoryLogin.User is { IsAdmin: true, MustChangePassword: true } &&
            !factoryLogin.User.Can(UserPermissions.Sale),
            "K-3 factory admin/admin only creates a powerless must-change session");

        var persistedMustChange = false;
        await using (var c = db.OpenReadConnection())
        await using (var q = c.CreateCommand())
        {
            q.CommandText =
                "SELECT must_change_password FROM users WHERE username='admin' COLLATE NOCASE;";
            persistedMustChange =
                Convert.ToInt32(await q.ExecuteScalarAsync()) == 1;
        }

        assert(
            persistedMustChange,
            "K-3 factory admin login persists must_change_password so alternate entry points stay locked");

        var shortPasswordRejected = false;
        try
        {
            await auth.ChangeAdminCredentialsAsync(
                "admin",
                "short123",
                "4826");
        }
        catch (InvalidOperationException)
        {
            shortPasswordRejected = true;
        }

        var factoryPinRejected = false;
        try
        {
            await auth.ChangeAdminCredentialsAsync(
                "admin",
                "SicheresPasswort10",
                "1234");
        }
        catch (InvalidOperationException)
        {
            factoryPinRejected = true;
        }

        assert(
            shortPasswordRejected && factoryPinRejected,
            "K-3 admin credential change requires at least 10 password characters and rejects factory PIN 1234");

        await auth.ChangeAdminCredentialsAsync(
            "admin",
            "SicheresPasswort10",
            "4826");

        var hardenedLogin =
            await auth.LoginWithPasswordAsync(
                "admin",
                "SicheresPasswort10");

        assert(
            hardenedLogin.Success &&
            hardenedLogin.User is { MustChangePassword: false } &&
            hardenedLogin.User.Can(UserPermissions.Sale),
            "K-3 replacing factory credentials unlocks the normal admin permission set");

        string beforeStaffHash;
        string migrationMarker;
        await using (var c = db.OpenReadConnection())
        {
            await using (var q = c.CreateCommand())
            {
                q.CommandText =
                    "SELECT password_hash FROM users WHERE is_admin=0 ORDER BY id LIMIT 1;";
                beforeStaffHash =
                    Convert.ToString(await q.ExecuteScalarAsync()) ?? "";
            }

            await using (var q = c.CreateCommand())
            {
                q.CommandText =
                    "SELECT value FROM app_settings WHERE key='security.default_staff_credentials_migrated';";
                migrationMarker =
                    Convert.ToString(await q.ExecuteScalarAsync()) ?? "";
            }
        }

        await auth.InitializeAsync();

        string afterStaffHash;
        await using (var c = db.OpenReadConnection())
        await using (var q = c.CreateCommand())
        {
            q.CommandText =
                "SELECT password_hash FROM users WHERE is_admin=0 ORDER BY id LIMIT 1;";
            afterStaffHash =
                Convert.ToString(await q.ExecuteScalarAsync()) ?? "";
        }

        assert(
            string.Equals(migrationMarker, "true", StringComparison.OrdinalIgnoreCase) &&
            beforeStaffHash.Length > 0 &&
            string.Equals(beforeStaffHash, afterStaffHash, StringComparison.Ordinal),
            "K-3 legacy default staff credential remediation is one-time and does not re-run PBKDF2 work on every startup");

        var entitlements = new RestaurantEntitlementService(
            new FakeCommercialLicenseService(
                new CommercialLicenseStatus(
                    CommercialLicenseState.Active,
                    "TEST",
                    Features: new[]
                    {
                        RestaurantEntitlementService.PlusFeatureCode
                    })));

        var fakeAuth = new FakeAuthenticationService(
            new AuthenticatedUser(
                1,
                "admin",
                "ADMIN",
                IsAdmin: true,
                MustChangePassword: false,
                UserPermissions.Sale));

        var operatorSessions =
            new RestaurantOperatorSessionService(
                db,
                entitlements,
                fakeAuth);

        var factoryHandheldPinRejected = false;
        try
        {
            await operatorSessions.LoginAsync(
                "DEVICE-K3",
                "admin",
                "1234");
        }
        catch (UnauthorizedAccessException)
        {
            factoryHandheldPinRejected = true;
        }

        assert(
            factoryHandheldPinRejected &&
            fakeAuth.PinLoginCalls == 0,
            "K-3 handheld rejects factory PIN 1234 before authentication");

        var adminHandheldRejected = false;
        try
        {
            await operatorSessions.LoginAsync(
                "DEVICE-K3",
                "admin",
                "4826");
        }
        catch (UnauthorizedAccessException)
        {
            adminHandheldRejected = true;
        }

        assert(
            adminHandheldRejected &&
            fakeAuth.PinLoginCalls == 1,
            "K-3 handheld rejects administrator accounts even with a non-factory PIN");

        var previousEdition =
            Environment.GetEnvironmentVariable(
                "TOR_POS_PRODUCT_EDITION");
        try
        {
            Environment.SetEnvironmentVariable(
                "TOR_POS_PRODUCT_EDITION",
                "RESTAURANT",
                EnvironmentVariableTarget.Process);

            var legacyDb = await SafetyDatabase.CreateCurrentAsync(
                Path.Combine(dir, "legacy-admin-session.db"));
            var legacyAuth =
                new AuthenticationService(legacyDb);
            await legacyAuth.InitializeAsync();
            await legacyAuth.ChangeAdminCredentialsAsync(
                "admin",
                "SicheresPasswort11",
                "5931");

            var rawToken =
                Convert.ToHexString(
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var tokenHash =
                Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(rawToken)));
            long adminId;

            await using (var dbWrite = legacyDb.OpenConnection())
            {
                await using (var id = dbWrite.CreateCommand())
                {
                    id.CommandText =
                        "SELECT id FROM users WHERE username='admin' COLLATE NOCASE;";
                    adminId =
                        Convert.ToInt64(await id.ExecuteScalarAsync());
                }

                await using var insert = dbWrite.CreateCommand();
                insert.CommandText = """
                    INSERT INTO restaurant_operator_sessions(
                        id,device_id,user_id,username,token_hash,is_admin,
                        permissions,created_at,expires_at,last_seen_at,revoked_at)
                    VALUES(
                        $id,'DEVICE-LEGACY',$user,'admin',$token,1,
                        $permissions,$now,$expires,$now,NULL);
                    """;
                insert.Parameters.AddWithValue(
                    "$id",
                    Guid.NewGuid().ToString("N"));
                insert.Parameters.AddWithValue(
                    "$user",
                    adminId);
                insert.Parameters.AddWithValue(
                    "$token",
                    tokenHash);
                insert.Parameters.AddWithValue(
                    "$permissions",
                    (long)UserPermissions.Sale);
                insert.Parameters.AddWithValue(
                    "$now",
                    DateTimeOffset.UtcNow.ToString("O"));
                insert.Parameters.AddWithValue(
                    "$expires",
                    DateTimeOffset.UtcNow.AddHours(1).ToString("O"));
                await insert.ExecuteNonQueryAsync();
            }

            var legacySessions =
                new RestaurantOperatorSessionService(
                    legacyDb,
                    entitlements,
                    legacyAuth);

            var legacyAdminSessionRejected = false;
            try
            {
                await legacySessions.RequireAsync(
                    "DEVICE-LEGACY",
                    rawToken);
            }
            catch (UnauthorizedAccessException)
            {
                legacyAdminSessionRejected = true;
            }

            assert(
                legacyAdminSessionRejected,
                "K-3 handheld rejects an administrator token issued by an older build even after admin credentials were hardened");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "TOR_POS_PRODUCT_EDITION",
                previousEdition,
                EnvironmentVariableTarget.Process);
        }
    }

    private sealed class FakeAuthenticationService : IAuthenticationService
    {
        private readonly AuthenticatedUser _user;
        public int PinLoginCalls { get; private set; }

        public FakeAuthenticationService(AuthenticatedUser user) =>
            _user = user;

        public Task InitializeAsync(CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<AuthenticationResult> LoginWithPasswordAsync(
            string username,
            string password,
            CancellationToken ct = default) =>
            Task.FromResult(
                new AuthenticationResult(
                    true,
                    "OK",
                    _user));

        public Task<AuthenticationResult> LoginWithPinAsync(
            string username,
            string pin,
            CancellationToken ct = default)
        {
            PinLoginCalls++;
            return Task.FromResult(
                new AuthenticationResult(
                    true,
                    "OK",
                    _user));
        }

        public Task ChangeAdminCredentialsAsync(
            string currentPassword,
            string newPassword,
            string newPin,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<StaffUser>> GetStaffUsersAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StaffUser>>(
                Array.Empty<StaffUser>());

        public Task SaveStaffUserAsync(
            StaffUserUpdate user,
            string changedBy,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeCommercialLicenseService :
        ICommercialLicenseService
    {
        private readonly CommercialLicenseStatus _status;

        public FakeCommercialLicenseService(
            CommercialLicenseStatus status) =>
            _status = status;

        public string InstallationId => "TEST";
        public string DeviceCode => "TEST";
        public string LicenseFilePath => "";

        public CommercialLicenseStatus Check(string edition) =>
            _status;

        public CommercialLicenseStatus Import(
            string sourcePath,
            string edition) =>
            _status;

        public CommercialLicenseStatus Deactivate(
            string edition,
            string deactivatedBy,
            string receiptTargetPath) =>
            _status;

        public void ExportActivationRequest(
            string targetPath,
            string edition,
            string customerNumber,
            string customerName,
            string productVersion)
        {
        }
    }
}
