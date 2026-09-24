using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

public static class K3FactoryAdminSecurityTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert)
    {
        var oldEdition =
            Environment.GetEnvironmentVariable(
                "TOR_POS_PRODUCT_EDITION");

        var dir = Path.Combine(
            root,
            "k3-factory-admin");
        Directory.CreateDirectory(dir);

        try
        {
            Environment.SetEnvironmentVariable(
                "TOR_POS_PRODUCT_EDITION",
                "RESTAURANT",
                EnvironmentVariableTarget.Process);

            var db = await SafetyDatabase.CreateCurrentAsync(
                Path.Combine(dir, "fresh.db"));

            var auth = new AuthenticationService(
                db,
                new AuditLogRepository(db));
            await auth.InitializeAsync();

            var factoryPassword =
                await auth.LoginWithPasswordAsync(
                    "admin",
                    "admin");
            var factoryPin =
                await auth.LoginWithPinAsync(
                    "admin",
                    "1234");

            assert(
                factoryPassword.Success &&
                factoryPassword.User is
                {
                    IsAdmin: true,
                    MustChangePassword: true
                } &&
                !factoryPassword.User.Can(UserPermissions.Sale) &&
                factoryPin.Success &&
                factoryPin.User is
                {
                    IsAdmin: true,
                    MustChangePassword: true
                } &&
                !factoryPin.User.Can(UserPermissions.ZReport),
                "K-3 factory admin password/PIN create only a powerless must-change session");

            var shortAdminRejected = false;
            try
            {
                await auth.ChangeAdminCredentialsAsync(
                    "admin",
                    "123456789",
                    "4826");
            }
            catch (InvalidOperationException ex)
            {
                shortAdminRejected =
                    ex.Message.Contains(
                        "10",
                        StringComparison.Ordinal);
            }

            assert(
                shortAdminRejected,
                "K-3 admin password replacement rejects fewer than 10 characters");

            const string strongAdminPassword =
                "AdminPasswort2026!";

            await auth.ChangeAdminCredentialsAsync(
                "admin",
                strongAdminPassword,
                "4826");

            var configuredAdmin =
                await auth.LoginWithPasswordAsync(
                    "admin",
                    strongAdminPassword);
            var oldFactoryPassword =
                await auth.LoginWithPasswordAsync(
                    "admin",
                    "admin");

            assert(
                configuredAdmin.Success &&
                configuredAdmin.User is
                {
                    IsAdmin: true,
                    MustChangePassword: false
                } &&
                configuredAdmin.User.Can(UserPermissions.Sale) &&
                !oldFactoryPassword.Success,
                "K-3 strong admin credential replacement unlocks normal permissions and retires admin/admin");

            var staff = (await auth.GetStaffUsersAsync())
                .First();

            var shortStaffRejected = false;
            try
            {
                await auth.SaveStaffUserAsync(
                    new StaffUserUpdate(
                        staff.Id,
                        "k3-mitarbeiter",
                        true,
                        UserPermissions.Sale,
                        "123456789",
                        ""),
                    "admin");
            }
            catch (InvalidOperationException ex)
            {
                shortStaffRejected =
                    ex.Message.Contains(
                        "10",
                        StringComparison.Ordinal);
            }

            assert(
                shortStaffRejected,
                "K-3 newly assigned staff passwords also require at least 10 characters");

            var plusEntitlements =
                new RestaurantEntitlementService(
                    new FakeCommercialLicenseService(
                        new CommercialLicenseStatus(
                            CommercialLicenseState.Active,
                            "TEST",
                            Features: new[]
                            {
                                RestaurantEntitlementService
                                    .PlusFeatureCode
                            })));

            var operatorSessions =
                new RestaurantOperatorSessionService(
                    db,
                    plusEntitlements,
                    auth);

            var adminHandheldRejected = false;
            try
            {
                await operatorSessions.LoginAsync(
                    "K3-DEVICE",
                    "admin",
                    "4826");
            }
            catch (UnauthorizedAccessException)
            {
                adminHandheldRejected = true;
            }

            assert(
                adminHandheldRejected,
                "K-3 Restaurant operator login rejects administrators even with valid configured PIN");

            var factoryPinRejected = false;
            try
            {
                await operatorSessions.LoginAsync(
                    "K3-DEVICE",
                    "beliebig",
                    "1234");
            }
            catch (UnauthorizedAccessException ex)
            {
                factoryPinRejected =
                    ex.Message.Contains(
                        "1234",
                        StringComparison.Ordinal);
            }

            assert(
                factoryPinRejected,
                "K-3 Restaurant operator login rejects the factory PIN before creating a handheld session");

            var legacyDb =
                await SafetyDatabase.CreateCurrentAsync(
                    Path.Combine(
                        dir,
                        "legacy-staff.db"));

            var legacyStaffId =
                await InsertLegacyFixtureAsync(
                    legacyDb);

            var legacyAuth =
                new AuthenticationService(
                    legacyDb,
                    new AuditLogRepository(legacyDb));

            await legacyAuth.InitializeAsync();

            var firstState =
                await ReadLegacyStateAsync(
                    legacyDb,
                    legacyStaffId);

            var legacyLogin =
                await legacyAuth.LoginWithPinAsync(
                    "legacy-kassierer",
                    "1234");

            assert(
                !firstState.Active &&
                firstState.MustChange &&
                !firstState.CredentialsConfigured &&
                firstState.Marker &&
                !legacyLogin.Success,
                "K-3 one-time legacy migration disables untouched staff 1234 credentials and records completion");

            await legacyAuth.InitializeAsync();

            var secondState =
                await ReadLegacyStateAsync(
                    legacyDb,
                    legacyStaffId);

            assert(
                secondState.Marker &&
                firstState.PasswordHash ==
                    secondState.PasswordHash &&
                firstState.PinHash ==
                    secondState.PinHash,
                "K-3 legacy staff credential migration is marker-backed and does not rehash on every startup");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "TOR_POS_PRODUCT_EDITION",
                oldEdition,
                EnvironmentVariableTarget.Process);
        }
    }

    private static async Task<long> InsertLegacyFixtureAsync(
        SqliteDatabase db)
    {
        var adminPassword =
            AuthenticationService.HashTechnicianSecret(
                "LegacyAdmin2026!");
        var adminPin =
            AuthenticationService.HashTechnicianSecret(
                "4826");
        var staffPassword =
            AuthenticationService.HashTechnicianSecret(
                "1234");
        var staffPin =
            AuthenticationService.HashTechnicianSecret(
                "1234");

        await using var c = db.OpenConnection();
        await using var tx =
            (SqliteTransaction)await c.BeginTransactionAsync();

        await using (var admin = c.CreateCommand())
        {
            admin.Transaction = tx;
            admin.CommandText = """
                INSERT INTO users(
                    username,password_hash,password_salt,
                    pin_hash,pin_salt,
                    password_kdf,password_iterations,
                    pin_kdf,pin_iterations,
                    role,is_admin,is_active,
                    must_change_password,created_at)
                VALUES(
                    'admin',$ph,$ps,$ih,$is,
                    $kdf,$iterations,$kdf,$iterations,
                    'ADMIN',1,1,0,$created);
                """;
            admin.Parameters.AddWithValue(
                "$ph",
                adminPassword.Hash);
            admin.Parameters.AddWithValue(
                "$ps",
                adminPassword.Salt);
            admin.Parameters.AddWithValue(
                "$ih",
                adminPin.Hash);
            admin.Parameters.AddWithValue(
                "$is",
                adminPin.Salt);
            admin.Parameters.AddWithValue(
                "$kdf",
                AuthenticationService.CurrentKdfAlgorithm);
            admin.Parameters.AddWithValue(
                "$iterations",
                AuthenticationService.CurrentPbkdf2Iterations);
            admin.Parameters.AddWithValue(
                "$created",
                DateTimeOffset.Now.ToString("O"));
            await admin.ExecuteNonQueryAsync();
        }

        long staffId;
        await using (var staff = c.CreateCommand())
        {
            staff.Transaction = tx;
            staff.CommandText = """
                INSERT INTO users(
                    username,password_hash,password_salt,
                    pin_hash,pin_salt,
                    password_kdf,password_iterations,
                    pin_kdf,pin_iterations,
                    role,is_admin,is_active,
                    must_change_password,created_at)
                VALUES(
                    'legacy-kassierer',$ph,$ps,$ih,$is,
                    $kdf,$iterations,$kdf,$iterations,
                    'MITARBEITER',0,1,0,$created);
                SELECT last_insert_rowid();
                """;
            staff.Parameters.AddWithValue(
                "$ph",
                staffPassword.Hash);
            staff.Parameters.AddWithValue(
                "$ps",
                staffPassword.Salt);
            staff.Parameters.AddWithValue(
                "$ih",
                staffPin.Hash);
            staff.Parameters.AddWithValue(
                "$is",
                staffPin.Salt);
            staff.Parameters.AddWithValue(
                "$kdf",
                AuthenticationService.CurrentKdfAlgorithm);
            staff.Parameters.AddWithValue(
                "$iterations",
                AuthenticationService.CurrentPbkdf2Iterations);
            staff.Parameters.AddWithValue(
                "$created",
                DateTimeOffset.Now.ToString("O"));
            staffId = Convert.ToInt64(
                await staff.ExecuteScalarAsync());
        }

        await using (var permissions = c.CreateCommand())
        {
            permissions.Transaction = tx;
            permissions.CommandText = """
                INSERT INTO user_permissions(
                    user_id,permissions,credentials_configured)
                VALUES($id,$permissions,1);
                """;
            permissions.Parameters.AddWithValue(
                "$id",
                staffId);
            permissions.Parameters.AddWithValue(
                "$permissions",
                (long)UserPermissions.Sale);
            await permissions.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return staffId;
    }

    private static async Task<LegacyState>
        ReadLegacyStateAsync(
            SqliteDatabase db,
            long staffId)
    {
        await using var c = db.OpenConnection();

        bool active;
        bool mustChange;
        string passwordHash;
        string pinHash;
        bool configured;

        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT
                    u.is_active,
                    u.must_change_password,
                    u.password_hash,
                    u.pin_hash,
                    COALESCE(p.credentials_configured,0)
                FROM users u
                LEFT JOIN user_permissions p
                  ON p.user_id=u.id
                WHERE u.id=$id;
                """;
            q.Parameters.AddWithValue("$id", staffId);

            await using var r =
                await q.ExecuteReaderAsync();

            if (!await r.ReadAsync())
                throw new InvalidOperationException(
                    "K-3 legacy staff fixture missing.");

            active = r.GetInt32(0) == 1;
            mustChange = r.GetInt32(1) == 1;
            passwordHash = r.GetString(2);
            pinHash = r.GetString(3);
            configured = r.GetInt32(4) == 1;
        }

        bool marker;
        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT value
                FROM app_settings
                WHERE key='security.staff_default_1234_migrated.v1';
                """;
            marker = string.Equals(
                Convert.ToString(
                    await q.ExecuteScalarAsync()),
                "true",
                StringComparison.OrdinalIgnoreCase);
        }

        return new LegacyState(
            active,
            mustChange,
            configured,
            marker,
            passwordHash,
            pinHash);
    }

    private sealed record LegacyState(
        bool Active,
        bool MustChange,
        bool CredentialsConfigured,
        bool Marker,
        string PasswordHash,
        string PinHash);

    private sealed class FakeCommercialLicenseService :
        ICommercialLicenseService
    {
        private readonly CommercialLicenseStatus _status;

        public FakeCommercialLicenseService(
            CommercialLicenseStatus status)
        {
            _status = status;
        }

        public string InstallationId => "TEST";
        public string DeviceCode => "TEST";
        public string LicenseFilePath => "";

        public CommercialLicenseStatus Check(
            string edition) => _status;

        public CommercialLicenseStatus Import(
            string sourcePath,
            string edition) => _status;

        public CommercialLicenseStatus Deactivate(
            string edition,
            string deactivatedBy,
            string receiptTargetPath) => _status;

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
