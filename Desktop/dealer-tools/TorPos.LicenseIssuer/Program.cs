using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TorPos.Core;

var options = ParseArguments(args);

if (!Required("output", out var output))
{
    PrintUsage();
    return 2;
}

CommercialActivationRequest request;

if (options.TryGetValue("request", out var requestPath))
{
    try
    {
        request = JsonSerializer.Deserialize<CommercialActivationRequest>(
            File.ReadAllText(requestPath, Encoding.UTF8),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Aktivierungsanfrage ist leer.");
    }
    catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
    {
        Console.Error.WriteLine("Aktivierungsanfrage kann nicht gelesen werden: " + ex.Message);
        return 2;
    }
}
else
{
    if (!Required("customer-no", out var customerNumber) ||
        !Required("customer-name", out var customerName) ||
        !Required("installation", out var installation) ||
        !Required("device", out var deviceCode) ||
        !Required("edition", out var manualEdition))
    {
        PrintUsage();
        return 2;
    }

    request = new CommercialActivationRequest(
        "TOR POS Pro",
        "0.7.19",
        customerNumber,
        customerName,
        installation,
        deviceCode,
        manualEdition,
        DateTimeOffset.UtcNow);
}

var normalizedCustomerNumber = NormalizeCustomerNumber(request.CustomerNumber);
var edition = request.Edition?.Trim().ToUpperInvariant() ?? "";
var installationId = request.InstallationId?.Trim().ToUpperInvariant() ?? "";
var device = request.DeviceCode?.Trim().ToUpperInvariant() ?? "";

if (normalizedCustomerNumber.Length < 3)
{
    Console.Error.WriteLine("Die Aktivierungsanfrage enthält keine gültige Kunden-Nr.");
    return 2;
}

if (edition is not ("KIOSK" or "IMBISS"))
{
    Console.Error.WriteLine("Edition muss KIOSK oder IMBISS sein.");
    return 2;
}

if (!installationId.StartsWith("TOR-", StringComparison.Ordinal) ||
    !device.StartsWith("PC-", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Installations-ID oder PC-Gerätecode ist ungültig.");
    return 2;
}

var days = 365;
if (options.TryGetValue("days", out var dayText) &&
    (!int.TryParse(dayText, out days) || days < 1 || days > 3650))
{
    Console.Error.WriteLine("--days muss zwischen 1 und 3650 liegen.");
    return 2;
}

var keyPath = options.TryGetValue("key", out var configuredKey)
    ? configuredKey
    : Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "TOR-POS-LICENSE-PRIVATE.pem"));

if (!File.Exists(keyPath))
{
    Console.Error.WriteLine("Privater Lizenzschlüssel nicht gefunden: " + keyPath);
    return 3;
}

var now = DateTimeOffset.UtcNow;
var payload = new CommercialLicensePayload(
    Guid.NewGuid().ToString("N").ToUpperInvariant(),
    normalizedCustomerNumber,
    request.CustomerName?.Trim() ?? "",
    installationId,
    device,
    edition,
    now,
    now.AddDays(days),
    ["COMMERCIAL_USE", $"EDITION_{edition}"]);

var payloadJson = JsonSerializer.Serialize(
    payload,
    new JsonSerializerOptions { WriteIndented = false });

using var rsa = RSA.Create();
rsa.ImportFromPem(File.ReadAllText(keyPath, Encoding.UTF8));

var signature = rsa.SignData(
    Encoding.UTF8.GetBytes(payloadJson),
    HashAlgorithmName.SHA256,
    RSASignaturePadding.Pkcs1);

var envelope = new CommercialLicenseEnvelope(
    payloadJson,
    Convert.ToBase64String(signature));

var outputPath = Path.GetFullPath(output);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
File.WriteAllText(
    outputPath,
    JsonSerializer.Serialize(
        envelope,
        new JsonSerializerOptions { WriteIndented = true }),
    new UTF8Encoding(false));

Console.WriteLine("Lizenz erstellt: " + outputPath);
Console.WriteLine("Kunden-Nr.: " + payload.CustomerNumber);
Console.WriteLine("Kunde: " + payload.CustomerName);
Console.WriteLine("PC-Gerätecode: " + payload.DeviceCode);
Console.WriteLine("Edition: " + payload.Edition);
Console.WriteLine("Gültig bis: " + payload.ValidUntilUtc.ToString("O"));
return 0;

bool Required(string name, out string value)
{
    if (options.TryGetValue(name, out var found) && !string.IsNullOrWhiteSpace(found))
    {
        value = found;
        return true;
    }

    Console.Error.WriteLine("Fehlender Parameter: --" + name);
    value = "";
    return false;
}

static Dictionary<string,string> ParseArguments(string[] values)
{
    var result = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < values.Length - 1; i += 2)
    {
        if (!values[i].StartsWith("--", StringComparison.Ordinal))
            continue;
        result[values[i][2..]] = values[i + 1];
    }
    return result;
}

static string NormalizeCustomerNumber(string? value) =>
    new(
        (value ?? "").Trim()
            .ToUpperInvariant()
            .Where(x => char.IsLetterOrDigit(x) || x is '-' or '_')
            .Take(40)
            .ToArray());

static void PrintUsage()
{
    Console.WriteLine("TOR POS License Issuer");
    Console.WriteLine("dotnet run -- --request Aktivierungsanfrage.json --days 365 --key PRIVATE.pem --output Kundenlizenz.json");
}
