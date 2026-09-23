using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed record FiskalySignDeCredentials(
    string ApiKey,
    string ApiSecret);

public interface IFiskalySignDeCredentialSource
{
    ValueTask<FiskalySignDeCredentials> GetAsync(
        CancellationToken ct = default);
}

/// <summary>
/// Thin SIGN DE v2 client for fiskaly's remote Middleware API.
///
/// This class is intentionally not registered in the application yet.
/// Production use remains impossible while FiscalRelease.CloudTseValidated is
/// false. Secrets are supplied only through IFiskalySignDeCredentialSource and
/// are never stored in TOR settings or included in exception text.
/// </summary>
public sealed class FiskalySignDeTseClient : IDirectCloudTseClient
{
    private sealed record CachedToken(
        string AccessToken,
        DateTimeOffset ExpiresAt);

    private sealed record TransactionProgress(
        long Revision,
        string State,
        string Fingerprint,
        TseTransactionResult Result);

    private sealed record PendingMutation(
        long Revision,
        string State,
        string Fingerprint,
        object Body);

    private readonly DirectCloudTseConfiguration _configuration;
    private readonly IFiskalySignDeCredentialSource _credentials;
    private readonly HttpClient _http;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _authGate = new(1, 1);
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _transactionGates = new();
    private readonly ConcurrentDictionary<ulong, TransactionProgress> _progress = new();
    private readonly ConcurrentDictionary<ulong, PendingMutation> _pending = new();

    private CachedToken? _token;

    public FiskalySignDeTseClient(
        DirectCloudTseConfiguration configuration,
        IFiskalySignDeCredentialSource credentials,
        HttpClient? httpClient = null,
        TimeSpan? requestTimeout = null)
    {
        _configuration = configuration ??
            throw new ArgumentNullException(nameof(configuration));
        _credentials = credentials ??
            throw new ArgumentNullException(nameof(credentials));
        _http = httpClient ?? new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(4);

        if (_requestTimeout <= TimeSpan.Zero ||
            _requestTimeout > TimeSpan.FromSeconds(15))
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                "fiskaly request timeout must be between >0 and 15 seconds.");
        }
    }

    public bool IsConfigured =>
        string.Equals(
            _configuration.ProviderId,
            TseProviderCatalog.FiskalyDirectCloud,
            StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(_configuration.Endpoint) &&
        !string.IsNullOrWhiteSpace(_configuration.MandantId) &&
        !string.IsNullOrWhiteSpace(_configuration.TssId) &&
        !string.IsNullOrWhiteSpace(_configuration.ProviderClientId) &&
        !string.IsNullOrWhiteSpace(_configuration.CashRegisterSerialNumber);

    // Export is deliberately kept closed until the asynchronous export job
    // lifecycle (trigger/poll/download/retry) has its own release evidence.
    public bool ExportAvailable => false;

    public async Task<TseProbeResult> ProbeAsync(
        CancellationToken ct = default)
    {
        TseProviderCatalog.RequireProviderRelease(
            TseProviderCatalog.FiskalyDirectCloud);

        try
        {
            var cfg = RequireConfiguration();

            using var timeout = LinkedTimeout(ct);

            var tssTask = GetJsonAsync(
                Relative(
                    "tss",
                    cfg.TssId),
                timeout.Token);

            var clientTask = GetJsonAsync(
                Relative(
                    "tss",
                    cfg.TssId,
                    "client",
                    cfg.ProviderClientId),
                timeout.Token);

            await Task.WhenAll(
                tssTask,
                clientTask);

            using var tss = await tssTask;
            using var client = await clientTask;

            var tssRoot = tss.RootElement;
            var clientRoot = client.RootElement;

            var tssState = ReadString(
                tssRoot,
                "state");
            var clientState = ReadString(
                clientRoot,
                "state");
            var clientSerial = ReadString(
                clientRoot,
                "serial_number");
            var tssSerial = ReadString(
                tssRoot,
                "serial_number");

            if (!string.Equals(
                    clientSerial,
                    cfg.CashRegisterSerialNumber,
                    StringComparison.Ordinal))
            {
                return new TseProbeResult(
                    TseConnectionState.Error,
                    "Cloud-TSE Client gehört zu einer anderen Kassen-Seriennummer.");
            }

            if (!string.Equals(
                    tssState,
                    "INITIALIZED",
                    StringComparison.OrdinalIgnoreCase))
            {
                return new TseProbeResult(
                    TseConnectionState.Connected,
                    $"Cloud-TSE ist verbunden, aber Zustand ist {tssState}.");
            }

            if (!string.Equals(
                    clientState,
                    "REGISTERED",
                    StringComparison.OrdinalIgnoreCase))
            {
                return new TseProbeResult(
                    TseConnectionState.Connected,
                    $"Cloud-TSE Client ist nicht registriert ({clientState}).");
            }

            return new TseProbeResult(
                TseConnectionState.Ready,
                "fiskaly Cloud-TSE ist bereit.",
                new TseDeviceInfo(
                    "fiskaly",
                    "SIGN DE",
                    "Cloud",
                    "Cloud",
                    tssSerial,
                    ReadString(
                        tssRoot,
                        "bsi_certification_id"),
                    null,
                    cfg.Endpoint,
                    "",
                    "",
                    "",
                    tssState,
                    SelfTestPassed: true,
                    ValidTime: true,
                    CtssActive: true));
        }
        catch (OperationCanceledException)
            when (!ct.IsCancellationRequested)
        {
            return new TseProbeResult(
                TseConnectionState.Error,
                "fiskaly Cloud-TSE Zeitüberschreitung.");
        }
        catch (Exception ex)
            when (ex is HttpRequestException or InvalidOperationException or JsonException)
        {
            return new TseProbeResult(
                TseConnectionState.Error,
                SanitizeError(ex.Message));
        }
    }

    public async Task<TseTransactionResult> StartTransactionAsync(
        TseTransactionStartRequest request,
        CancellationToken ct = default)
    {
        TseProviderCatalog.RequireProviderRelease(
            TseProviderCatalog.FiskalyDirectCloud);
        var cfg = RequireConfiguration();

        if (!string.Equals(
                request.ClientId?.Trim(),
                cfg.CashRegisterSerialNumber,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Cloud-TSE Start gehört nicht zur konfigurierten Kassen-Seriennummer.");
        }

        var txIdN =
            DirectCloudTransactionIdentity.RequireUuidV4(
                request.StableTransactionId);

        var txId = Guid.ParseExact(
                txIdN,
                "N")
            .ToString("D");

        if (request.ProcessData.Length != 0 ||
            !string.IsNullOrEmpty(request.ProcessType))
        {
            throw new InvalidOperationException(
                "fiskaly SIGN DE Start muss ohne processType/processData erfolgen.");
        }

        var body = new
        {
            state = "ACTIVE",
            client_id = CanonicalUuid(
                cfg.ProviderClientId,
                "Provider-Client-ID")
        };

        using var response = await PutTransactionAsync(
            cfg,
            txId,
            revision: 1,
            body,
            ct);

        var result = ParseTransactionResult(
            response.RootElement);

        _progress[result.TransactionNumber] =
            new TransactionProgress(
                1,
                "ACTIVE",
                StartFingerprint(),
                result);

        return result;
    }

    public Task<TseTransactionResult> UpdateTransactionAsync(
        TseTransactionUpdateRequest request,
        CancellationToken ct = default) =>
        MutateTransactionAsync(
            request.TransactionNumber,
            request.ClientId,
            request.ProcessType,
            request.ProcessData,
            targetState: "ACTIVE",
            ct);

    public Task<TseTransactionResult> FinishTransactionAsync(
        TseTransactionFinishRequest request,
        CancellationToken ct = default) =>
        MutateTransactionAsync(
            request.TransactionNumber,
            request.ClientId,
            request.ProcessType,
            request.ProcessData,
            targetState: "FINISHED",
            ct);

    public Task<TseExportResult> ExportAsync(
        string targetPath,
        CancellationToken ct = default)
    {
        TseProviderCatalog.RequireProviderRelease(
            TseProviderCatalog.FiskalyDirectCloud);

        throw new InvalidOperationException(
            "fiskaly Cloud-TSE TAR-Export ist noch nicht validiert und bleibt gesperrt.");
    }

    private async Task<TseTransactionResult> MutateTransactionAsync(
        ulong transactionNumber,
        string requestClientId,
        string processType,
        byte[] processData,
        string targetState,
        CancellationToken ct)
    {
        TseProviderCatalog.RequireProviderRelease(
            TseProviderCatalog.FiskalyDirectCloud);
        var cfg = RequireConfiguration();

        if (transactionNumber == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transactionNumber));
        }

        if (!string.Equals(
                requestClientId?.Trim(),
                cfg.CashRegisterSerialNumber,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Cloud-TSE Vorgang gehört nicht zur konfigurierten Kassen-Seriennummer.");
        }

        processType = (processType ?? "").Trim();
        if (processType.Length == 0)
        {
            throw new InvalidOperationException(
                "Cloud-TSE processType fehlt.");
        }

        processData ??= Array.Empty<byte>();

        var fingerprint = MutationFingerprint(
            targetState,
            processType,
            processData);

        var gate = _transactionGates.GetOrAdd(
            transactionNumber,
            _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(ct);
        try
        {
            if (_progress.TryGetValue(
                    transactionNumber,
                    out var known) &&
                string.Equals(
                    known.State,
                    targetState,
                    StringComparison.Ordinal) &&
                string.Equals(
                    known.Fingerprint,
                    fingerprint,
                    StringComparison.Ordinal))
            {
                return known.Result;
            }

            if (_pending.TryGetValue(
                    transactionNumber,
                    out var pending))
            {
                if (!string.Equals(
                        pending.State,
                        targetState,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        pending.Fingerprint,
                        fingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Cloud-TSE Vorgang hat eine ungeklärte vorherige Revision. Erst diesen Zustand auflösen.");
                }

                using var retried = await PutTransactionAsync(
                    cfg,
                    transactionNumber.ToString(
                        CultureInfo.InvariantCulture),
                    pending.Revision,
                    pending.Body,
                    ct);

                var retryResult =
                    ParseTransactionResult(
                        retried.RootElement);

                _pending.TryRemove(
                    transactionNumber,
                    out _);

                _progress[transactionNumber] =
                    new TransactionProgress(
                        pending.Revision,
                        targetState,
                        fingerprint,
                        retryResult);

                return retryResult;
            }

            var current = await ResolveProgressAsync(
                cfg,
                transactionNumber,
                ct);

            if (string.Equals(
                    current.State,
                    targetState,
                    StringComparison.Ordinal) &&
                string.Equals(
                    current.Fingerprint,
                    fingerprint,
                    StringComparison.Ordinal))
            {
                _progress[transactionNumber] = current;
                return current.Result;
            }

            if (string.Equals(
                    current.State,
                    "FINISHED",
                    StringComparison.Ordinal) ||
                string.Equals(
                    current.State,
                    "CANCELLED",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Cloud-TSE Vorgang ist bereits {current.State}.");
            }

            var nextRevision =
                checked(current.Revision + 1);

            var body = new
            {
                state = targetState,
                client_id = CanonicalUuid(
                    cfg.ProviderClientId,
                    "Provider-Client-ID"),
                schema = new
                {
                    raw = new
                    {
                        process_type = processType,
                        process_data =
                            Convert.ToBase64String(
                                processData)
                    }
                }
            };

            _pending[transactionNumber] =
                new PendingMutation(
                    nextRevision,
                    targetState,
                    fingerprint,
                    body);

            try
            {
                using var response =
                    await PutTransactionAsync(
                        cfg,
                        transactionNumber.ToString(
                            CultureInfo.InvariantCulture),
                        nextRevision,
                        body,
                        ct);

                var result =
                    ParseTransactionResult(
                        response.RootElement);

                _pending.TryRemove(
                    transactionNumber,
                    out _);

                _progress[transactionNumber] =
                    new TransactionProgress(
                        nextRevision,
                        targetState,
                        fingerprint,
                        result);

                return result;
            }
            catch
            {
                // Keep the exact pending revision/body in memory. A retry of
                // the same TOR operation resends the same idempotent request,
                // never a newly incremented revision.
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<TransactionProgress> ResolveProgressAsync(
        DirectCloudTseConfiguration cfg,
        ulong transactionNumber,
        CancellationToken ct)
    {
        if (_progress.TryGetValue(
                transactionNumber,
                out var known))
        {
            return known;
        }

        using var current =
            await GetJsonAsync(
                Relative(
                    "tss",
                    cfg.TssId,
                    "tx",
                    transactionNumber.ToString(
                        CultureInfo.InvariantCulture)),
                ct);

        var root = current.RootElement;
        var state = ReadString(
            root,
            "state");
        var revision = ReadInt64(
            root,
            "latest_revision");

        var fingerprint =
            ReadTransactionFingerprint(
                root,
                state);

        var result =
            ParseTransactionResult(root);

        return new TransactionProgress(
            revision,
            state,
            fingerprint,
            result);
    }

    private async Task<JsonDocument> PutTransactionAsync(
        DirectCloudTseConfiguration cfg,
        string transactionIdOrNumber,
        long revision,
        object body,
        CancellationToken ct)
    {
        return await SendAuthorizedJsonAsync(
            HttpMethod.Put,
            Relative(
                "tss",
                cfg.TssId,
                "tx",
                transactionIdOrNumber) +
            "?tx_revision=" +
            revision.ToString(
                CultureInfo.InvariantCulture),
            body,
            ct);
    }

    private async Task<JsonDocument> GetJsonAsync(
        string relative,
        CancellationToken ct) =>
        await SendAuthorizedJsonAsync(
            HttpMethod.Get,
            relative,
            body: null,
            ct);

    private async Task<JsonDocument> SendAuthorizedJsonAsync(
        HttpMethod method,
        string relative,
        object? body,
        CancellationToken ct)
    {
        TseProviderCatalog.RequireProviderRelease(
            TseProviderCatalog.FiskalyDirectCloud);
        var cfg = RequireConfiguration();

        var token = await GetTokenAsync(
            cfg,
            ct);

        var response = await SendOnceAsync(
            cfg,
            method,
            relative,
            body,
            token.AccessToken,
            ct);

        if (response.StatusCode ==
            HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            _token = null;

            token = await GetTokenAsync(
                cfg,
                ct,
                forceRefresh: true);

            response = await SendOnceAsync(
                cfg,
                method,
                relative,
                body,
                token.AccessToken,
                ct);
        }

        using (response)
        {
            var payload =
                await response.Content
                    .ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                throw ProviderFailure(
                    response,
                    payload);
            }

            try
            {
                return JsonDocument.Parse(payload);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "fiskaly Cloud-TSE lieferte keine gültige JSON-Antwort.",
                    ex);
            }
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        DirectCloudTseConfiguration cfg,
        HttpMethod method,
        string relative,
        object? body,
        string accessToken,
        CancellationToken ct)
    {
        using var request =
            new HttpRequestMessage(
                method,
                BuildUri(
                    cfg,
                    relative));

        request.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                accessToken);

        request.Headers.TryAddWithoutValidation(
            "request-id",
            Guid.NewGuid().ToString("D"));

        if (body is not null)
        {
            request.Content =
                JsonContent.Create(body);
        }

        using var timeout =
            LinkedTimeout(ct);

        return await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
    }

    private async Task<CachedToken> GetTokenAsync(
        DirectCloudTseConfiguration cfg,
        CancellationToken ct,
        bool forceRefresh = false)
    {
        var cached = _token;
        if (!forceRefresh &&
            cached is not null &&
            cached.ExpiresAt >
                DateTimeOffset.UtcNow.AddSeconds(45))
        {
            return cached;
        }

        await _authGate.WaitAsync(ct);
        try
        {
            cached = _token;
            if (!forceRefresh &&
                cached is not null &&
                cached.ExpiresAt >
                    DateTimeOffset.UtcNow.AddSeconds(45))
            {
                return cached;
            }

            var secret =
                await _credentials.GetAsync(ct);

            if (string.IsNullOrWhiteSpace(secret.ApiKey) ||
                string.IsNullOrWhiteSpace(secret.ApiSecret))
            {
                throw new InvalidOperationException(
                    "fiskaly API-Zugangsdaten fehlen.");
            }

            using var request =
                new HttpRequestMessage(
                    HttpMethod.Post,
                    BuildUri(
                        cfg,
                        "auth"))
                {
                    Content = JsonContent.Create(
                        new
                        {
                            api_key = secret.ApiKey,
                            api_secret = secret.ApiSecret
                        })
                };

            request.Headers.TryAddWithoutValidation(
                "request-id",
                Guid.NewGuid().ToString("D"));

            using var timeout =
                LinkedTimeout(ct);

            using var response =
                await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);

            var payload =
                await response.Content
                    .ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                throw ProviderFailure(
                    response,
                    payload);
            }

            using var json =
                JsonDocument.Parse(payload);

            var root = json.RootElement;
            var accessToken =
                ReadString(
                    root,
                    "access_token");

            if (accessToken.Length == 0)
            {
                throw new InvalidOperationException(
                    "fiskaly Authentifizierung lieferte kein Access-Token.");
            }

            var organizationId = "";
            if (root.TryGetProperty(
                    "access_token_claims",
                    out var claims))
            {
                organizationId =
                    ReadString(
                        claims,
                        "organization_id");
            }

            if (!string.Equals(
                    organizationId,
                    cfg.MandantId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "fiskaly Access-Token gehört nicht zum konfigurierten Mandanten.");
            }

            var expiresIn =
                ReadOptionalInt64(
                    root,
                    "access_token_expires_in") ??
                1200;

            var lifetimeSeconds =
                Math.Clamp(
                    expiresIn - 60,
                    60,
                    86_340);

            cached = new CachedToken(
                accessToken,
                DateTimeOffset.UtcNow
                    .AddSeconds(lifetimeSeconds));

            _token = cached;
            return cached;
        }
        finally
        {
            _authGate.Release();
        }
    }

    private DirectCloudTseConfiguration RequireConfiguration()
    {
        var cfg =
            DirectCloudTseConfigurationPolicy
                .RequireForFiscalUse(
                    _configuration);

        if (!string.Equals(
                cfg.ProviderId,
                TseProviderCatalog.FiskalyDirectCloud,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Fiskaly-Client wurde mit einem anderen Cloud-TSE-Provider konfiguriert.");
        }

        var endpoint = new Uri(
            cfg.Endpoint,
            UriKind.Absolute);

        if (!string.Equals(
                endpoint.Host,
                "kassensichv-middleware.fiskaly.com",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                endpoint.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase) ||
            !endpoint.AbsolutePath
                .TrimEnd('/')
                .EndsWith(
                    "/api/v2",
                    StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "fiskaly SIGN DE Signing darf nur über den offiziellen HTTPS Middleware-Endpunkt /api/v2 erfolgen.");
        }

        _ = CanonicalUuid(
            cfg.MandantId,
            "Mandant-ID");

        _ = CanonicalUuid(
            cfg.TssId,
            "TSS-ID");

        _ = CanonicalUuid(
            cfg.ProviderClientId,
            "Provider-Client-ID");

        var serial =
            cfg.CashRegisterSerialNumber;

        if (serial.Length > 70 ||
            serial.Contains('/') ||
            serial.Contains('_') ||
            !string.Equals(
                serial,
                serial.Trim(),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Kassen-Seriennummer erfüllt die SIGN-DE/DSFinV-K-Regeln nicht.");
        }

        return cfg;
    }

    private Uri BuildUri(
        DirectCloudTseConfiguration cfg,
        string relative)
    {
        var endpoint =
            cfg.Endpoint.TrimEnd('/') + "/";

        return new Uri(
            new Uri(endpoint),
            relative.TrimStart('/'));
    }

    private static string Relative(
        params string[] segments) =>
        string.Join(
            "/",
            segments.Select(
                Uri.EscapeDataString));

    private CancellationTokenSource LinkedTimeout(
        CancellationToken ct)
    {
        var timeout =
            CancellationTokenSource
                .CreateLinkedTokenSource(ct);

        timeout.CancelAfter(
            _requestTimeout);

        return timeout;
    }

    private static TseTransactionResult ParseTransactionResult(
        JsonElement root)
    {
        var number =
            ReadUInt64(
                root,
                "number");

        var serial =
            ReadString(
                root,
                "tss_serial_number");

        var signatureCounter = 0UL;
        var signature = "";
        if (root.TryGetProperty(
                "signature",
                out var signatureObject))
        {
            signature =
                ReadString(
                    signatureObject,
                    "value");

            signatureCounter =
                ReadUInt64Flexible(
                    signatureObject,
                    "counter");
        }

        DateTimeOffset? logTime = null;
        if (root.TryGetProperty(
                "log",
                out var log))
        {
            logTime =
                ReadTimestamp(
                    log,
                    "timestamp");
        }

        if (number == 0 ||
            serial.Length == 0 ||
            signatureCounter == 0 ||
            signature.Length == 0 ||
            logTime is null)
        {
            throw new InvalidOperationException(
                "fiskaly Cloud-TSE Antwort enthält keine vollständigen echten Fiskaldaten.");
        }

        return new TseTransactionResult(
            true,
            "fiskaly SIGN DE",
            number,
            signatureCounter,
            logTime,
            serial,
            signature);
    }

    private static string ReadTransactionFingerprint(
        JsonElement root,
        string state)
    {
        if (!root.TryGetProperty(
                "schema",
                out var schema) ||
            !schema.TryGetProperty(
                "raw",
                out var raw))
        {
            return string.Equals(
                    state,
                    "ACTIVE",
                    StringComparison.Ordinal)
                ? StartFingerprint()
                : "";
        }

        var processType =
            ReadString(
                raw,
                "process_type");

        var processData =
            ReadString(
                raw,
                "process_data");

        return MutationFingerprintBase64(
            state,
            processType,
            processData);
    }

    private static string StartFingerprint() =>
        "ACTIVE\u001fSTART";

    private static string MutationFingerprint(
        string state,
        string processType,
        byte[] processData) =>
        MutationFingerprintBase64(
            state,
            processType,
            Convert.ToBase64String(
                processData));

    private static string MutationFingerprintBase64(
        string state,
        string processType,
        string processDataBase64) =>
        string.Join(
            "\u001f",
            state,
            processType,
            processDataBase64);

    private static string CanonicalUuid(
        string value,
        string label)
    {
        value = (value ?? "").Trim();

        if (!Guid.TryParse(
                value,
                out var parsed))
        {
            throw new InvalidOperationException(
                $"{label} ist keine gültige UUID.");
        }

        return parsed.ToString("D");
    }

    private static ulong ReadUInt64(
        JsonElement root,
        string property)
    {
        if (!root.TryGetProperty(
                property,
                out var value))
        {
            return 0;
        }

        if (value.ValueKind ==
                JsonValueKind.Number &&
            value.TryGetUInt64(
                out var numeric))
        {
            return numeric;
        }

        if (value.ValueKind ==
                JsonValueKind.String &&
            ulong.TryParse(
                value.GetString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out numeric))
        {
            return numeric;
        }

        return 0;
    }

    private static ulong ReadUInt64Flexible(
        JsonElement root,
        string property) =>
        ReadUInt64(
            root,
            property);

    private static long ReadInt64(
        JsonElement root,
        string property)
    {
        if (!root.TryGetProperty(
                property,
                out var value))
        {
            return 0;
        }

        if (value.ValueKind ==
                JsonValueKind.Number &&
            value.TryGetInt64(
                out var numeric))
        {
            return numeric;
        }

        if (value.ValueKind ==
                JsonValueKind.String &&
            long.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out numeric))
        {
            return numeric;
        }

        return 0;
    }

    private static long? ReadOptionalInt64(
        JsonElement root,
        string property)
    {
        var value =
            ReadInt64(
                root,
                property);

        return value > 0
            ? value
            : null;
    }

    private static DateTimeOffset? ReadTimestamp(
        JsonElement root,
        string property)
    {
        if (!root.TryGetProperty(
                property,
                out var value))
        {
            return null;
        }

        if (value.ValueKind ==
                JsonValueKind.Number &&
            value.TryGetInt64(
                out var unix))
        {
            try
            {
                return unix > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(
                        unix)
                    : DateTimeOffset.FromUnixTimeSeconds(
                        unix);
            }
            catch
            {
                return null;
            }
        }

        if (value.ValueKind ==
            JsonValueKind.String)
        {
            var text =
                value.GetString();

            if (long.TryParse(
                    text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var numeric))
            {
                try
                {
                    return numeric > 10_000_000_000
                        ? DateTimeOffset.FromUnixTimeMilliseconds(
                            numeric)
                        : DateTimeOffset.FromUnixTimeSeconds(
                            numeric);
                }
                catch
                {
                    return null;
                }
            }

            if (DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string ReadString(
        JsonElement root,
        string property)
    {
        if (!root.TryGetProperty(
                property,
                out var value))
        {
            return "";
        }

        return value.ValueKind ==
            JsonValueKind.String
            ? value.GetString() ?? ""
            : value.ToString();
    }

    private static HttpRequestException ProviderFailure(
        HttpResponseMessage response,
        string payload)
    {
        var providerCode = "";
        var providerMessage = "";

        try
        {
            using var json =
                JsonDocument.Parse(payload);

            providerCode =
                ReadString(
                    json.RootElement,
                    "code");

            providerMessage =
                ReadString(
                    json.RootElement,
                    "message");
        }
        catch
        {
        }

        var retryAfter =
            response.Headers.RetryAfter?.Delta is { } delta
                ? $" · Retry-After {Math.Ceiling(delta.TotalSeconds):0}s"
                : "";

        var detail =
            string.Join(
                " · ",
                new[]
                {
                    providerCode,
                    providerMessage
                }
                .Where(x =>
                    !string.IsNullOrWhiteSpace(x)));

        if (detail.Length == 0)
            detail = response.ReasonPhrase ?? "Providerfehler";

        return new HttpRequestException(
            $"fiskaly SIGN DE HTTP {(int)response.StatusCode}: {SanitizeError(detail)}{retryAfter}",
            null,
            response.StatusCode);
    }

    private static string SanitizeError(
        string message)
    {
        message = (message ?? "").Trim();

        if (message.Length == 0)
            return "fiskaly Cloud-TSE nicht verfügbar.";

        return message.Length <= 300
            ? message
            : message[..300];
    }
}
