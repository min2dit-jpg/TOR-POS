using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TorPos.Core;
using TorPos.Infrastructure;

// G-4: a returned licence must not come back by deleting one file, turning the
// Windows clock back must not extend it, and a hand-made demo cache must not
// outlive what the trial server could ever have issued.
public static partial class RetailGastroFollowUpTests
{
    internal static async Task LicenseHardening(string dir, Action<bool, string> assert)
    {
        var a = Path.Combine(dir, "g4", "data", "license-state.dat");
        var b = Path.Combine(dir, "g4", "programdata", "license-state.dat");
        var store = new LicenseTamperStore(new[] { a, b }, useRegistry: false);
        store.RecordDeactivation("lic-4711");
        File.Delete(a);
        var afterDelete = new LicenseTamperStore(new[] { a, b }, useRegistry: false);
        assert(
            afterDelete.IsDeactivated("LIC-4711") && !afterDelete.IsDeactivated("LIC-0815") && !afterDelete.IsDeactivated(""),
            "G-4 a deactivated licence stays deactivated when one of its stores is deleted");

        var clock = new LicenseTamperStore(new[] { Path.Combine(dir, "g4", "clock.dat") }, useRegistry: false);
        var today = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        var first = clock.EffectiveUtcNow(today);
        var rolledBack = clock.EffectiveUtcNow(today.AddDays(-90));
        var forward = clock.EffectiveUtcNow(today.AddDays(1));
        var farBehind = new LicenseTamperStore(new[] { Path.Combine(dir, "g4", "clock.dat") }, useRegistry: false)
            .EffectiveUtcNow(today.AddDays(-500));
        assert(
            first == today && rolledBack == today && forward == today.AddDays(1) && farBehind == today.AddDays(-500),
            "G-4 licence expiry is judged at the latest time the PC has seen - a clock turned back does not extend it (a mark over 400 days ahead is not trusted)");

        var service = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Infrastructure/CommercialLicenseService.cs"));
        assert(
            service.Contains("_tamper.RecordDeactivation(current.LicenseId);", StringComparison.Ordinal) &&
            service.Contains("if (_tamper.IsDeactivated(licenseId))", StringComparison.Ordinal) &&
            service.Contains("payload.ValidUntilUtc <= _tamper.EffectiveUtcNow(DateTimeOffset.UtcNow)", StringComparison.Ordinal) &&
            !service.Contains("payload.ValidUntilUtc <= DateTimeOffset.UtcNow", StringComparison.Ordinal),
            "G-4 the commercial licence service writes and checks the tombstones and uses the clock high-water mark for expiry");

        // Trial: a cache signed with the (derivable) key but claiming 30 days.
        var trialDir = Path.Combine(dir, "g4", "trial");
        Directory.CreateDirectory(trialDir);
        var identity = Path.Combine(trialDir, "trial.id");
        var cachePath = Path.Combine(trialDir, "trial-state.json");
        var trialId = TrialLicenseService.LoadOrCreateTrialId(identity);
        var now = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

        async Task<TrialLicenseState> Offline(DateTimeOffset started, DateTimeOffset expires, DateTimeOffset lastServer)
        {
            WriteTrialCache(cachePath, trialId, started, expires, lastServer, now.AddMinutes(-1));
            using var trial = new TrialLicenseService(new OfflineHandler(), new Uri("https://trial.invalid/api"), () => now, cachePath, identity);
            return (await trial.CheckOrActivateAsync()).State;
        }

        var genuine = await Offline(now.AddDays(-2), now.AddDays(5), now.AddDays(-2));
        var forged = await Offline(now.AddDays(-2), now.AddDays(28), now.AddDays(-2));
        var backdatedStart = await Offline(now.AddHours(-1), now.AddDays(6), now.AddDays(-3));
        assert(
            genuine == TrialLicenseState.Active &&
            forged == TrialLicenseState.VerificationRequired &&
            backdatedStart == TrialLicenseState.VerificationRequired,
            "G-4 a demo cache beyond seven days from its start, or started after its last server contact, is refused and an online check is required");
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("offline");
    }

    private static void WriteTrialCache(string path, string trialId, DateTimeOffset started, DateTimeOffset expires, DateTimeOffset lastServer, DateTimeOffset lastObserved)
    {
        static string O(DateTimeOffset x) => x.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var key = SHA256.HashData(Encoding.UTF8.GetBytes("TOR-POS-TRIAL-CACHE-V2|" + trialId));
        var canonical = string.Join("|", trialId, O(started), O(expires), O(lastServer), O(lastObserved));
        var signature = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical)));
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            TrialId = trialId,
            StartedAtUtc = started,
            ExpiresAtUtc = expires,
            LastServerTimeUtc = lastServer,
            LastObservedUtc = lastObserved,
            Signature = signature
        }));
    }
}

public static partial class RetailGastroFollowUpTests
{
    internal static void DataLocation(string dir, Action<bool, string> assert)
    {
        var users = Path.Combine(dir, "o15", "Users");
        const string product = "TOR-Einzelhandel";
        string DataOf(string user) => Path.Combine(users, user, "AppData", "Roaming", product);
        foreach (var user in new[] { "kasse", "chef", "aushilfe" })
            Directory.CreateDirectory(DataOf(user));
        File.WriteAllText(Path.Combine(DataOf("kasse"), "torpos.db"), "");
        var alone = DataLocationCheck.Warnings(DataOf("kasse"), users, product);
        File.WriteAllText(Path.Combine(DataOf("chef"), "torpos.db"), "");
        Directory.CreateDirectory(Path.Combine(users, "gast", "AppData", "Roaming", "TOR-Gastro"));
        File.WriteAllText(Path.Combine(users, "gast", "AppData", "Roaming", "TOR-Gastro", "torpos.db"), "");
        var split = DataLocationCheck.Warnings(DataOf("kasse"), users, product);
        assert(
            alone.Count == 0 &&
            split.Count == 1 && split[0].Contains("Windows-Benutzer chef", StringComparison.Ordinal) &&
            !split[0].Contains("gast", StringComparison.Ordinal) && !split[0].Contains("aushilfe", StringComparison.Ordinal) &&
            DataLocationCheck.Warnings(DataOf("kasse"), Path.Combine(dir, "o15", "fehlt"), product).Count == 0,
            "O-15 fiscal data of the same product in another Windows user's profile is reported at start; other products and empty profiles are not");
    }
}
