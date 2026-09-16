namespace TorPos.Core;

[Flags]
public enum UserPermissions : long
{
    None = 0,
    Sale = 1L << 0,
    Discount = 1L << 1,
    ImmediateStorno = 1L << 2,
    ReceiptStorno = 1L << 3,
    ParkReceipts = 1L << 4,
    CashMovement = 1L << 5,
    ZReport = 1L << 6,
    ManageProducts = 1L << 7,
    ViewReceiptHistory = 1L << 8,
    Training = 1L << 9
}

public sealed record AuthenticatedUser(
    long Id,
    string Username,
    string Role,
    bool IsAdmin,
    bool MustChangePassword,
    UserPermissions Permissions = UserPermissions.None,
    bool IsTraining = false)
{
    /// <summary>
    /// R122 (audit finding G4): a session whose credentials are still the
    /// factory ones (admin/admin, PIN 1234) can do NOTHING until they are
    /// replaced - not even the things IsAdmin would otherwise unlock.
    ///
    /// Before this, MustChangePassword was enforced for staff inside
    /// AuthenticationService, but for the admin only by the UI: App.axaml.cs
    /// opens RequiredAdminCredentialsWindow after login and refuses to
    /// continue without it. That works, but it is a single check in a single
    /// window, and every other entry point had to remember the same rule on
    /// its own (CheckoutReviewWindow and MainWindow.Safety each do, by hand).
    /// Putting it here means a caller that forgets gets a powerless session
    /// instead of a full admin one.
    ///
    /// Nothing legitimate is blocked: the credential-change dialog itself
    /// verifies the current password through
    /// IAuthenticationService.ChangeAdminCredentialsAsync, not through Can(),
    /// and App.axaml.cs clears the flag on the session object the moment the
    /// change succeeds.
    /// </summary>
    public bool Can(UserPermissions permission) =>
        !MustChangePassword && (IsAdmin || (Permissions & permission) == permission);
}

public sealed record StaffUser(
    long Id,
    int Slot,
    string Username,
    bool IsActive,
    bool CredentialsConfigured,
    UserPermissions Permissions);

public sealed record StaffUserUpdate(
    long Id,
    string Username,
    bool IsActive,
    UserPermissions Permissions,
    string NewPassword,
    string NewPin);

public sealed record AuthenticationResult(
    bool Success,
    string Message,
    AuthenticatedUser? User = null);

public interface IAuthenticationService
{
    Task InitializeAsync(CancellationToken ct = default);

    Task<AuthenticationResult> LoginWithPasswordAsync(
        string username,
        string password,
        CancellationToken ct = default);

    Task<AuthenticationResult> LoginWithPinAsync(
        string username,
        string pin,
        CancellationToken ct = default);

    Task ChangeAdminCredentialsAsync(
        string currentPassword,
        string newPassword,
        string newPin,
        CancellationToken ct = default);

    Task<IReadOnlyList<StaffUser>> GetStaffUsersAsync(
        CancellationToken ct = default);

    Task SaveStaffUserAsync(
        StaffUserUpdate user,
        string changedBy,
        CancellationToken ct = default);
}
