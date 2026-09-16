using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TorPos.Infrastructure;

public sealed record SumUpReader(string Id, string Name, string PairingStatus)
{
    public override string ToString() => $"{Name} · {Id} · {PairingStatus}";
}

public sealed record SumUpTestCheckout(string CheckoutId, string ClientTransactionId, long AmountCents);

// R37 test-only SumUp integration.
// Reader listing/pairing/status remain unchanged. The only payment-capable method sends
// a fixed EUR 1.00 checkout for a physical device test and is not wired into the sales flow.
public sealed class SumUpConnectionService : IDisposable
{
    private readonly HttpClient _http;
    public SumUpConnectionService() : this(new HttpClientHandler { AllowAutoRedirect = false }) { }
    internal SumUpConnectionService(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1024 * 1024 };
    }

    private static string Segment(string value)
    {
        value = value.Trim();
        if (!Regex.IsMatch(value, "^[A-Za-z0-9_-]{1,80}$"))
            throw new ArgumentException("Händlercode / Reader-ID prüfen: nur Buchstaben, Ziffern, '-' und '_'.");
        return value;
    }

    private static string ApiKey(string key)
    {
        key = key.Trim();
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsWhiteSpace))
            throw new ArgumentException("Gültigen SumUp API-Key eingeben (kein Passwort).");
        return key;
    }

    private static string ErrorDetail(System.Net.HttpStatusCode statusCode) => (int)statusCode switch
    {
        401 => "API-Key ungültig oder abgelaufen.",
        403 => "Keine Berechtigung für diesen Händler / die Readers API. API-Key mit readers.read/readers.write prüfen.",
        404 => "Händler oder Reader nicht gefunden.",
        409 => "Reader ist beschäftigt oder eine andere Zahlungsanforderung läuft bereits.",
        400 or 422 => "SumUp hat die Anfrage abgelehnt. Händlercode, Reader und Eingaben prüfen.",
        429 => "Zu viele Anfragen. Später manuell erneut prüfen.",
        _ => "SumUp-Anfrage fehlgeschlagen. Verbindung später erneut prüfen."
    };

    private HttpRequestMessage Request(string merchant, string key, HttpMethod method, string suffix, object? body)
    {
        var code = Segment(merchant);
        var request = new HttpRequestMessage(method, "https://api.sumup.com/v0.1/merchants/" + code + "/readers" + suffix);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey(key));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private async Task<JsonDocument> SendJsonAsync(string merchant, string key, HttpMethod method, string suffix, object? body, CancellationToken ct)
    {
        using var request = Request(merchant, key, method, suffix, body);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Do not expose response bodies, which may echo credentials or submitted values.
            throw new InvalidOperationException($"SumUp HTTP {(int)response.StatusCode}: {ErrorDetail(response.StatusCode)}");
        }
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    private async Task SendNoContentAsync(string merchant, string key, HttpMethod method, string suffix, CancellationToken ct)
    {
        using var request = Request(merchant, key, method, suffix, null);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Do not expose response bodies, which may echo credentials or submitted values.
            throw new InvalidOperationException($"SumUp HTTP {(int)response.StatusCode}: {ErrorDetail(response.StatusCode)}");
        }
    }

    private static string Read(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    public async Task<IReadOnlyList<SumUpReader>> ListAsync(string merchant, string key, CancellationToken ct = default)
    {
        using var json = await SendJsonAsync(merchant, key, HttpMethod.Get, "", null, ct);
        var items = json.RootElement.GetProperty("items");
        return items.EnumerateArray().Select(x => new SumUpReader(Read(x, "id"), Read(x, "name"), Read(x, "status"))).ToArray();
    }

    public async Task<SumUpReader> PairAsync(string merchant, string key, string pairingCode, CancellationToken ct = default)
    {
        var code = pairingCode.Trim();
        if (!Regex.IsMatch(code, "^[A-Za-z0-9]{8,9}$")) throw new ArgumentException("Kopplungscode vom Solo eingeben.");
        using var json = await SendJsonAsync(merchant, key, HttpMethod.Post, "", new { pairing_code = code, name = "TOR POS Solo" }, ct);
        var root = json.RootElement;
        return new SumUpReader(Read(root, "id"), Read(root, "name"), Read(root, "status"));
    }

    public async Task<string> StatusAsync(string merchant, string key, string reader, CancellationToken ct = default)
    {
        using var json = await SendJsonAsync(merchant, key, HttpMethod.Get, "/" + Segment(reader) + "/status", null, ct);
        var data = json.RootElement.GetProperty("data");
        return $"SumUp meldet: {Read(data, "status")} · {Read(data, "state")}\n" +
            $"Verbindung: {Read(data, "connection_type")}\nLetzte Geräteaktivität: {Read(data, "last_activity")}\n" +
            $"Abfrage: {DateTimeOffset.Now:dd.MM.yyyy HH:mm:ss}\nLetzter bekannter Gerätestatus; keine Zahlung gestartet.";
    }

    // Device test only. This sends a REAL payment request of exactly EUR 1.00 to the Solo.
    // Do not present a card. Cancel it on the Solo or via TerminateCheckoutAsync immediately.
    public async Task<SumUpTestCheckout> StartOneEuroDeviceTestAsync(string merchant, string key, string reader, CancellationToken ct = default)
    {
        var readerId = Segment(reader);
        var body = new
        {
            total_amount = new
            {
                currency = "EUR",
                minor_unit = 2,
                value = 100
            }
        };
        using var json = await SendJsonAsync(merchant, key, HttpMethod.Post, "/" + readerId + "/checkout", body, ct);
        var data = json.RootElement.GetProperty("data");
        var checkoutId = Read(data, "checkout_id");
        var clientTransactionId = Read(data, "client_transaction_id");
        if (string.IsNullOrWhiteSpace(checkoutId))
            throw new InvalidOperationException("SumUp hat die Testanforderung angenommen, aber keine Checkout-ID geliefert.");
        return new SumUpTestCheckout(checkoutId, clientTransactionId, 100);
    }

    public Task TerminateCheckoutAsync(string merchant, string key, string reader, CancellationToken ct = default) =>
        SendNoContentAsync(merchant, key, HttpMethod.Post, "/" + Segment(reader) + "/terminate", ct);

    public void Dispose() => _http.Dispose();
}
