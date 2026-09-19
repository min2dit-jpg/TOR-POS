using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed class TrialLicenseService : IDisposable
{
    private sealed record TrialCache(
        string FingerprintSha256,
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
    private readonly Func<string> _fingerprintProvider;
    private readonly bool _ownsHttp;

    public TrialLicenseService(
        HttpMessageHandler? handler = null,
        Uri? activationEndpoint = null,
        Func<DateTimeOffset>? utcNow = null,
        string? cachePath = null,
        Func<string>? fingerprintProvider = null)
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
        _fingerprintProvider = fingerprintProvider ?? CreateMachineFingerprintHash;
    }

    public string FingerprintSha256 => _fingerprintProvider();

    public async Task<TrialLicenseStatus> CheckOrActivateAsync(
        CancellationToken cancellationToken = default)
    {
        var now = _utcNow();
        string fingerprint;

        try
        {
            fingerprint = _fingerprintProvider();
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or
            CryptographicException or
            IOException or
            UnauthorizedAccessException)
        {
            return new TrialLicenseStatus(
                TrialLicenseState.VerificationRequired,
                "Dieser PC konnte für die Demo nicht eindeutig erkannt werden. " +
                "Bitte TOR Service kontaktieren. " + ex.Message);
        }

        var cached = ReadCache(fingerprint);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _activationEndpoint)
            {
                Content = JsonContent.Create(new
                {
                    fingerprint_sha256 = fingerprint,
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
                fingerprint,
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
                    fingerprint,
                    cached.StartedAtUtc,
                    cached.ExpiresAtUtc,
                    cached.LastServerTimeUtc,
                    now);
            }

            return offline;
        }
    }

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

    private TrialCache? ReadCache(string fingerprint)
    {
        try
        {
            if (!File.Exists(_cachePath))
                return null;

            var cache = JsonSerializer.Deserialize<TrialCache>(
                File.ReadAllText(_cachePath, Encoding.UTF8),
                JsonOptions);

            if (cache is null ||
                !string.Equals(
                    cache.FingerprintSha256,
                    fingerprint,
                    StringComparison.OrdinalIgnoreCase) ||
                !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(cache.Signature),
                    Convert.FromHexString(Sign(cache with { Signature = "" }, fingerprint))))
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
        string fingerprint,
        DateTimeOffset startedAtUtc,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset lastServerTimeUtc,
        DateTimeOffset lastObservedUtc)
    {
        var unsigned = new TrialCache(
            fingerprint,
            startedAtUtc,
            expiresAtUtc,
            lastServerTimeUtc,
            lastObservedUtc,
            "");

        var cache = unsigned with
        {
            Signature = Sign(unsigned, fingerprint)
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

    private static string Sign(TrialCache cache, string fingerprint)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(
            "TOR-POS-TRIAL-CACHE-V1|" + fingerprint));

        var canonical = string.Join(
            "|",
            cache.FingerprintSha256.ToUpperInvariant(),
            cache.StartedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            cache.ExpiresAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            cache.LastServerTimeUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            cache.LastObservedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

        return Convert.ToHexString(
            HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical)));
    }

    public static string FingerprintHashFor(
        string machineGuid,
        string volumeSerial)
    {
        machineGuid = (machineGuid ?? "").Trim().ToUpperInvariant();
        volumeSerial = (volumeSerial ?? "").Trim().ToUpperInvariant();

        if (machineGuid.Length == 0 && volumeSerial.Length == 0)
            throw new InvalidOperationException(
                "Windows MachineGuid und Systemlaufwerk-Seriennummer fehlen.");

        var raw = Encoding.UTF8.GetBytes(
            $"TOR-POS-TRIAL-PC-V1|{machineGuid}|{volumeSerial}");

        return Convert.ToHexString(SHA256.HashData(raw));
    }

    public static string CreateMachineFingerprintHash()
    {
        var machineGuid = "";
        var volumeSerial = "";

        if (OperatingSystem.IsWindows())
        {
            try
            {
                machineGuid = Registry.LocalMachine
                    .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography")?
                    .GetValue("MachineGuid")?
                    .ToString() ?? "";
            }
            catch
            {
                // Volume serial remains as fallback.
            }

            try
            {
                var root = Path.GetPathRoot(Environment.SystemDirectory);
                if (!string.IsNullOrWhiteSpace(root) &&
                    GetVolumeInformation(
                        root,
                        null,
                        0,
                        out var serial,
                        out _,
                        out _,
                        null,
                        0))
                {
                    volumeSerial = serial.ToString("X8", CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                // MachineGuid remains as fallback.
            }
        }

        return FingerprintHashFor(machineGuid, volumeSerial);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(
        string rootPathName,
        StringBuilder? volumeNameBuffer,
        uint volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder? fileSystemNameBuffer,
        uint fileSystemNameSize);

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
