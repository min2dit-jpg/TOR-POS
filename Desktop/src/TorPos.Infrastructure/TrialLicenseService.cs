using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed class TrialLicenseService : IDisposable
{
    private sealed record TrialCache(
        string TrialId,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset ExpiresAtUtc,
        DateTimeOffset LastServerTimeUtc,
        DateTimeOffset LastObservedUtc,
        string Signature);

    private sealed record TrialServerReply(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("server_time")] DateTimeOffset ServerTimeUtc,
        [property: JsonPropertyName("started_at")] DateTimeOffset StartedAtUtc,
        [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAtUtc,
        [property: JsonPropertyName("reused")] bool Reused);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly HttpClient _http;
    private readonly Uri _activationEndpoint;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly string _cachePath;
    private readonly string _identityPath;
    private readonly bool _ownsHttp;

    public TrialLicenseService(
        HttpMessageHandler? handler = null,
        Uri? activationEndpoint = null,
        Func<DateTimeOffset>? utcNow = null,
        string? cachePath = null,
        string? identityPath = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        _ownsHttp = true;
        _activationEndpoint = activationEndpoint ??
            new Uri(TrialPolicy.PublicApiBaseUrl + "/api/v1/trial/activate");
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _cachePath = cachePath ?? AppPaths.TrialStatePath;
        _identityPath = identityPath ?? AppPaths.TrialIdentityPath;
    }

    public string TrialId => LoadOrCreateTrialId(_identityPath);

    public async Task<TrialLicenseStatus> CheckOrActivateAsync(
        CancellationToken cancellationToken = default)
    {
        var now = _utcNow();
        string trialId;

        try
        {
            trialId = LoadOrCreateTrialId(_identityPath);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or
            CryptographicException or
            IOException or
            UnauthorizedAccessException)
        {
            return new TrialLicenseStatus(
                TrialLicenseState.VerificationRequired,
                "Die lokale Demo-ID konnte nicht gelesen oder erstellt werden. " +
                "Bitte TOR Service kontaktieren. " + ex.Message);
        }

        var cached = ReadCache(trialId);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _activationEndpoint)
            {
                Content = JsonContent.Create(new
                {
                    trial_id = trialId,
                    version = TorRelease.Version,
                    revision = TorRelease.Revision
                })
            };

            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Trial-Server HTTP {(int)response.StatusCode}");
            }

            var reply = await response.Content.ReadFromJsonAsync<TrialServerReply>(
                JsonOptions,
                cancellationToken);

            if (reply is null || !reply.Ok)
                throw new InvalidDataException("Trial-Serverantwort ist unvollständig.");

            ValidateServerReply(reply);

            var serverState = string.Equals(
                reply.State,
                "ACTIVE",
                StringComparison.OrdinalIgnoreCase)
                    ? TrialLicenseState.Active
                    : TrialLicenseState.Expired;

            var status = new TrialLicenseStatus(
                serverState,
                serverState == TrialLicenseState.Active
                    ? $"TOR POS Demo aktiv bis {reply.ExpiresAtUtc.ToLocalTime():dd.MM.yyyy HH:mm}."
                    : $"Die 7-Tage-Demo ist am {reply.ExpiresAtUtc.ToLocalTime():dd.MM.yyyy HH:mm} abgelaufen.",
                reply.StartedAtUtc,
                reply.ExpiresAtUtc,
                Offline: false,
                Reused: reply.Reused);

            SaveCache(
                trialId,
                reply.StartedAtUtc,
                reply.ExpiresAtUtc,
                reply.ServerTimeUtc,
                now > reply.ServerTimeUtc ? now : reply.ServerTimeUtc);

            return status;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or
            TaskCanceledException or
            InvalidDataException or
            JsonException)
        {
            if (cached is null)
            {
                return new TrialLicenseStatus(
                    TrialLicenseState.VerificationRequired,
                    "Die TOR POS Demo muss beim ersten Start online aktiviert werden. " +
                    "Bitte Internetverbindung prüfen und erneut starten.");
            }

            var offline = TrialClockPolicy.EvaluateCached(
                now,
                cached.StartedAtUtc,
                cached.ExpiresAtUtc,
                cached.LastObservedUtc);

            if (offline.IsActive)
            {
                SaveCache(
                    trialId,
                    cached.StartedAtUtc,
                    cached.ExpiresAtUtc,
                    cached.LastServerTimeUtc,
                    now);
            }

            return offline;
        }
    }

    public static string LoadOrCreateTrialId(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Trial-ID-Pfad fehlt.", nameof(path));

        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path, Encoding.UTF8)
                    .Trim()
                    .ToUpperInvariant();

                if (IsValidTrialId(existing))
                    return existing;

                throw new InvalidOperationException(
                    "Die vorhandene Demo-ID ist beschädigt.");
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var temp = path + ".new";
            File.WriteAllText(temp, id + Environment.NewLine, new UTF8Encoding(false));

            try
            {
                File.Move(temp, path, false);
                return id;
            }
            catch (IOException) when (File.Exists(path))
            {
                File.Delete(temp);
                var concurrent = File.ReadAllText(path, Encoding.UTF8)
                    .Trim()
                    .ToUpperInvariant();

                if (IsValidTrialId(concurrent))
                    return concurrent;

                throw;
            }
        }
        catch
        {
            try
            {
                var temp = path + ".new";
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch
            {
                // Best-effort cleanup only.
            }

            throw;
        }
    }

    public static bool IsValidTrialId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length == 64 &&
        value.All(static c =>
            (c >= '0' && c <= '9') ||
            (c >= 'A' && c <= 'F'));

    private static void ValidateServerReply(TrialServerReply reply)
    {
        if (reply.StartedAtUtc == default ||
            reply.ExpiresAtUtc == default ||
            reply.ServerTimeUtc == default)
        {
            throw new InvalidDataException("Trial-Serverzeiten fehlen.");
        }

        if (reply.ExpiresAtUtc <= reply.StartedAtUtc)
            throw new InvalidDataException("Trial-Laufzeit ist ungültig.");

        var maximum = reply.StartedAtUtc.AddDays(TrialPolicy.DurationDays)
            .AddMinutes(1);

        if (reply.ExpiresAtUtc > maximum)
            throw new InvalidDataException("Trial-Laufzeit überschreitet sieben Tage.");

        if (!string.Equals(reply.State, "ACTIVE", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(reply.State, "EXPIRED", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Trial-Status ist ungültig.");
        }
    }

    private TrialCache? ReadCache(string trialId)
    {
        try
        {
            if (!File.Exists(_cachePath))
                return null;

            var cache = JsonSerializer.Deserialize<TrialCache>(
                File.ReadAllText(_cachePath, Encoding.UTF8),
                JsonOptions);

            if (cache is null ||
                !string.Equals(cache.TrialId, trialId, StringComparison.Ordinal) ||
                !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(cache.Signature),
                    Convert.FromHexString(Sign(cache with { Signature = "" }, trialId))))
            {
                return null;
            }

            return cache;
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            JsonException or
            FormatException or
            CryptographicException)
        {
            return null;
        }
    }

    private void SaveCache(
        string trialId,
        DateTimeOffset startedAtUtc,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset lastServerTimeUtc,
        DateTimeOffset lastObservedUtc)
    {
        var unsigned = new TrialCache(
            trialId,
            startedAtUtc,
            expiresAtUtc,
            lastServerTimeUtc,
            lastObservedUtc,
            "");

        var cache = unsigned with
        {
            Signature = Sign(unsigned, trialId)
        };

        var directory = Path.GetDirectoryName(_cachePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temp = _cachePath + ".new";
        File.WriteAllText(
            temp,
            JsonSerializer.Serialize(cache, JsonOptions),
            new UTF8Encoding(false));
        File.Move(temp, _cachePath, true);
    }

    private static string Sign(TrialCache cache, string trialId)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(
            "TOR-POS-TRIAL-CACHE-V2|" + trialId));

        var canonical = string.Join(
            "|",
            cache.TrialId,
            cache.StartedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            cache.ExpiresAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            cache.LastServerTimeUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            cache.LastObservedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

        return Convert.ToHexString(
            HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical)));
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
