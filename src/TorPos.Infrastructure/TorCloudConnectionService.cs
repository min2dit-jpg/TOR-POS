using System.Net.Http.Headers;
using System.Text.Json;
using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// R38 foundation: narrow TOR Cloud connectivity boundary.
/// It does not read sales, does not alter fiscal data and does not run in the background yet.
/// </summary>
public sealed class TorCloudConnectionService
{
    public async Task<string> PingAsync(
        string baseUrl,
        string deviceCode,
        string deviceToken,
        CancellationToken ct = default)
    {
        baseUrl = (baseUrl ?? "").Trim();
        deviceCode = (deviceCode ?? "").Trim();
        deviceToken = (deviceToken ?? "").Trim();

        if (baseUrl.Length == 0)
            throw new InvalidOperationException("TOR Cloud Server-URL fehlt.");
        if (deviceCode.Length == 0)
            throw new InvalidOperationException("TOR Cloud Gerätecode fehlt.");
        if (deviceToken.Length == 0)
            throw new InvalidOperationException("TOR Cloud Gerätetoken fehlt.");

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var root) ||
            (root.Scheme != Uri.UriSchemeHttps && root.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException("TOR Cloud Server-URL ist ungültig.");

        // HTTP is allowed only for the local development loopback test.
        if (root.Scheme == Uri.UriSchemeHttp &&
            root.Host is not ("127.0.0.1" or "localhost" or "::1"))
            throw new InvalidOperationException("TOR Cloud benötigt HTTPS. HTTP ist nur für localhost-Tests erlaubt.");

        var endpoint = new Uri(new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"), "api/v1/devices/ping");
        using var handler = new HttpClientHandler
        {
            // Device credentials must never be forwarded to a redirected host.
            AllowAutoRedirect = false
        };
        using var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Add("X-Device-Code", deviceCode);
        request.Headers.Add("X-Device-Token", deviceToken);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("TOR-POS-Pro", TorRelease.UserAgentVersion));

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = ExtractMessage(body);
            throw new InvalidOperationException(
                $"TOR Cloud antwortet mit {(int)response.StatusCode} {response.ReasonPhrase}" +
                (detail.Length > 0 ? $": {detail}" : "."));
        }

        return "TOR Cloud erreichbar · Gerät autorisiert";
    }

    private static string ExtractMessage(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var value))
                return value.GetString() ?? "";
        }
        catch
        {
            // Response detail is optional; never expose arbitrary HTML to the operator.
        }
        return "";
    }
}
