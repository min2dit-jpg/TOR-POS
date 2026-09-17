using System.Globalization;
using System.Text.Json;
using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// R145: publishes the customer's digital receipt to TOR Cloud through the
/// authenticated device API (api.&lt;domain&gt;); the customer opens it on the
/// receipt domain (bon.&lt;domain&gt;) behind the token in the returned link.
///
/// Nothing fiscal happens here. The sale is final and signed before the choice
/// is offered, the records the law requires stay on the till, and nothing is
/// queued for later: a receipt the customer cannot receive now is of no use
/// later (AEAO zu § 146a Nr. 2.5.7), so a failure means the paper receipt.
/// </summary>
public sealed class CloudDigitalReceiptService : IDigitalReceiptPublisher
{
    public const string EnabledSetting = "receipt.digital_cloud.enabled";
    public const string Route = "api/v1/devices/receipts";

    private readonly ISettingsRepository _settings;
    private readonly Func<string, object, CancellationToken, Task<JsonElement>>? _deviceApi;
    private readonly Func<Task<bool>> _cloudActive;
    private readonly TimeSpan _timeout;

    public CloudDigitalReceiptService(ISettingsRepository settings, TorCloudSyncService? cloud)
        : this(
            settings,
            cloud is null ? null : (route, body, ct) => cloud.DeviceApiAsync(route, body, ct),
            cloud is null ? () => Task.FromResult(false) : async () => await cloud.ConfigurationAsync() is { Enabled: true },
            TimeSpan.FromSeconds(10))
    {
    }

    public CloudDigitalReceiptService(
        ISettingsRepository settings,
        Func<string, object, CancellationToken, Task<JsonElement>>? deviceApi,
        Func<Task<bool>> cloudActive,
        TimeSpan timeout)
    {
        _settings = settings;
        _deviceApi = deviceApi;
        _cloudActive = cloudActive;
        _timeout = timeout;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_deviceApi is null)
            return false;
        if (!string.Equals(await _settings.GetAsync(EnabledSetting, "false", ct), "true", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            return await _cloudActive();
        }
        catch
        {
            // An unreadable Cloud configuration just means no digital receipt.
            return false;
        }
    }

    public async Task<DigitalReceiptPublication> PublishAsync(DigitalReceiptDocument document, string reference, CancellationToken ct = default)
    {
        if (_deviceApi is null)
            throw new InvalidOperationException("TOR Cloud ist auf dieser Kasse nicht eingerichtet.");

        // R122: the printer's rule - no receipt without its mandatory fields.
        if (!document.TestReceipt && document.MissingFields.Count > 0)
            throw new InvalidOperationException("Digitaler Beleg gesperrt. Fiskal-Felder fehlen: " + string.Join(", ", document.MissingFields));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        JsonElement reply;
        try
        {
            reply = await _deviceApi(Route, new { receipt_ref = reference, receipt = document.ToCloudPayload() }, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("TOR Cloud hat den digitalen Beleg nicht rechtzeitig bestätigt.");
        }

        string Text(string name) =>
            reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";

        // The link becomes a QR code in front of the customer: only a receipt
        // link over HTTPS is shown, whatever else a server might answer.
        var url = Text("url");
        if (!DigitalReceiptLink.IsAcceptable(url))
            throw new InvalidDataException("TOR Cloud lieferte keinen gültigen Beleg-Link.");
        if (!DateTimeOffset.TryParse(Text("expires_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiresAt))
            throw new InvalidDataException("TOR Cloud lieferte kein Ablaufdatum für den Beleg-Link.");

        return new DigitalReceiptPublication(Text("receipt_id"), url, expiresAt);
    }
}
