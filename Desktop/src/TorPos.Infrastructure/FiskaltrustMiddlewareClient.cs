using System.Net.Http.Json;
using System.Text.Json;

namespace TorPos.Infrastructure;

public interface IFiskaltrustMiddlewareClient
{
    Task<string> EchoAsync(string message, CancellationToken ct = default);

    Task<FiskaltrustReceiptResponse> SignAsync(
        FiskaltrustReceiptRequest request,
        CancellationToken ct = default);

    Task<FiskaltrustReceiptResponse?> RecoverAsync(
        FiskaltrustReceiptRequest originalRequest,
        CancellationToken ct = default);
}

/// <summary>
/// HTTP/JSON client for fiskaltrust Middleware API v1.
///
/// Local Windows Middleware example:
///   http://localhost:14002/rest/json/v1/
///
/// The BaseUri is deliberately configurable because the portal/Launcher can
/// assign a different host, port or path. For SaaS/CloudCashbox, CashBox ID and
/// access token are sent as headers as required by the official API.
/// </summary>
public sealed class FiskaltrustMiddlewareClient : IFiskaltrustMiddlewareClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly FiskaltrustMiddlewareOptions _options;

    public FiskaltrustMiddlewareClient(
        HttpClient http,
        FiskaltrustMiddlewareOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (!_options.BaseUri.IsAbsoluteUri)
            throw new ArgumentException("fiskaltrust BaseUri muss absolut sein.", nameof(options));

        if (_options.UseSaasHeaders)
        {
            if (_options.CashBoxId == Guid.Empty)
                throw new ArgumentException("fiskaltrust CashBox-ID fehlt für SaaS/CloudCashbox.", nameof(options));

            if (string.IsNullOrWhiteSpace(_options.AccessToken))
                throw new ArgumentException("fiskaltrust AccessToken fehlt für SaaS/CloudCashbox.", nameof(options));
        }
    }

    public async Task<string> EchoAsync(
        string message,
        CancellationToken ct = default)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            _options.Endpoint("json/v1/Echo"),
            JsonContent.Create(
                new FiskaltrustEchoRequest(message ?? ""),
                options: JsonOptions));

        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);

        var payload = await response.Content.ReadFromJsonAsync<FiskaltrustEchoResponse>(
            JsonOptions,
            ct);

        return payload?.Message ?? "";
    }

    public async Task<FiskaltrustReceiptResponse> SignAsync(
        FiskaltrustReceiptRequest request,
        CancellationToken ct = default)
    {
        var normalized = NormalizeFiscalRequest(request);

        using var httpRequest = CreateRequest(
            HttpMethod.Post,
            _options.Endpoint("json/v1/Sign"),
            JsonContent.Create(normalized, options: JsonOptions));

        using var response = await _http.SendAsync(httpRequest, ct);
        await EnsureSuccessAsync(response, ct);

        var result = await response.Content.ReadFromJsonAsync<FiskaltrustReceiptResponse>(
            JsonOptions,
            ct);

        return result
            ?? throw new InvalidOperationException(
                "fiskaltrust Sign lieferte keine lesbare ReceiptResponse.");
    }

    /// <summary>
    /// Recovery for an ambiguous Sign outcome. fiskaltrust requires the same
    /// cbReceiptReference and the same charge/pay items; only the documented
    /// ReceiptRequest flag is added. A null JSON response means no matching
    /// processed receipt was found and is deliberately returned as null.
    /// </summary>
    public async Task<FiskaltrustReceiptResponse?> RecoverAsync(
        FiskaltrustReceiptRequest originalRequest,
        CancellationToken ct = default)
    {
        var normalized = NormalizeFiscalRequest(originalRequest);
        var recoveryRequest = normalized with
        {
            FtReceiptCase =
                FiskaltrustDeCases.WithReceiptRequest(normalized.FtReceiptCase)
        };

        using var httpRequest = CreateRequest(
            HttpMethod.Post,
            _options.Endpoint("json/v1/Sign"),
            JsonContent.Create(recoveryRequest, options: JsonOptions));

        using var response = await _http.SendAsync(httpRequest, ct);
        await EnsureSuccessAsync(response, ct);

        return await response.Content.ReadFromJsonAsync<FiskaltrustReceiptResponse>(
            JsonOptions,
            ct);
    }

    private FiskaltrustReceiptRequest NormalizeFiscalRequest(
        FiskaltrustReceiptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_options.CashBoxId == Guid.Empty)
            throw new InvalidOperationException("fiskaltrust CashBox-ID fehlt für Sign.");

        if (_options.PosSystemId == Guid.Empty)
            throw new InvalidOperationException("fiskaltrust POS-System-ID fehlt für Sign.");

        if (string.IsNullOrWhiteSpace(_options.TerminalId))
            throw new InvalidOperationException("fiskaltrust Terminal-ID fehlt für Sign.");

        var normalized = request with
        {
            FtCashBoxId = string.IsNullOrWhiteSpace(request.FtCashBoxId)
                ? _options.CashBoxId.ToString()
                : request.FtCashBoxId,
            FtPosSystemId = string.IsNullOrWhiteSpace(request.FtPosSystemId)
                ? _options.PosSystemId.ToString()
                : request.FtPosSystemId,
            CbTerminalId = string.IsNullOrWhiteSpace(request.CbTerminalId)
                ? _options.TerminalId
                : request.CbTerminalId
        };

        if (string.IsNullOrWhiteSpace(normalized.CbReceiptReference))
            throw new ArgumentException("cbReceiptReference fehlt.", nameof(request));

        return normalized;
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        Uri uri,
        HttpContent content)
    {
        var request = new HttpRequestMessage(method, uri)
        {
            Content = content
        };

        if (_options.UseSaasHeaders)
        {
            request.Headers.TryAddWithoutValidation(
                "cashboxid",
                _options.CashBoxId.ToString());

            request.Headers.TryAddWithoutValidation(
                "accesstoken",
                _options.AccessToken);
        }

        return request;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        if (body.Length > 2_000)
            body = body[..2_000];

        throw new HttpRequestException(
            $"fiskaltrust HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {body}",
            inner: null,
            response.StatusCode);
    }
}
