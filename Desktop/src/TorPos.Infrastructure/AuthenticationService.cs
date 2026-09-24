using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed class AuthenticationService : IAuthenticationService
{
    public const string CurrentKdfAlgorithm = "PBKDF2-SHA256";
    public const int CurrentPbkdf2Iterations = 600_000;

    private const int LegacyPbkdf2Iterations = 180_000;
    private const int MaximumAcceptedPbkdf2Iterations = 5_000_000;
    private const int SaltBytes = 32;
    private const int HashBytes = 32;
    // R118: the lockout schedule moved to TorPos.Core.LoginLockoutPolicy so
    // the escalation can be asserted directly in the safety suite.

    private readonly SqliteDatabase _db;
    private readonly IAuditLog? _audit;

    // R182: the shipped access stays usable, so "running on factory credentials"
    // is no longer a stored flag that disables the session. It is detected at
    // login and still leaves the audit trace the Verfahrensdokumentation relies on.
    public const int MinimumPasswordLength = 10;
    private const string FactoryAdminPassword = "admin";
    private const string FactoryAdminPin = "1234";
    private const string LegacyStaffCredentialMigrationKey =
        "security.staff_default_1234_migrated.v1";

    public static bool IsFactoryPin(string? pin) =>
        string.Equals(
            (pin ?? "").Trim(),
            FactoryAdminPin,
            StringComparison.Ordinal);

    public AuthenticationService(SqliteDatabase db, IAuditLog? audit = null)
    {
        _db = db;
        _audit = audit;
    }
public async Task InitializeAsync(CancellationToken ct = default)
{
    await IoQueue.RunAsync(async () =>
    {
        await using var c = _db.OpenConnection();
        await using var count = c.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM users;";
        var hasUsers = Convert.ToInt32(await count.ExecuteScalarAsync(ct)) > 0;
        if (!hasUsers)
        {
            var bootstrap = ReadBootstrapAndDelete();
            var password = string.IsNullOrWhiteSpace(bootstrap.Password) ? "admin" : bootstrap.Password;
            var pin = IsValidPin(bootstrap.Pin) ? bootstrap.Pin : "1234";
            // Factory credentials exist only to reach the mandatory credential
            // setup dialog. The resulting admin session stays powerless until
            // the password/PIN pair has been replaced.
            await CreateAdminAsync(c, "admin", password, pin, mustChangePassword: true, ct);
        }
        else
        {
            // Never keep reversible bootstrap credentials after security is initialized.
            TryDelete(AppPaths.FirstRunAdminPath);
        }

        await EnsurePermissionRowsAsync(c, ct);
        await EnsureThreeStaffUsersAsync(c, ct);
        await MigrateLegacyDefaultStaffCredentialsOnceAsync(c, ct);
        try
        {
            File.WriteAllText(AppPaths.SecurityInitializedPath, DateTimeOffset.Now.ToString("O"));
        }
        catch
        {
        // Marker is installer convenience only; authentication must still work.
        }
    });
}public async Task<AuthenticationResult> LoginWithPasswordAsync(string username, string password, CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return new AuthenticationResult(false, "Benutzername und Passwort sind erforderlich.");
        await using var c = _db.OpenConnection();
        var row = await ReadUserAsync(c, username.Trim(), ct);
        if (row is null || !row.IsActive)
            return new AuthenticationResult(false, "Anmeldung fehlgeschlagen.");
        if (IsLocked(row, out var remaining))
        {
            await WriteAuditSafeAsync(row.Username, "LOGIN_LOCKED", ct);
            return new AuthenticationResult(false, $"Konto vorübergehend gesperrt. Bitte in {remaining} Min. erneut versuchen.");
        }

        var ok = VerifySecret(
            password,
            row.PasswordSalt,
            row.PasswordHash,
            row.PasswordKdf,
            row.PasswordIterations);
        if (!ok)
        {
            await RegisterFailedAttemptAsync(c, row.Id, ct);
            await WriteAuditSafeAsync(username.Trim(), "LOGIN_FAILED", ct);
            return new AuthenticationResult(false, "Benutzername oder Passwort ist falsch.");
        }

        if (!row.IsAdmin && row.MustChangePassword)
            return new AuthenticationResult(false, "Mitarbeiter-Zugang noch nicht eingerichtet. Bitte durch Administrator konfigurieren.");

        var factoryAdminCredentials =
            row.IsAdmin &&
            UsesFactoryAdminCredentials(row);

        if (factoryAdminCredentials && !row.MustChangePassword)
            await MarkMustChangePasswordAsync(c, row.Id, ct);

        if (row.IsAdmin &&
            (row.MustChangePassword || factoryAdminCredentials))
            await WriteAuditSafeAsync(
                row.Username,
                "ADMIN_LOGIN_CREDENTIALS_UNCONFIGURED",
                ct);

        var passwordUpgraded = await TryUpgradePasswordKdfAsync(c, row, password, ct);
        await RegisterSuccessfulLoginAsync(c, row.Id, ct);
        if (passwordUpgraded)
            await WriteAuditSafeAsync(row.Username, "PASSWORD_KDF_UPGRADED", $"algorithm={CurrentKdfAlgorithm}; iterations={CurrentPbkdf2Iterations}", ct);
        await WriteAuditSafeAsync(row.Username, "LOGIN_OK", ct);

        var authenticated = ToAuthenticated(row);
        if (factoryAdminCredentials)
            authenticated = authenticated with { MustChangePassword = true };

        return new AuthenticationResult(
            true,
            "Anmeldung erfolgreich.",
            authenticated);
    });
}public async Task<AuthenticationResult> LoginWithPinAsync(string username, string pin, CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(username) || !IsValidPin(pin))
            return new AuthenticationResult(false, "Benutzername und 4-stellige PIN sind erforderlich.");
        await using var c = _db.OpenConnection();
        var row = await ReadUserAsync(c, username.Trim(), ct);
        if (row is null || !row.IsActive)
            return new AuthenticationResult(false, "Anmeldung fehlgeschlagen.");
        if (IsLocked(row, out var remaining))
        {
            await WriteAuditSafeAsync(row.Username, "PIN_LOGIN_LOCKED", ct);
            return new AuthenticationResult(false, $"Konto vorübergehend gesperrt. Bitte in {remaining} Min. erneut versuchen.");
        }

        var ok = VerifySecret(
            pin,
            row.PinSalt,
            row.PinHash,
            row.PinKdf,
            row.PinIterations);
        if (!ok)
        {
            await RegisterFailedAttemptAsync(c, row.Id, ct);
            await WriteAuditSafeAsync(username.Trim(), "PIN_LOGIN_FAILED", ct);
            return new AuthenticationResult(false, "Benutzername oder PIN ist falsch.");
        }

        if (!row.IsAdmin && row.MustChangePassword)
            return new AuthenticationResult(false, "Mitarbeiter-Zugang noch nicht eingerichtet. Bitte durch Administrator konfigurieren.");

        var factoryAdminCredentials =
            row.IsAdmin &&
            UsesFactoryAdminCredentials(row);

        if (factoryAdminCredentials && !row.MustChangePassword)
            await MarkMustChangePasswordAsync(c, row.Id, ct);

        if (row.IsAdmin &&
            (row.MustChangePassword || factoryAdminCredentials))
            await WriteAuditSafeAsync(
                row.Username,
                "ADMIN_LOGIN_CREDENTIALS_UNCONFIGURED",
                ct);

        var pinUpgraded = await TryUpgradePinKdfAsync(c, row, pin, ct);
        await RegisterSuccessfulLoginAsync(c, row.Id, ct);
        if (pinUpgraded)
            await WriteAuditSafeAsync(row.Username, "PIN_KDF_UPGRADED", $"algorithm={CurrentKdfAlgorithm}; iterations={CurrentPbkdf2Iterations}", ct);
        await WriteAuditSafeAsync(row.Username, "PIN_LOGIN_OK", ct);

        var authenticated = ToAuthenticated(row);
        if (factoryAdminCredentials)
            authenticated = authenticated with { MustChangePassword = true };

        return new AuthenticationResult(
            true,
            "Anmeldung erfolgreich.",
            authenticated);
    });
}public async Task ChangeAdminCredentialsAsync(string currentPassword, string newPassword, string newPin, CancellationToken ct = default)
{
    await IoQueue.RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(newPassword) ||
            newPassword.Length < MinimumPasswordLength)
        {
            throw new InvalidOperationException(
                $"Neues Passwort muss mindestens {MinimumPasswordLength} Zeichen haben.");
        }

        if (!IsValidPin(newPin))
            throw new InvalidOperationException("PIN muss genau 4 Ziffern haben.");

        if (IsFactoryPin(newPin))
            throw new InvalidOperationException("Die Standard-PIN 1234 darf nicht weiterverwendet werden.");
        var login = await LoginWithPasswordAsync("admin", currentPassword, ct);
        if (!login.Success)
            throw new InvalidOperationException("Aktuelles Admin-Passwort ist falsch.");
        var passwordSecret = HashSecret(newPassword);
        var pinSecret = HashSecret(newPin);
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            UPDATE users SET
              password_hash=$ph,
              password_salt=$ps,
              password_kdf=$kdf,
              password_iterations=$iterations,
              pin_hash=$ih,
              pin_salt=$is,
              pin_kdf=$kdf,
              pin_iterations=$iterations,
              must_change_password=0
            WHERE username='admin' COLLATE NOCASE;
            """;
        q.Parameters.AddWithValue("$ph", passwordSecret.Hash);
        q.Parameters.AddWithValue("$ps", passwordSecret.Salt);
        q.Parameters.AddWithValue("$ih", pinSecret.Hash);
        q.Parameters.AddWithValue("$is", pinSecret.Salt);
        q.Parameters.AddWithValue("$kdf", CurrentKdfAlgorithm);
        q.Parameters.AddWithValue("$iterations", CurrentPbkdf2Iterations);
        await q.ExecuteNonQueryAsync(ct);
    });
}public async Task<AuthenticationKdfStatus> GetKdfStatusAsync(CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT password_kdf,password_iterations,pin_kdf,pin_iterations
            FROM users;
            """;
        await using var r = await q.ExecuteReaderAsync(ct);

        var users = 0;
        var passwordLegacy = 0;
        var passwordUnsupported = 0;
        var pinLegacy = 0;
        var pinUnsupported = 0;

        while (await r.ReadAsync(ct))
        {
            users++;
            Classify(r.GetString(0), r.GetInt32(1), ref passwordLegacy, ref passwordUnsupported);
            Classify(r.GetString(2), r.GetInt32(3), ref pinLegacy, ref pinUnsupported);
        }

        return new AuthenticationKdfStatus(
            users,
            CurrentKdfAlgorithm,
            CurrentPbkdf2Iterations,
            passwordLegacy,
            passwordUnsupported,
            pinLegacy,
            pinUnsupported);
    });

    static void Classify(string algorithm, int iterations, ref int legacy, ref int unsupported)
    {
        if (!string.Equals(algorithm, CurrentKdfAlgorithm, StringComparison.OrdinalIgnoreCase) ||
            iterations < LegacyPbkdf2Iterations ||
            iterations > MaximumAcceptedPbkdf2Iterations)
        {
            unsupported++;
            return;
        }

        if (iterations < CurrentPbkdf2Iterations)
            legacy++;
    }
}public async Task<IReadOnlyList<StaffUser>> GetStaffUsersAsync(CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var result = new List<StaffUser>();
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT u.id,u.username,u.is_active,
                   COALESCE(p.credentials_configured,0),
                   COALESCE(p.permissions,0)
            FROM users u
            LEFT JOIN user_permissions p ON p.user_id=u.id
            WHERE u.is_admin=0
            ORDER BY u.id
            LIMIT 3;
            """;
        await using var r = await q.ExecuteReaderAsync(ct);
        var slot = 0;
        while (await r.ReadAsync(ct))
        {
            slot++;
            result.Add(new StaffUser(r.GetInt64(0), slot, r.GetString(1), r.GetInt64(2) == 1, r.GetInt64(3) == 1, (UserPermissions)r.GetInt64(4)));
        }

        return result;
    });
}public async Task SaveStaffUserAsync(StaffUserUpdate user, string changedBy, CancellationToken ct = default)
{
    await IoQueue.RunAsync(async () =>
    {
        var username = (user.Username ?? "").Trim();
        if (username.Length < 2)
            throw new InvalidOperationException("Benutzername muss mindestens 2 Zeichen haben.");
        var password = user.NewPassword ?? "";
        var pin = user.NewPin ?? "";
        if (password.Length > 0 &&
            password.Length < MinimumPasswordLength)
        {
            throw new InvalidOperationException(
                $"Das neue Passwort muss mindestens {MinimumPasswordLength} Zeichen haben.");
        }
        if (pin.Length > 0 && !IsValidPin(pin))
            throw new InvalidOperationException("Die neue PIN muss genau 4 Ziffern haben.");
        if (pin.Length > 0 && IsFactoryPin(pin))
            throw new InvalidOperationException("Die Standard-PIN 1234 darf nicht weiterverwendet werden.");
        await using var c = _db.OpenConnection();
        bool configured;
        await using (var state = c.CreateCommand())
        {
            state.CommandText = """
                SELECT COALESCE(p.credentials_configured,0)
                FROM users u
                LEFT JOIN user_permissions p ON p.user_id=u.id
                WHERE u.id=$id AND u.is_admin=0;
                """;
            state.Parameters.AddWithValue("$id", user.Id);
            var value = await state.ExecuteScalarAsync(ct);
            if (value is null)
                throw new InvalidOperationException("Mitarbeiterkonto wurde nicht gefunden.");
            configured = Convert.ToInt64(value) == 1;
        }

        if (user.IsActive && !configured && password.Length == 0 && pin.Length == 0)
        {
            throw new InvalidOperationException(
                "Vor der Aktivierung muss mindestens ein Passwort oder eine 4-stellige PIN vergeben werden.");
        }

        await using (var duplicate = c.CreateCommand())
        {
            duplicate.CommandText = """
                SELECT COUNT(*)
                FROM users
                WHERE username=$username COLLATE NOCASE
                  AND id<>$id;
                """;
            duplicate.Parameters.AddWithValue("$username", username);
            duplicate.Parameters.AddWithValue("$id", user.Id);
            if (Convert.ToInt32(await duplicate.ExecuteScalarAsync(ct)) > 0)
                throw new InvalidOperationException("Dieser Benutzername wird bereits verwendet.");
        }

        await using var tx = await c.BeginTransactionAsync(ct);
        await using (var update = c.CreateCommand())
        {
            update.Transaction = (SqliteTransaction)tx;
            update.CommandText = """
                UPDATE users
                SET username=$username,is_active=$active,role='MITARBEITER'
                WHERE id=$id AND is_admin=0;
                """;
            update.Parameters.AddWithValue("$username", username);
            update.Parameters.AddWithValue("$active", user.IsActive ? 1 : 0);
            update.Parameters.AddWithValue("$id", user.Id);
            if (await update.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Mitarbeiterkonto wurde nicht gefunden.");
        }

        // R162: the first activation may intentionally use only one login method.
        // The untouched factory secret for the other method must never remain usable.
        // Replace it with a cryptographically random, unknowable value.
        var passwordToStore = password;
        var pinToStore = pin;
        if (!configured)
        {
            if (passwordToStore.Length == 0 && pinToStore.Length > 0)
                passwordToStore = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            if (pinToStore.Length == 0 && passwordToStore.Length > 0)
                pinToStore = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        }

        if (passwordToStore.Length > 0)
        {
            var secret = HashSecret(passwordToStore);
            await using var update = c.CreateCommand();
            update.Transaction = (SqliteTransaction)tx;
            update.CommandText = """
                UPDATE users SET
                    password_hash=$hash,
                    password_salt=$salt,
                    password_kdf=$kdf,
                    password_iterations=$iterations
                WHERE id=$id AND is_admin=0;
                """;
            update.Parameters.AddWithValue("$hash", secret.Hash);
            update.Parameters.AddWithValue("$salt", secret.Salt);
            update.Parameters.AddWithValue("$kdf", CurrentKdfAlgorithm);
            update.Parameters.AddWithValue("$iterations", CurrentPbkdf2Iterations);
            update.Parameters.AddWithValue("$id", user.Id);
            await update.ExecuteNonQueryAsync(ct);
        }

        if (pinToStore.Length > 0)
        {
            var secret = HashSecret(pinToStore);
            await using var update = c.CreateCommand();
            update.Transaction = (SqliteTransaction)tx;
            update.CommandText = """
                UPDATE users SET
                    pin_hash=$hash,
                    pin_salt=$salt,
                    pin_kdf=$kdf,
                    pin_iterations=$iterations
                WHERE id=$id AND is_admin=0;
                """;
            update.Parameters.AddWithValue("$hash", secret.Hash);
            update.Parameters.AddWithValue("$salt", secret.Salt);
            update.Parameters.AddWithValue("$kdf", CurrentKdfAlgorithm);
            update.Parameters.AddWithValue("$iterations", CurrentPbkdf2Iterations);
            update.Parameters.AddWithValue("$id", user.Id);
            await update.ExecuteNonQueryAsync(ct);
        }

        var nowConfigured = configured || password.Length > 0 || pin.Length > 0;
        if (nowConfigured)
        {
            await using var ready = c.CreateCommand();
            ready.Transaction = (SqliteTransaction)tx;
            ready.CommandText = "UPDATE users SET must_change_password=0,failed_attempts=0,locked_until='' WHERE id=$id AND is_admin=0;";
            ready.Parameters.AddWithValue("$id", user.Id);
            await ready.ExecuteNonQueryAsync(ct);
        }

        await using (var permissions = c.CreateCommand())
        {
            permissions.Transaction = (SqliteTransaction)tx;
            permissions.CommandText = """
                INSERT INTO user_permissions(user_id,permissions,credentials_configured)
                VALUES($id,$permissions,$configured)
                ON CONFLICT(user_id) DO UPDATE SET
                  permissions=excluded.permissions,
                  credentials_configured=excluded.credentials_configured;
                """;
            permissions.Parameters.AddWithValue("$id", user.Id);
            permissions.Parameters.AddWithValue("$permissions", (long)user.Permissions);
            permissions.Parameters.AddWithValue("$configured", nowConfigured ? 1 : 0);
            await permissions.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        await WriteAuditSafeAsync(changedBy, "STAFF_USER_UPDATED", $"user_id={user.Id}; username={username}; active={user.IsActive}; permissions={(long)user.Permissions}", ct);
    });
}
    private static async Task CreateAdminAsync(
        SqliteConnection c,
        string username,
        string password,
        string pin,
        bool mustChangePassword,
        CancellationToken ct)
    {
        var passwordSecret = HashSecret(password);
        var pinSecret = HashSecret(pin);

        await using var q = c.CreateCommand();
        q.CommandText = """
            INSERT INTO users(
              username,password_hash,password_salt,pin_hash,pin_salt,
              role,is_admin,is_active,must_change_password,created_at)
            VALUES(
              $u,$ph,$ps,$ih,$is,
              'ADMIN',1,1,$must,$created);
            """;
        q.Parameters.AddWithValue("$u", username);
        q.Parameters.AddWithValue("$ph", passwordSecret.Hash);
        q.Parameters.AddWithValue("$ps", passwordSecret.Salt);
        q.Parameters.AddWithValue("$ih", pinSecret.Hash);
        q.Parameters.AddWithValue("$is", pinSecret.Salt);
        q.Parameters.AddWithValue("$must", mustChangePassword ? 1 : 0);
        q.Parameters.AddWithValue("$created", DateTimeOffset.Now.ToString("O"));
        await q.ExecuteNonQueryAsync(ct);
    }

    private static async Task EnsurePermissionRowsAsync(
        SqliteConnection c,
        CancellationToken ct)
    {
        await using var q = c.CreateCommand();
        q.CommandText = """
            INSERT OR IGNORE INTO user_permissions(
              user_id,permissions,credentials_configured)
            SELECT id,0,1 FROM users;
            """;
        await q.ExecuteNonQueryAsync(ct);
    }

    private static async Task EnsureThreeStaffUsersAsync(
        SqliteConnection c,
        CancellationToken ct)
    {
        int count;
        await using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT COUNT(*) FROM users WHERE is_admin=0;";
            count = Convert.ToInt32(await q.ExecuteScalarAsync(ct));
        }

        var defaultPermissions =
            UserPermissions.Sale |
            UserPermissions.ParkReceipts |
            UserPermissions.ViewReceiptHistory |
            UserPermissions.Training;

        for (var slot = count + 1; slot <= 3; slot++)
        {
            // New staff slots must never contain a known login secret. They are
            // inactive until the administrator assigns a password and/or PIN.
            var passwordSecret = HashSecret(
                Convert.ToBase64String(
                    RandomNumberGenerator.GetBytes(32)));
            var pinSecret = HashSecret(
                Convert.ToBase64String(
                    RandomNumberGenerator.GetBytes(32)));
            var username = $"kassierer{slot}";

            await using (var nameCheck = c.CreateCommand())
            {
                nameCheck.CommandText = "SELECT COUNT(*) FROM users WHERE username=$username COLLATE NOCASE;";
                nameCheck.Parameters.AddWithValue("$username", username);
                if (Convert.ToInt32(await nameCheck.ExecuteScalarAsync(ct)) > 0)
                    username = $"mitarbeiter-{Guid.NewGuid():N}"[..20];
            }

            long id;
            await using (var insert = c.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO users(
                      username,password_hash,password_salt,pin_hash,pin_salt,
                      role,is_admin,is_active,must_change_password,created_at)
                    VALUES($username,$ph,$ps,$ih,$is,'MITARBEITER',0,0,1,$created);
                    SELECT last_insert_rowid();
                    """;
                insert.Parameters.AddWithValue("$username", username);
                insert.Parameters.AddWithValue("$ph", passwordSecret.Hash);
                insert.Parameters.AddWithValue("$ps", passwordSecret.Salt);
                insert.Parameters.AddWithValue("$ih", pinSecret.Hash);
                insert.Parameters.AddWithValue("$is", pinSecret.Salt);
                insert.Parameters.AddWithValue("$created", DateTimeOffset.Now.ToString("O"));
                id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
            }

            await using var permission = c.CreateCommand();
            permission.CommandText = """
                INSERT INTO user_permissions(user_id,permissions,credentials_configured)
                VALUES($id,$permissions,0);
                """;
            permission.Parameters.AddWithValue("$id", id);
            permission.Parameters.AddWithValue("$permissions", (long)defaultPermissions);
            await permission.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task MigrateLegacyDefaultStaffCredentialsOnceAsync(
        SqliteConnection c,
        CancellationToken ct)
    {
        await using (var marker = c.CreateCommand())
        {
            marker.CommandText =
                "SELECT value FROM app_settings WHERE key=$key;";
            marker.Parameters.AddWithValue(
                "$key",
                LegacyStaffCredentialMigrationKey);

            var value = Convert.ToString(
                await marker.ExecuteScalarAsync(ct));

            if (string.Equals(
                    value,
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await using var tx =
            (SqliteTransaction)await c.BeginTransactionAsync(ct);

        var legacyIds = new List<long>();

        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = """
                SELECT
                    id,
                    password_hash,password_salt,password_kdf,password_iterations,
                    pin_hash,pin_salt,pin_kdf,pin_iterations
                FROM users
                WHERE is_admin=0;
                """;

            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var passwordIsDefault = VerifySecret(
                    "1234",
                    r.GetString(2),
                    r.GetString(1),
                    r.GetString(3),
                    r.GetInt32(4));

                var pinIsDefault = VerifySecret(
                    "1234",
                    r.GetString(6),
                    r.GetString(5),
                    r.GetString(7),
                    r.GetInt32(8));

                if (passwordIsDefault && pinIsDefault)
                    legacyIds.Add(r.GetInt64(0));
            }
        }

        foreach (var id in legacyIds)
        {
            var randomPassword = HashSecret(
                Convert.ToBase64String(
                    RandomNumberGenerator.GetBytes(32)));
            var randomPin = HashSecret(
                Convert.ToBase64String(
                    RandomNumberGenerator.GetBytes(32)));

            await using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = """
                    UPDATE users
                    SET password_hash=$ph,
                        password_salt=$ps,
                        password_kdf=$kdf,
                        password_iterations=$iterations,
                        pin_hash=$ih,
                        pin_salt=$is,
                        pin_kdf=$kdf,
                        pin_iterations=$iterations,
                        is_active=0,
                        must_change_password=1,
                        failed_attempts=0,
                        locked_until=''
                    WHERE id=$id AND is_admin=0;
                    """;
                q.Parameters.AddWithValue("$ph", randomPassword.Hash);
                q.Parameters.AddWithValue("$ps", randomPassword.Salt);
                q.Parameters.AddWithValue("$ih", randomPin.Hash);
                q.Parameters.AddWithValue("$is", randomPin.Salt);
                q.Parameters.AddWithValue("$kdf", CurrentKdfAlgorithm);
                q.Parameters.AddWithValue(
                    "$iterations",
                    CurrentPbkdf2Iterations);
                q.Parameters.AddWithValue("$id", id);
                await q.ExecuteNonQueryAsync(ct);
            }

            await using (var permissions = c.CreateCommand())
            {
                permissions.Transaction = tx;
                permissions.CommandText = """
                    INSERT INTO user_permissions(
                        user_id,permissions,credentials_configured)
                    VALUES($id,0,0)
                    ON CONFLICT(user_id) DO UPDATE SET
                        credentials_configured=0;
                    """;
                permissions.Parameters.AddWithValue("$id", id);
                await permissions.ExecuteNonQueryAsync(ct);
            }
        }

        await using (var marker = c.CreateCommand())
        {
            marker.Transaction = tx;
            marker.CommandText = """
                INSERT INTO app_settings(key,value)
                VALUES($key,'true')
                ON CONFLICT(key) DO UPDATE SET value='true';
                """;
            marker.Parameters.AddWithValue(
                "$key",
                LegacyStaffCredentialMigrationKey);
            await marker.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    // Reuses the same PBKDF2 parameters/verification as user credentials for other
    // installation-scoped secrets (e.g. the technician service password) that must
    // never be a single value shared across every deployed copy of the application.
    public static (string Salt, string Hash) HashTechnicianSecret(string secret) =>
        HashSecret(secret);

    public static bool VerifyTechnicianSecret(
        string secret,
        string saltText,
        string hashText,
        string algorithm,
        int iterations) =>
        VerifySecret(secret, saltText, hashText, algorithm, iterations);

    private static (string Salt, string Hash) HashSecret(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(secret),
            salt,
            CurrentPbkdf2Iterations,
            HashAlgorithmName.SHA256,
            HashBytes);

        return (Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    private static bool VerifySecret(
        string secret,
        string saltText,
        string hashText,
        string algorithm,
        int iterations)
    {
        try
        {
            if (!string.Equals(
                    algorithm,
                    CurrentKdfAlgorithm,
                    StringComparison.OrdinalIgnoreCase) ||
                iterations < LegacyPbkdf2Iterations ||
                iterations > MaximumAcceptedPbkdf2Iterations)
            {
                return false;
            }

            var salt = Convert.FromBase64String(saltText);
            var expected = Convert.FromBase64String(hashText);

            if (salt.Length < 16 || expected.Length < 16)
                return false;

            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(secret),
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expected.Length);

            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    private static bool NeedsKdfUpgrade(string algorithm, int iterations) =>
        string.Equals(
            algorithm,
            CurrentKdfAlgorithm,
            StringComparison.OrdinalIgnoreCase) &&
        iterations >= LegacyPbkdf2Iterations &&
        iterations < CurrentPbkdf2Iterations;

    private static async Task<bool> TryUpgradePasswordKdfAsync(
        SqliteConnection c,
        UserRow row,
        string plaintext,
        CancellationToken ct)
    {
        if (!NeedsKdfUpgrade(row.PasswordKdf, row.PasswordIterations))
            return false;

        var upgraded = HashSecret(plaintext);
        await using var q = c.CreateCommand();
        q.CommandText = """
            UPDATE users
            SET password_hash=$hash,
                password_salt=$salt,
                password_kdf=$kdf,
                password_iterations=$iterations
            WHERE id=$id
              AND password_hash=$oldHash
              AND password_salt=$oldSalt
              AND password_kdf=$oldKdf
              AND password_iterations=$oldIterations;
            """;
        q.Parameters.AddWithValue("$hash", upgraded.Hash);
        q.Parameters.AddWithValue("$salt", upgraded.Salt);
        q.Parameters.AddWithValue("$kdf", CurrentKdfAlgorithm);
        q.Parameters.AddWithValue("$iterations", CurrentPbkdf2Iterations);
        q.Parameters.AddWithValue("$id", row.Id);
        q.Parameters.AddWithValue("$oldHash", row.PasswordHash);
        q.Parameters.AddWithValue("$oldSalt", row.PasswordSalt);
        q.Parameters.AddWithValue("$oldKdf", row.PasswordKdf);
        q.Parameters.AddWithValue("$oldIterations", row.PasswordIterations);
        return await q.ExecuteNonQueryAsync(ct) == 1;
    }

    private static async Task<bool> TryUpgradePinKdfAsync(
        SqliteConnection c,
        UserRow row,
        string plaintext,
        CancellationToken ct)
    {
        if (!NeedsKdfUpgrade(row.PinKdf, row.PinIterations))
            return false;

        var upgraded = HashSecret(plaintext);
        await using var q = c.CreateCommand();
        q.CommandText = """
            UPDATE users
            SET pin_hash=$hash,
                pin_salt=$salt,
                pin_kdf=$kdf,
                pin_iterations=$iterations
            WHERE id=$id
              AND pin_hash=$oldHash
              AND pin_salt=$oldSalt
              AND pin_kdf=$oldKdf
              AND pin_iterations=$oldIterations;
            """;
        q.Parameters.AddWithValue("$hash", upgraded.Hash);
        q.Parameters.AddWithValue("$salt", upgraded.Salt);
        q.Parameters.AddWithValue("$kdf", CurrentKdfAlgorithm);
        q.Parameters.AddWithValue("$iterations", CurrentPbkdf2Iterations);
        q.Parameters.AddWithValue("$id", row.Id);
        q.Parameters.AddWithValue("$oldHash", row.PinHash);
        q.Parameters.AddWithValue("$oldSalt", row.PinSalt);
        q.Parameters.AddWithValue("$oldKdf", row.PinKdf);
        q.Parameters.AddWithValue("$oldIterations", row.PinIterations);
        return await q.ExecuteNonQueryAsync(ct) == 1;
    }

    private static bool IsValidPin(string? pin) =>
        pin is { Length: 4 } && pin.All(char.IsDigit);

    private static BootstrapAdmin ReadBootstrapAndDelete()
    {
        if (!File.Exists(AppPaths.FirstRunAdminPath))
            return new BootstrapAdmin(false, "", "");

        try
        {
            var values = File.ReadAllLines(AppPaths.FirstRunAdminPath)
                .Select(line => line.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);

            var password = values.TryGetValue("password_hex", out var ph)
                ? DecodeUtf16Hex(ph)
                : "";
            var pin = values.TryGetValue("pin", out var p) ? p : "";
            return new BootstrapAdmin(true, password, pin);
        }
        catch
        {
            return new BootstrapAdmin(false, "", "");
        }
        finally
        {
            TryDelete(AppPaths.FirstRunAdminPath);
        }
    }

    private static string DecodeUtf16Hex(string hex)
    {
        if (hex.Length == 0 || hex.Length % 4 != 0)
            return "";

        var chars = new char[hex.Length / 4];
        for (var i = 0; i < chars.Length; i++)
        {
            var part = hex.Substring(i * 4, 4);
            chars[i] = (char)Convert.ToInt32(part, 16);
        }
        return new string(chars);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static async Task<UserRow?> ReadUserAsync(
        SqliteConnection c,
        string username,
        CancellationToken ct)
    {
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT u.id,u.username,
                   u.password_hash,u.password_salt,u.password_kdf,u.password_iterations,
                   u.pin_hash,u.pin_salt,u.pin_kdf,u.pin_iterations,
                   u.role,u.is_admin,u.is_active,u.must_change_password,
                   u.failed_attempts,COALESCE(u.locked_until,''),
                   COALESCE(p.permissions,0)
            FROM users u
            LEFT JOIN user_permissions p ON p.user_id=u.id
            WHERE u.username=$u COLLATE NOCASE
            LIMIT 1;
            """;
        q.Parameters.AddWithValue("$u", username);

        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;

        return new UserRow(
            Id: r.GetInt64(0),
            Username: r.GetString(1),
            PasswordHash: r.GetString(2),
            PasswordSalt: r.GetString(3),
            PasswordKdf: r.GetString(4),
            PasswordIterations: r.GetInt32(5),
            PinHash: r.GetString(6),
            PinSalt: r.GetString(7),
            PinKdf: r.GetString(8),
            PinIterations: r.GetInt32(9),
            Role: r.GetString(10),
            IsAdmin: r.GetInt64(11) == 1,
            IsActive: r.GetInt64(12) == 1,
            MustChangePassword: r.GetInt64(13) == 1,
            FailedAttempts: r.GetInt32(14),
            LockedUntil: r.GetString(15),
            Permissions: r.GetInt64(16));
    }

    private static async Task RegisterFailedAttemptAsync(
        SqliteConnection c,
        long id,
        CancellationToken ct)
    {
        // R118: the counter must keep rising. It used to be reset to 0 at the
        // very moment the account locked, so each 5-minute lockout handed the
        // next attacker another five free guesses - a flat ~1440 guesses a day
        // against a 4-digit PIN. Only a successful login clears it now (see
        // RegisterSuccessfulLoginAsync), and the wait doubles each time.
        int failures;
        await using (var bump = c.CreateCommand())
        {
            bump.CommandText = """
                UPDATE users SET failed_attempts = failed_attempts + 1 WHERE id=$id;
                SELECT failed_attempts FROM users WHERE id=$id;
                """;
            bump.Parameters.AddWithValue("$id", id);
            failures = Convert.ToInt32(await bump.ExecuteScalarAsync(ct));
        }

        var lockout = LoginLockoutPolicy.LockoutFor(failures);
        if (lockout <= TimeSpan.Zero)
            return;

        await using var q = c.CreateCommand();
        q.CommandText = "UPDATE users SET locked_until=$until WHERE id=$id;";
        q.Parameters.AddWithValue("$id", id);
        q.Parameters.AddWithValue("$until", DateTimeOffset.Now.Add(lockout).ToString("O"));
        await q.ExecuteNonQueryAsync(ct);
    }

    private static async Task RegisterSuccessfulLoginAsync(
        SqliteConnection c,
        long id,
        CancellationToken ct)
    {
        await using var q = c.CreateCommand();
        q.CommandText = """
            UPDATE users SET failed_attempts=0,locked_until='',last_login_at=$now WHERE id=$id;
            """;
        q.Parameters.AddWithValue("$id", id);
        q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        await q.ExecuteNonQueryAsync(ct);
    }

    private Task WriteAuditSafeAsync(
        string actor,
        string eventType,
        CancellationToken ct) =>
        WriteAuditSafeAsync(actor, eventType, "", ct);

    private async Task WriteAuditSafeAsync(
        string actor,
        string eventType,
        string details,
        CancellationToken ct)
    {
        if (_audit is null) return;

        try
        {
            await _audit.WriteAsync(
                actor,
                eventType,
                "USER",
                actor,
                details,
                ct);
        }
        catch
        {
            // Authentication must never be blocked by a secondary audit write failure.
        }
    }

    private static bool IsLocked(UserRow row, out int remainingMinutes)
    {
        remainingMinutes = 0;
        if (string.IsNullOrWhiteSpace(row.LockedUntil)) return false;
        if (!DateTimeOffset.TryParse(row.LockedUntil, out var until)) return false;
        var remaining = until - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return false;
        remainingMinutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return true;
    }

    private static bool UsesFactoryAdminCredentials(
        UserRow row) =>
        VerifySecret(
            FactoryAdminPassword,
            row.PasswordSalt,
            row.PasswordHash,
            row.PasswordKdf,
            row.PasswordIterations) ||
        VerifySecret(
            FactoryAdminPin,
            row.PinSalt,
            row.PinHash,
            row.PinKdf,
            row.PinIterations);

    private static async Task MarkMustChangePasswordAsync(
        SqliteConnection c,
        long userId,
        CancellationToken ct)
    {
        await using var q = c.CreateCommand();
        q.CommandText = """
            UPDATE users
            SET must_change_password=1
            WHERE id=$id AND is_admin=1;
            """;
        q.Parameters.AddWithValue("$id", userId);
        await q.ExecuteNonQueryAsync(ct);
    }

    private static AuthenticatedUser ToAuthenticated(UserRow row) =>
        new(
            row.Id,
            row.Username,
            row.Role,
            row.IsAdmin,
            row.MustChangePassword,
            (UserPermissions)row.Permissions);

    private sealed record BootstrapAdmin(bool WasFound, string Password, string Pin);

    private sealed record UserRow(
        long Id,
        string Username,
        string PasswordHash,
        string PasswordSalt,
        string PasswordKdf,
        int PasswordIterations,
        string PinHash,
        string PinSalt,
        string PinKdf,
        int PinIterations,
        string Role,
        bool IsAdmin,
        bool IsActive,
        bool MustChangePassword,
        int FailedAttempts,
        string LockedUntil,
        long Permissions);
}

public sealed record AuthenticationKdfStatus(
    int Users,
    string Algorithm,
    int CurrentIterations,
    int PasswordLegacyCount,
    int PasswordUnsupportedCount,
    int PinLegacyCount,
    int PinUnsupportedCount)
{
    public int LegacySecretCount => PasswordLegacyCount + PinLegacyCount;
    public int UnsupportedSecretCount => PasswordUnsupportedCount + PinUnsupportedCount;
    public bool IsHealthy => UnsupportedSecretCount == 0;
}
