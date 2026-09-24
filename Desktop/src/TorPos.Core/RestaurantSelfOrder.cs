using System.Security.Cryptography;
using System.Text;

namespace TorPos.Core;

public enum RestaurantSelfOrderApprovalMode
{
    ConfirmationRequired = 1,
    Automatic = 2
}

public enum RestaurantSelfOrderOrderState
{
    Received = 1,
    Accepted = 2,
    Rejected = 3,
    Expired = 4
}

/// <summary>
/// The printable table QR contains PublicToken. Persistence stores TokenHash
/// only. A leaked database therefore cannot recreate printable table QR links.
/// The static table token identifies a table but is never sufficient to place
/// an order by itself; a later active table-session capability is required too.
/// </summary>
public sealed record RestaurantSelfOrderTableQrSecret(
    string PublicToken,
    string TokenHash,
    DateTimeOffset CreatedAt);

public sealed record RestaurantSelfOrderTableQrIssue(
    long TableId,
    string PublicToken,
    DateTimeOffset RotatedAt);

public sealed record RestaurantSelfOrderSessionCapabilityIssue(
    string SessionId,
    long TableId,
    string PublicSessionId,
    string CapabilitySecret,
    RestaurantSelfOrderApprovalMode ApprovalMode,
    DateTimeOffset ExpiresAt);

public sealed record RestaurantSelfOrderCapabilityValidation(
    bool Valid,
    string SessionId,
    long TableId,
    RestaurantSelfOrderApprovalMode ApprovalMode)
{
    public static RestaurantSelfOrderCapabilityValidation Invalid { get; } =
        new(false, "", 0, RestaurantSelfOrderApprovalMode.ConfirmationRequired);
}

public static class RestaurantSelfOrderSecurity
{
    public const int TableTokenBytes = 32;

    public static string CreatePublicSessionId() =>
        Base64Url(
            RandomNumberGenerator.GetBytes(16));

    public static RestaurantSelfOrderTableQrSecret CreateTableQrSecret()
    {
        var token = Base64Url(
            RandomNumberGenerator.GetBytes(
                TableTokenBytes));

        return new RestaurantSelfOrderTableQrSecret(
            token,
            HashToken(token),
            DateTimeOffset.UtcNow);
    }

    public static string HashToken(
        string token)
    {
        token = (token ?? "").Trim();

        if (token.Length < 32 ||
            token.Length > 128 ||
            token.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Self-Order-Token ist ungültig.",
                nameof(token));
        }

        return Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(token)))
            .ToLowerInvariant();
    }

    public static bool VerifyToken(
        string token,
        string expectedHash)
    {
        try
        {
            var actual = Convert.FromHexString(
                HashToken(token));
            var expected = Convert.FromHexString(
                (expectedHash ?? "").Trim());

            return actual.Length == expected.Length &&
                   CryptographicOperations.FixedTimeEquals(
                       actual,
                       expected);
        }
        catch
        {
            return false;
        }
    }

    private static string Base64Url(
        byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
