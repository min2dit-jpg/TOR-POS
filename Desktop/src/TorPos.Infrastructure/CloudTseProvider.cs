using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// Cloud TSE: the signing device is operated by a certified provider and
/// reached over HTTPS instead of sitting in a USB port.
///
/// This is the seam and the safety behaviour, not a vendor adapter. Each
/// vendor - fiskaly, Deutsche Fiskal, a fiskaltrust Middleware with a cloud SCU
/// - has its own contract, its own REST shape and its own certification, and
/// none of it can be written truthfully without that vendor. So everything that
/// would produce fiscal data refuses, loudly and in one place, rather than
/// returning something that looks like a signature.
///
/// What IS real here:
/// - configuration, including the tenant/queue pairing a till must not get wrong
/// - a health check with a hard timeout, off the UI thread
/// - fail-closed behaviour that TseFailSafeService turns into a documented
///   TSE-Ausfall, exactly as it does for a hardware TSE
/// - the release gate: while FiscalRelease.CloudTseValidated is false, nothing
///   signs, no matter how well the endpoint answers
///
/// The speed rule for a till applies here more than anywhere: a network call
/// must never be what makes KASSIEREN wait. Every call below is async, bounded
/// by a timeout measured in seconds, and a dead endpoint produces an outage
/// rather than a frozen screen.
/// </summary>
public sealed class CloudTseProvider : ITseProvider
{
    public const string NotReleasedMessage =
        "Cloud-TSE ist in diesem Build nicht freigegeben. Es wird nichts signiert.";

    private readonly Func<CloudTseConfiguration> _configuration;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public CloudTseProvider(
        Func<CloudTseConfiguration> configuration,
        HttpClient? http = null,
        TimeSpan? timeout = null)
    {
        _configuration = configuration;
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
        _http = http ?? new HttpClient { Timeout = _timeout };
    }

    public string ProviderId => "CLOUD_TSE";

    public string DisplayName => "Cloud-TSE";

    public string PreferredProduct =>
        "Zertifizierte Cloud-TSE eines Anbieters (Vertrag erforderlich)";

    // There is no local SDK to load. Whether this till can sign at all depends
    // on the release gate and the configuration, never on a DLL.
    public bool SdkAvailable => false;

    public bool ActivationAvailable => false;

    public bool TransactionAvailable => false;

    public bool ExportAvailable => false;

    public TseRuntimeStatus GetRuntimeStatus()
    {
        var config = Read();

        return new TseRuntimeStatus(
            false,
            false,
            "",
            config.BaseUrl,
            Blocker(config) ?? "Cloud-TSE konfiguriert.");
    }

    public Task<IReadOnlyList<string>> FindSdkLibrariesAsync(
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public TseRuntimeStatus ConfigureSdkLibrary(string libraryPath) =>
        new(false, false, "", "", "Eine Cloud-TSE verwendet keine lokale SDK-Bibliothek.");

    public async Task<TseProbeResult> ProbeAsync(
        CancellationToken ct = default)
    {
        var config = Read();

        var blocker = Blocker(config);
        if (blocker is not null)
        {
            return new TseProbeResult(
                config.IsAddressable
                    ? TseConnectionState.Error
                    : TseConnectionState.NotConfigured,
                blocker);
        }

        var health = await CheckHealthAsync(config, ct);

        // Reachable is not ready. The endpoint answering says the network is
        // fine; it says nothing about this till being paired to the right
        // queue, and nothing about the vendor adapter existing.
        return new TseProbeResult(
            TseConnectionState.Error,
            health.Reachable
                ? $"Cloud-TSE erreichbar ({health.RoundTripMs} ms) · {NotReleasedMessage}"
                : $"Cloud-TSE nicht erreichbar · {health.Message}");
    }

    /// <summary>
    /// A plain reachability check against the configured endpoint. It sends no
    /// fiscal data and carries no secret: it only answers whether the queue's
    /// host responds at all, which is what an operator needs before anyone
    /// starts looking for a contract problem.
    /// </summary>
    public async Task<CloudTseHealth> CheckHealthAsync(
        CloudTseConfiguration config,
        CancellationToken ct = default)
    {
        if (!config.IsAddressable)
            return new CloudTseHealth(false, 0, 0, $"Unvollständig: {config.MissingPart()}");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);

        var watch = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Head,
                config.BaseUrl);

            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            watch.Stop();

            return new CloudTseHealth(
                true,
                (int)response.StatusCode,
                watch.ElapsedMilliseconds,
                $"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            return new CloudTseHealth(
                false,
                0,
                watch.ElapsedMilliseconds,
                $"Zeitüberschreitung nach {_timeout.TotalSeconds:0} Sekunden.");
        }
        catch (Exception ex)
        {
            watch.Stop();
            return new CloudTseHealth(false, 0, watch.ElapsedMilliseconds, ex.Message);
        }
    }

    // Everything below produces fiscal data, so everything below refuses.
    // Returning a plausible-looking transaction number would be worse than any
    // outage: an outage is documented and recoverable, a fabricated signature
    // is a forged fiscal record.
    public Task<TseActivationResult> ActivateAsync(
        TseActivationRequest request,
        CancellationToken ct = default) =>
        Task.FromResult(new TseActivationResult(false, NotReleasedMessage));

    public Task<TseTransactionResult> StartTransactionAsync(
        TseTransactionStartRequest request,
        CancellationToken ct = default) =>
        Task.FromResult(Refused());

    public Task<TseTransactionResult> UpdateTransactionAsync(
        TseTransactionUpdateRequest request,
        CancellationToken ct = default) =>
        Task.FromResult(Refused());

    public Task<TseTransactionResult> FinishTransactionAsync(
        TseTransactionFinishRequest request,
        CancellationToken ct = default) =>
        Task.FromResult(Refused());

    public Task<TseExportResult> ExportTarAsync(
        string targetPath,
        CancellationToken ct = default) =>
        Task.FromResult(new TseExportResult(false, NotReleasedMessage));

    // Success=false and every fiscal field at its default. Nothing that
    // could be mistaken for a transaction number or a counter.
    private static TseTransactionResult Refused() =>
        new(false, NotReleasedMessage);

    private CloudTseConfiguration Read()
    {
        try
        {
            return _configuration() ?? CloudTseConfiguration.Empty;
        }
        catch
        {
            return CloudTseConfiguration.Empty;
        }
    }

    /// <summary>
    /// The single reason this till cannot sign through the cloud right now, or
    /// null. The release gate is checked first on purpose: a perfectly
    /// configured, perfectly reachable endpoint changes nothing while the cloud
    /// path is unqualified.
    /// </summary>
    private static string? Blocker(CloudTseConfiguration config)
    {
        if (!FiscalRelease.CloudTseValidated)
            return NotReleasedMessage;

        if (!config.IsAddressable)
            return $"Cloud-TSE ist nicht vollständig eingerichtet: {config.MissingPart()} fehlt.";

        return null;
    }

    /// <summary>
    /// The API key, unwrapped only here and never held in a field. An empty
    /// result is a missing key, never an exception on a selling path.
    /// </summary>
    public static string UnprotectApiKey(string protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue) || !OperatingSystem.IsWindows())
            return "";

        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(
                    Convert.FromBase64String(protectedValue),
                    null,
                    DataProtectionScope.CurrentUser));
        }
        catch
        {
            return "";
        }
    }

    public static string ProtectApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return "";

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Der Cloud-TSE-Schlüssel wird nur unter Windows gespeichert.");

        return Convert.ToBase64String(
            ProtectedData.Protect(
                Encoding.UTF8.GetBytes(apiKey.Trim()),
                null,
                DataProtectionScope.CurrentUser));
    }
}
