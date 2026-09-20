using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// R62 Google/Gmail integration. Authentication is paired through TOR POS Cloud by QR code.
/// The desktop never receives or stores the Google refresh token and never asks for a Gmail password.
/// TOR Cloud returns only a short-lived access token; report MIME data is sent directly from this PC
/// to the Gmail API and does not pass through TOR Cloud.
/// </summary>
public sealed class GoogleGmailService : IDisposable
{
    private readonly ISettingsRepository _settings;
    private readonly TorCloudSyncService _cloud;
    private readonly HttpClient _http;

    public GoogleGmailService(ISettingsRepository settings, TorCloudSyncService cloud, HttpMessageHandler? handler = null)
    {
        _settings = settings;
        _cloud = cloud;
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
    }

    public sealed record PairingSession(
        string PairId,
        string PairSecret,
        string DisplayUrl,
        DateTimeOffset ExpiresAt,
        IReadOnlyList<string> QrMatrix);

    public sealed record PairingStatus(string Status, string AccountEmail = "", string ConnectionId = "", string Error = "");
    public sealed record ConnectionState(bool Connected, string ConnectionId, string AccountEmail, string ConnectedAt);

    public async Task<PairingSession> StartPairingAsync(CancellationToken ct = default)
    {
        var reply = await _cloud.DeviceApiAsync("api/v1/devices/google-oauth/pair/start", new { }, ct);
        var pairId = GetString(reply, "pair_id");
        var pairSecret = GetString(reply, "pair_secret");
        var displayUrl = GetString(reply, "display_url");
        var expiresRaw = GetString(reply, "expires_at");
        if (!DateTimeOffset.TryParse(expiresRaw, out var expires))
            throw new InvalidDataException("Google-QR-Ablaufzeit fehlt.");
        if (!Uri.TryCreate(displayUrl, UriKind.Absolute, out var displayUri))
            throw new InvalidDataException("Google-QR-Link ist ungültig.");
        var localDisplayHost = displayUri.Host is "127.0.0.1" or "localhost" or "::1";
        if (!string.Equals(displayUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && !localDisplayHost)
            throw new InvalidDataException("Google-QR-Link muss HTTPS verwenden.");

        var matrix = new List<string>();
        if (!reply.TryGetProperty("qr_matrix", out var qr) || qr.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Google-QR-Matrix fehlt.");
        foreach (var row in qr.EnumerateArray())
        {
            var value = row.GetString() ?? "";
            if (value.Length != 41 || value.Any(ch => ch is not ('0' or '1')))
                throw new InvalidDataException("Google-QR-Matrix ist ungültig.");
            matrix.Add(value);
        }
        if (matrix.Count != 41)
            throw new InvalidDataException("Google-QR-Matrix ist unvollständig.");
        return new PairingSession(pairId, pairSecret, displayUri.ToString(), expires, matrix);
    }

    public async Task<PairingStatus> CheckPairingAsync(PairingSession pairing, CancellationToken ct = default)
    {
        var reply = await _cloud.DeviceApiAsync("api/v1/devices/google-oauth/pair/status", new
        {
            pair_id = pairing.PairId,
            pair_secret = pairing.PairSecret
        }, ct);
        var status = GetString(reply, "status").ToUpperInvariant();
        if (status == "PENDING") return new PairingStatus("PENDING");
        if (status == "ERROR") return new PairingStatus("ERROR", Error: GetString(reply, "error"));
        if (status != "COMPLETE") throw new InvalidDataException("Unbekannter Google-Anmeldestatus.");

        var connectionId = GetString(reply, "connection_id");
        var accountEmail = GetString(reply, "account_email");
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(accountEmail))
            throw new InvalidDataException("Google-Verbindungsdaten fehlen.");
        await _settings.SaveManyAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["reports.email.transport"] = "google",
            ["reports.email.google.connection_id"] = connectionId,
            ["reports.email.google.account_email"] = accountEmail,
            ["reports.email.google.connected_at"] = DateTimeOffset.Now.ToString("O")
        }, ct);
        try
        {
            await _cloud.DeviceApiAsync("api/v1/devices/google-oauth/pair/ack", new
            {
                pair_id = pairing.PairId,
                pair_secret = pairing.PairSecret
            }, ct);
        }
        catch
        {
            // The local connection was already committed. Pair rows expire server-side after 10 minutes.
        }
        return new PairingStatus("COMPLETE", accountEmail, connectionId);
    }

    public async Task<ConnectionState> GetConnectionAsync(CancellationToken ct = default)
    {
        var values = await _settings.LoadAllAsync(ct);
        var id = Get(values, "reports.email.google.connection_id");
        var email = Get(values, "reports.email.google.account_email");
        var at = Get(values, "reports.email.google.connected_at");
        return new ConnectionState(!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(email), id, email, at);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        var current = await GetConnectionAsync(ct);
        if (current.Connected)
        {
            await _cloud.DeviceApiAsync("api/v1/devices/google-oauth/disconnect", new { connection_id = current.ConnectionId }, ct);
        }
        await _settings.SaveManyAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["reports.email.google.connection_id"] = "",
            ["reports.email.google.account_email"] = "",
            ["reports.email.google.connected_at"] = "",
            ["reports.email.transport"] = "tor"
        }, ct);
    }

    public async Task SendTestAsync(string recipient, CancellationToken ct = default)
    {
        await SendAsync(recipient,
            "TOR POS · Google E-Mail-Test",
            "Diese Test-E-Mail bestätigt, dass TOR POS sicher über Google OAuth und die Gmail API senden kann. Ein Gmail-Passwort oder App-Passwort wird nicht verwendet.",
            Array.Empty<string>(), ct);
    }

    public async Task SendAsync(string recipient, string subject, string body, IReadOnlyList<string> attachments, CancellationToken ct = default)
    {
        var connection = await GetConnectionAsync(ct);
        if (!connection.Connected)
            throw new InvalidOperationException("Kein Google-Konto verbunden. Unter Berichte & E-Mail zuerst MIT GOOGLE ANMELDEN wählen.");

        var tokenReply = await _cloud.DeviceApiAsync("api/v1/devices/google-oauth/access-token", new { connection_id = connection.ConnectionId }, ct);
        var accessToken = GetString(tokenReply, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidDataException("Google-Zugriffstoken fehlt.");

        var mime = await MailMimeBuilder.BuildAsync(connection.AccountEmail, recipient, subject, body, attachments, ct);
        var raw = MailMimeBuilder.ToBase64Url(mime);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://gmail.googleapis.com/gmail/v1/users/me/messages/send");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new { raw });
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.IsSuccessStatusCode) return;

        var safe = "";
        try
        {
            var payload = await response.Content.ReadAsStringAsync(ct);
            using var json = JsonDocument.Parse(payload);
            if (json.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message))
                safe = message.GetString() ?? "";
        }
        catch { }
        if (safe.Length > 500) safe = safe[..500];
        if ((int)response.StatusCode is 401 or 403)
            throw new InvalidOperationException("Google hat den Versand nicht autorisiert. Bitte Google-Verbindung trennen und erneut per QR verbinden." + (string.IsNullOrWhiteSpace(safe) ? "" : "\nGoogle: " + safe));
        throw new InvalidOperationException($"Gmail API Versand fehlgeschlagen (HTTP {(int)response.StatusCode})." + (string.IsNullOrWhiteSpace(safe) ? "" : "\nGoogle: " + safe));
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) ? value.Trim() : "";

    private static string GetString(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";

    public void Dispose() => _http.Dispose();
}
