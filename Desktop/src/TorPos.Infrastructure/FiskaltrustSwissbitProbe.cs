using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TorPos.Infrastructure;

/// <summary>
/// R175: read-only inspection of an installed fiskaltrust Middleware setup that
/// uses the Swissbit SCU. This class deliberately does not initialize a TSE,
/// register a client, change PIN/PUK values or create fiscal transactions.
///
/// The probe only:
/// - reads the local fiskaltrust service configuration,
/// - checks the configured Swissbit drive for TSE_INFO.DAT,
/// - checks whether the SCU TCP endpoint is reachable,
/// - calls the Queue Echo endpoint, which has no fiscal side effect.
///
/// It never reads or logs the fiskaltrust access token.
/// </summary>
public sealed record FiskaltrustSwissbitProbeResult(
    bool ConfigurationFound,
    bool SwissbitConfigured,
    bool QueueReachable,
    bool ScuReachable,
    bool DeviceFilesPresent,
    string ConfigurationPath,
    string QueueEndpoint,
    string ScuEndpoint,
    string DevicePath,
    string SwissbitPackageVersion,
    string Message)
{
    public bool MiddlewareReachable => QueueReachable && ScuReachable;
}

public static class FiskaltrustSwissbitProbe
{
    public const string ProviderId = "FISKALTRUST_SWISSBIT";
    public const string EchoMarker = "TOR POS READONLY PROBE";

    public static async Task<FiskaltrustSwissbitProbeResult> ProbeAsync(
        CancellationToken ct = default)
    {
        var serviceDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "fiskaltrust",
            "service");

        if (!Directory.Exists(serviceDirectory))
        {
            return Missing(
                "Keine lokale fiskaltrust Middleware-Konfiguration gefunden.");
        }

        var configurationPath = Directory
            .EnumerateFiles(serviceDirectory, "Configuration-*.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(configurationPath))
        {
            return Missing(
                "Keine Configuration-*.json unter dem fiskaltrust Service-Verzeichnis gefunden.");
        }

        try
        {
            await using var stream = new FileStream(
                configurationPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.Asynchronous);

            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = document.RootElement;

            if (!TryFindSwissbitDevice(
                    root,
                    out var devicePath,
                    out var scuEndpoint,
                    out var swissbitVersion))
            {
                return new FiskaltrustSwissbitProbeResult(
                    true,
                    false,
                    false,
                    false,
                    false,
                    configurationPath,
                    "",
                    "",
                    "",
                    "",
                    "fiskaltrust-Konfiguration gefunden, aber keine Swissbit-SCU ist eingetragen.");
            }

            var queueEndpoint = FindQueueEndpoint(root);
            var deviceFilesPresent = HasTseInfo(devicePath);
            var scuReachable = await EndpointReachableAsync(scuEndpoint, ct);
            var queueReachable = await EchoAsync(queueEndpoint, ct);

            var message =
                queueReachable && scuReachable && deviceFilesPresent
                    ? "fiskaltrust Queue, Swissbit-SCU und TSE-Gerätepfad sind erreichbar. " +
                      "Dies ist nur ein Lese-/Verbindungstest; Client-Registrierung und Fiskaltransaktionen wurden nicht ausgeführt."
                    : BuildProblemMessage(
                        queueReachable,
                        scuReachable,
                        deviceFilesPresent,
                        devicePath);

            return new FiskaltrustSwissbitProbeResult(
                true,
                true,
                queueReachable,
                scuReachable,
                deviceFilesPresent,
                configurationPath,
                queueEndpoint,
                scuEndpoint,
                devicePath,
                swissbitVersion,
                message);
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            JsonException or
            InvalidOperationException)
        {
            return new FiskaltrustSwissbitProbeResult(
                true,
                false,
                false,
                false,
                false,
                configurationPath,
                "",
                "",
                "",
                "",
                "fiskaltrust-Konfiguration konnte nicht sicher gelesen werden: " + ex.Message);
        }
    }

    private static FiskaltrustSwissbitProbeResult Missing(string message) =>
        new(
            false,
            false,
            false,
            false,
            false,
            "",
            "",
            "",
            "",
            "",
            message);

    private static bool TryFindSwissbitDevice(
        JsonElement root,
        out string devicePath,
        out string endpoint,
        out string packageVersion)
    {
        devicePath = "";
        endpoint = "";
        packageVersion = "";

        if (!root.TryGetProperty("ftSignaturCreationDevices", out var devices) ||
            devices.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var device in devices.EnumerateArray())
        {
            var package = StringProperty(device, "Package");
            if (!package.Contains("Swissbit", StringComparison.OrdinalIgnoreCase))
                continue;

            packageVersion = StringProperty(device, "Version");
            endpoint = FirstUrl(device);

            if (device.TryGetProperty("Configuration", out var configuration) &&
                configuration.ValueKind == JsonValueKind.Object)
            {
                devicePath = StringProperty(configuration, "devicePath").Trim();
            }

            return true;
        }

        return false;
    }

    private static string FindQueueEndpoint(JsonElement root)
    {
        if (!root.TryGetProperty("ftQueues", out var queues) ||
            queues.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        foreach (var queue in queues.EnumerateArray())
        {
            var endpoint = FirstUrl(queue);
            if (!string.IsNullOrWhiteSpace(endpoint))
                return endpoint;
        }

        return "";
    }

    private static string FirstUrl(JsonElement element)
    {
        if (!element.TryGetProperty("Url", out var urls) ||
            urls.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        foreach (var url in urls.EnumerateArray())
        {
            if (url.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(url.GetString()))
            {
                return url.GetString()!;
            }
        }

        return "";
    }

    private static string StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool HasTseInfo(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
            return false;

        try
        {
            var root = devicePath.Trim();
            if (root.Length == 2 && root[1] == ':')
                root += Path.DirectorySeparatorChar;

            return File.Exists(Path.Combine(root, "TSE_INFO.DAT"));
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> EndpointReachableAsync(
        string endpoint,
        CancellationToken ct)
    {
        if (!TryEndpoint(endpoint, out var host, out var port))
            return false;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token);
            return client.Connected;
        }
        catch (Exception ex) when (
            ex is SocketException or
            OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> EchoAsync(
        string queueEndpoint,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(queueEndpoint))
            return false;

        var httpEndpoint = queueEndpoint.StartsWith(
                "rest://",
                StringComparison.OrdinalIgnoreCase)
            ? "http://" + queueEndpoint["rest://".Length..]
            : queueEndpoint;

        if (!Uri.TryCreate(
                httpEndpoint.TrimEnd('/') + "/json/v1/Echo",
                UriKind.Absolute,
                out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));

        try
        {
            using var client = new HttpClient();
            using var body = new StringContent(
                JsonSerializer.Serialize(new { Message = EchoMarker }),
                Encoding.UTF8,
                "application/json");
            using var response = await client.PostAsync(uri, body, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return false;

            var payload = await response.Content.ReadAsStringAsync(timeout.Token);
            return payload.Contains(EchoMarker, StringComparison.Ordinal);
        }
        catch (Exception ex) when (
            ex is HttpRequestException or
            OperationCanceledException)
        {
            return false;
        }
    }

    private static bool TryEndpoint(
        string endpoint,
        out string host,
        out int port)
    {
        host = "";
        port = 0;

        if (string.IsNullOrWhiteSpace(endpoint))
            return false;

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            uri.Port <= 0)
        {
            return false;
        }

        host = uri.Host;
        port = uri.Port;
        return true;
    }

    private static string BuildProblemMessage(
        bool queueReachable,
        bool scuReachable,
        bool deviceFilesPresent,
        string devicePath)
    {
        var problems = new List<string>();

        if (!queueReachable)
            problems.Add("Queue REST/Echo nicht erreichbar");

        if (!scuReachable)
            problems.Add("Swissbit-SCU nicht erreichbar");

        if (!deviceFilesPresent)
        {
            problems.Add(
                string.IsNullOrWhiteSpace(devicePath)
                    ? "kein devicePath konfiguriert"
                    : $"TSE_INFO.DAT unter {devicePath} nicht gefunden");
        }

        return "fiskaltrust-Prüfung: " + string.Join(" · ", problems) +
               ". Es wurden keine TSE-Schreiboperationen ausgeführt.";
    }
}
