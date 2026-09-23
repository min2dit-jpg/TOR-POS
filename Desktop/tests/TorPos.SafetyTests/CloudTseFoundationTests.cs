using System.Text.RegularExpressions;

// Cloud TSE: the seam, and the refusal.
//
// A cloud TSE is a certified signing device operated by a vendor and reached
// over HTTPS. Each vendor - fiskaly, Deutsche Fiskal, a fiskaltrust Middleware
// with a cloud SCU - has its own contract, its own REST shape and its own
// certification, and none of that can be written truthfully without the vendor.
//
// So what exists today is the part that is true regardless of vendor: the
// provider seam, the configuration including the tenant/queue pairing, a
// bounded health check, and a hard refusal to produce fiscal data. These checks
// exist to keep that refusal honest - the one failure mode that must never
// happen is a till that returns something looking like a signature.
public static class CloudTseFoundationTests
{
    public static Task Run(Action<bool, string> assert)
    {
        var core = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Core/CloudTse.cs"));
        var provider = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Infrastructure/CloudTseProvider.cs"));
        var settings = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Infrastructure/CloudTseSettings.cs"));
        var checkout = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Core/CheckoutSafety.cs"));
        var app = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/App.axaml.cs"));

        // A typo in one settings row must not move a till onto a different
        // fiscal device. Unknown falls back to the hardware TSE, never to cloud.
        assert(
            core.Contains("public static string Normalize(string? value) =>", StringComparison.Ordinal) &&
            Regex.IsMatch(core, @"\?\s*Cloud\s*\r?\n\s*:\s*SwissbitUsb;") &&
            app.Contains("TseProviderKind.Normalize(", StringComparison.Ordinal),
            "cloud TSE: the device is chosen once at the composition root, and an unknown value falls back to the USB TSE rather than to the cloud");

        // Nothing signs while the vendor's gate is closed - and the gate is the
        // first thing checked, before configuration and before reachability.
        var blocker = Block(provider, "private static string? Blocker(", "public static string UnprotectApiKey");
        assert(
            core.Contains("public const bool FiskaltrustValidated = false;", StringComparison.Ordinal) &&
            core.Contains("public const bool FiskalyValidated = false;", StringComparison.Ordinal) &&
            core.Contains("public const bool DeutscheFiskalValidated = false;", StringComparison.Ordinal) &&
            blocker.IndexOf("CloudTseRelease.IsValidated", StringComparison.Ordinal) <
                blocker.IndexOf("config.IsAddressable", StringComparison.Ordinal),
            "cloud TSE: every vendor is qualified on its own flag, and an unqualified one refuses before configuration or reachability is even looked at");

        // The trap a single cloud flag would have been: qualifying one vendor's
        // sandbox opening production for vendors nobody ever ran a transaction
        // against. And a name TOR does not know is never a released one.
        var validated = Block(core, "public static bool IsValidated(string? vendor)", "public static string NotReleasedMessage");
        assert(
            Regex.IsMatch(validated, @"_\s*=>\s*false") &&
            !checkout.Contains("CloudTseValidated", StringComparison.Ordinal) &&
            !Block(checkout, "public static bool Enabled =>", "public static IReadOnlyList<string> MissingQualifications()")
                .Contains("Cloud", StringComparison.Ordinal),
            "cloud TSE: an unknown vendor is never validated, and cloud qualification neither rides on nor blocks the six existing fiscal release flags");

        // The refusal itself. Every call that would produce fiscal data returns
        // failure, and none of them invents a transaction number, a counter or
        // a signature.
        var fiscalCalls = new[]
        {
            "ActivateAsync", "StartTransactionAsync", "UpdateTransactionAsync",
            "FinishTransactionAsync", "ExportTarAsync"
        };
        assert(
            fiscalCalls.All(call => provider.Contains(call, StringComparison.Ordinal)) &&
            provider.Contains("Task.FromResult(Refused())", StringComparison.Ordinal) &&
            Regex.Matches(provider, @"Task\.FromResult\(Refused\(\)\)").Count == 3 &&
            provider.Contains("new(false, NotReleasedMessage);", StringComparison.Ordinal) &&
            !Regex.IsMatch(WithoutComments(provider), @"TransactionNumber\s*=|SignatureCounter\s*=|SignatureBase64\s*="),
            "cloud TSE: every fiscal call refuses and none of them fabricates a transaction number, a signature counter or a signature");

        // A till must never be what waits on a network. The health check is
        // bounded and sends no fiscal data and no key.
        var health = Block(provider, "public async Task<CloudTseHealth> CheckHealthAsync", "// Everything below produces fiscal data");
        assert(
            health.Contains("timeout.CancelAfter(_timeout)", StringComparison.Ordinal) &&
            health.Contains("HttpMethod.Head", StringComparison.Ordinal) &&
            health.Contains("catch (OperationCanceledException)", StringComparison.Ordinal) &&
            !health.Contains("ApiKey", StringComparison.Ordinal),
            "cloud TSE: the reachability check is bounded by a timeout, sends no fiscal data and carries no key, so a dead endpoint cannot freeze the till");

        // The pairing that ruins books quietly: a till signing into another
        // tenant's queue produces receipts that look valid and belong to
        // somebody else.
        assert(
            core.Contains("string TenantId,", StringComparison.Ordinal) &&
            core.Contains("string QueueId,", StringComparison.Ordinal) &&
            core.Contains("uri.Scheme == Uri.UriSchemeHttps", StringComparison.Ordinal) &&
            core.Contains("public string MissingPart()", StringComparison.Ordinal),
            "cloud TSE: tenant and queue are part of the configuration and an endpoint that is not HTTPS is never addressable");

        // The key is a secret like any other in this program.
        assert(
            provider.Contains("ProtectedData.Protect", StringComparison.Ordinal) &&
            provider.Contains("DataProtectionScope.CurrentUser", StringComparison.Ordinal) &&
            settings.Contains("ApiKeySetting] = protectedKey", StringComparison.Ordinal) &&
            !Regex.IsMatch(WithoutComments(settings), @"ApiKeySetting\]\s*=\s*newApiKey"),
            "cloud TSE: the API key is only stored DPAPI-protected, and an empty key box keeps the stored key instead of wiping it");

        return Task.CompletedTask;
    }

    private static string WithoutComments(string source) =>
        string.Join(
            "\n",
            source
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string Block(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        if (from < 0) return "";
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        return to < 0 ? source[from..] : source[from..to];
    }

    private static string FindRepoFile(string relative)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException(relative);
    }
}
