using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TorPos.Infrastructure;

// R176: typed, local-only fiskaltrust Middleware v1 REST client.
// Official DE flow:
//   POST .../json/v1/Sign
//   Start transaction = 0x4445000000000008
//   POS receipt / explicit finish = 0x4445000000000001
// This client never reads or sends an access token. It is intentionally
// limited to a locally hosted on-premise Queue endpoint.
public static class FiskaltrustDeCases
{
    public const long PosReceipt = 0x4445000000000001L;
    public const long StartTransaction = 0x4445000000000008L;
    public const long UpdateTransaction = 0x4445000000000009L;
    public const long DeltaTransaction = 0x444500000000000AL;
    public const long FailTransaction = 0x444500000000000BL;

    public const long ChargeNormal19 = 0x4445000000000001L;
    public const long ChargeReduced7 = 0x4445000000000002L;
    public const long ChargeZero = 0x4445000000000006L;
    public const long ChargeDiscount19 = 0x4445000000000031L;
    public const long ChargeDiscount7 = 0x4445000000000032L;
    public const long ChargeDiscountZero = 0x4445000000000036L;
    public const long ChargeReturnable19 = 0x4445000000000021L;
    public const long ChargeReturnable7 = 0x4445000000000022L;
    public const long ChargeReturnableZero = 0x4445000000000026L;
    public const long TakeAwayFlag = 0x0000000000010000L;

    public const long PayCashEur = 0x4445000000000001L;
    public const long PayDebitCard = 0x4445000000000004L;
    public const long PayCreditCard = 0x4445000000000005L;
    public const long PayOnline = 0x4445000000000006L;
    public const long PayCustomerCard = 0x4445000000000007L;

    public const long SignatureQr = 0x4445000000000001L;
    public const long SignatureTransactionNumber = 0x4445000000000017L;
    public const long SignatureCounter = 0x4445000000000018L;
    public const long SignatureStartTime = 0x4445000000000019L;
    public const long SignatureLogTime = 0x444500000000001AL;
    public const long SignatureValue = 0x444500000000001DL;
    public const long SignatureTseSerial = 0x4445000000000023L;

    public static long ChargeForVat(decimal vatRate) => vatRate switch
    {
        19m => ChargeNormal19,
        7m => ChargeReduced7,
        0m => ChargeZero,
        _ => throw new InvalidOperationException(
            $"fiskaltrust: MwSt-Satz {vatRate.ToString(CultureInfo.InvariantCulture)} % ist nicht freigegeben.")
    };

    public static long DiscountForVat(decimal vatRate) => vatRate switch
    {
        19m => ChargeDiscount19,
        7m => ChargeDiscount7,
        0m => ChargeDiscountZero,
        _ => throw new InvalidOperationException(
            $"fiskaltrust: Rabatt-MwSt-Satz {vatRate.ToString(CultureInfo.InvariantCulture)} % ist nicht freigegeben.")
    };

    public static long ReturnableForVat(decimal vatRate) => vatRate switch
    {
        19m => ChargeReturnable19,
        7m => ChargeReturnable7,
        0m => ChargeReturnableZero,
        _ => throw new InvalidOperationException(
            $"fiskaltrust: Pfand-MwSt-Satz {vatRate.ToString(CultureInfo.InvariantCulture)} % ist nicht freigegeben.")
    };
}

public enum FiskaltrustCardTender
{
    DebitCard,
    CreditCard,
    Online,
    CustomerCard
}

public sealed record FiskaltrustLocalQueueConfiguration(
    string ConfigurationPath,
    string CashBoxId,
    string QueueId,
    string QueueEndpoint)
{
    public static async Task<FiskaltrustLocalQueueConfiguration?> LoadAsync(
        CancellationToken ct = default)
    {
        var serviceDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "fiskaltrust",
            "service");

        if (!Directory.Exists(serviceDirectory))
            return null;

        var configurationPath = Directory
            .EnumerateFiles(serviceDirectory, "Configuration-*.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(configurationPath))
            return null;

        await using var stream = new FileStream(
            configurationPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous);

        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = document.RootElement;

        var cashBoxId = StringProperty(root, "ftCashBoxId", "ftCashBoxID");
        if (!TryProperty(root, out var queues, "ftQueues") ||
            queues.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var queue in queues.EnumerateArray())
        {
            var id = StringProperty(queue, "Id", "id");
            var endpoint = FirstUrl(queue);
            if (!string.IsNullOrWhiteSpace(id) &&
                endpoint.StartsWith("rest://", StringComparison.OrdinalIgnoreCase))
            {
                return new(
                    configurationPath,
                    cashBoxId,
                    id,
                    endpoint);
            }
        }

        return null;
    }

    private static string FirstUrl(JsonElement element)
    {
        if (!TryProperty(element, out var urls, "Url", "url") ||
            urls.ValueKind != JsonValueKind.Array)
            return "";

        foreach (var url in urls.EnumerateArray())
            if (url.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(url.GetString()))
                return url.GetString()!;

        return "";
    }

    private static string StringProperty(JsonElement element, params string[] names)
    {
        return TryProperty(element, out var value, names) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static bool TryProperty(
        JsonElement element,
        out JsonElement value,
        params string[] names)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (names.Any(n => string.Equals(
                    property.Name,
                    n,
                    StringComparison.OrdinalIgnoreCase)))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

public sealed record FiskaltrustChargeItem(
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("quantity")] decimal Quantity,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("vatRate")] decimal VatRate,
    [property: JsonPropertyName("ftChargeItemCase")] long FtChargeItemCase,
    [property: JsonPropertyName("productGroup")] string ProductGroup,
    [property: JsonPropertyName("productNumber")] string ProductNumber,
    [property: JsonPropertyName("productBarcode")] string ProductBarcode,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("unitQuantity")] decimal UnitQuantity,
    [property: JsonPropertyName("unitPrice")] decimal UnitPrice,
    [property: JsonPropertyName("moment")] DateTimeOffset Moment);

public sealed record FiskaltrustPayItem(
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("quantity")] decimal Quantity,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("ftPayItemCase")] long FtPayItemCase,
    [property: JsonPropertyName("moneyGroup")] string MoneyGroup,
    [property: JsonPropertyName("moneyNumber")] string MoneyNumber,
    [property: JsonPropertyName("moment")] DateTimeOffset Moment);

public sealed record FiskaltrustReceiptRequest(
    [property: JsonPropertyName("ftCashBoxID")] string FtCashBoxId,
    [property: JsonPropertyName("ftQueueID")] string FtQueueId,
    [property: JsonPropertyName("ftPosSystemId")] string FtPosSystemId,
    [property: JsonPropertyName("cbTerminalID")] string CbTerminalId,
    [property: JsonPropertyName("cbReceiptReference")] string CbReceiptReference,
    [property: JsonPropertyName("cbReceiptMoment")] DateTimeOffset CbReceiptMoment,
    [property: JsonPropertyName("cbChargeItems")] IReadOnlyList<FiskaltrustChargeItem> CbChargeItems,
    [property: JsonPropertyName("cbPayItems")] IReadOnlyList<FiskaltrustPayItem> CbPayItems,
    [property: JsonPropertyName("ftReceiptCase")] long FtReceiptCase,
    [property: JsonPropertyName("ftReceiptCaseData")] string FtReceiptCaseData,
    [property: JsonPropertyName("cbReceiptAmount")] decimal CbReceiptAmount,
    [property: JsonPropertyName("cbUser")] string CbUser,
    [property: JsonPropertyName("cbArea")] string CbArea);

public sealed record FiskaltrustSignature(
    [property: JsonPropertyName("ftSignatureFormat")] long FtSignatureFormat,
    [property: JsonPropertyName("ftSignatureType")] long FtSignatureType,
    [property: JsonPropertyName("caption")] string Caption,
    [property: JsonPropertyName("data")] string Data);

public sealed record FiskaltrustReceiptResponse(
    [property: JsonPropertyName("ftCashBoxID")] string FtCashBoxId,
    [property: JsonPropertyName("ftQueueID")] string FtQueueId,
    [property: JsonPropertyName("ftQueueItemID")] string FtQueueItemId,
    [property: JsonPropertyName("ftQueueRow")] long FtQueueRow,
    [property: JsonPropertyName("cbTerminalID")] string CbTerminalId,
    [property: JsonPropertyName("cbReceiptReference")] string CbReceiptReference,
    [property: JsonPropertyName("ftCashBoxIdentification")] string FtCashBoxIdentification,
    [property: JsonPropertyName("ftReceiptIdentification")] string FtReceiptIdentification,
    [property: JsonPropertyName("ftReceiptMoment")] DateTimeOffset FtReceiptMoment,
    [property: JsonPropertyName("ftSignatures")] IReadOnlyList<FiskaltrustSignature> FtSignatures,
    [property: JsonPropertyName("ftState")] long FtState,
    [property: JsonPropertyName("ftStateData")] string FtStateData);

public sealed record FiskaltrustFiscalResult(
    bool Success,
    string ReceiptReference,
    ulong TransactionNumber,
    ulong SignatureCounter,
    string TseSerialNumber,
    string Signature,
    DateTimeOffset? StartLogTime,
    DateTimeOffset? LogTime,
    string QrData,
    string Message);

public sealed class FiskaltrustQueueClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Uri _signEndpoint;

    public FiskaltrustLocalQueueConfiguration Configuration { get; }

    public FiskaltrustQueueClient(
        FiskaltrustLocalQueueConfiguration configuration,
        HttpClient? httpClient = null)
    {
        Configuration = configuration ??
            throw new ArgumentNullException(nameof(configuration));

        if (string.IsNullOrWhiteSpace(
                configuration.CashBoxId))
        {
            throw new InvalidOperationException(
                "fiskaltrust CashBox/Mandant-ID fehlt.");
        }

        if (string.IsNullOrWhiteSpace(
                configuration.QueueId))
        {
            throw new InvalidOperationException(
                "fiskaltrust Queue-ID fehlt.");
        }

        _signEndpoint = BuildEndpoint(
            configuration.QueueEndpoint,
            "json/v1/Sign");

        _http = httpClient ?? new HttpClient();
        _ownsHttp = httpClient is null;
    }

    public static async Task<FiskaltrustQueueClient?> TryCreateLocalAsync(
        CancellationToken ct = default)
    {
        var configuration =
            await FiskaltrustLocalQueueConfiguration.LoadAsync(ct);
        return configuration is null
            ? null
            : new FiskaltrustQueueClient(configuration);
    }

    public Task<FiskaltrustReceiptResponse> StartTransactionAsync(
        string posSystemId,
        string terminalId,
        string receiptReference,
        string user,
        string area,
        DateTimeOffset moment,
        CancellationToken ct = default) =>
        SignAsync(
            new FiskaltrustReceiptRequest(
                Configuration.CashBoxId,
                Configuration.QueueId,
                posSystemId,
                terminalId,
                receiptReference,
                moment.ToUniversalTime(),
                Array.Empty<FiskaltrustChargeItem>(),
                Array.Empty<FiskaltrustPayItem>(),
                FiskaltrustDeCases.StartTransaction,
                "",
                0m,
                user,
                area),
            ct);

    public async Task<FiskaltrustReceiptResponse> SignAsync(
        FiskaltrustReceiptRequest request,
        CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
            _signEndpoint,
            request,
            JsonOptions,
            ct);

        var payload = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"fiskaltrust Sign HTTP {(int)response.StatusCode}: " +
                SafeMessage(payload));
        }

        var parsed = JsonSerializer.Deserialize<FiskaltrustReceiptResponse>(
            payload,
            JsonOptions);

        return parsed ??
            throw new InvalidOperationException(
                "fiskaltrust Sign lieferte keine lesbare ReceiptResponse.");
    }

    public static FiskaltrustFiscalResult ParseFiscalResult(
        FiskaltrustReceiptResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        string Signature(long type) =>
            response.FtSignatures?
                .FirstOrDefault(x => x.FtSignatureType == type)?
                .Data?.Trim() ?? "";

        var transactionText = Signature(
            FiskaltrustDeCases.SignatureTransactionNumber);
        if (transactionText.Length == 0)
            transactionText = TransactionFromIdentification(
                response.FtReceiptIdentification);

        var counterText = Signature(
            FiskaltrustDeCases.SignatureCounter);
        var tseSerial = Signature(
            FiskaltrustDeCases.SignatureTseSerial);
        var signature = Signature(
            FiskaltrustDeCases.SignatureValue);
        var qr = Signature(
            FiskaltrustDeCases.SignatureQr);

        var start = ParseDate(
            Signature(FiskaltrustDeCases.SignatureStartTime));
        var end = ParseDate(
            Signature(FiskaltrustDeCases.SignatureLogTime));

        var missing = new List<string>();
        if (!ulong.TryParse(
                transactionText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var transaction))
            missing.Add("Transaktionsnummer");
        if (!ulong.TryParse(
                counterText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var counter))
            missing.Add("Signaturzähler");
        if (string.IsNullOrWhiteSpace(tseSerial))
            missing.Add("TSE-Seriennummer");
        if (string.IsNullOrWhiteSpace(signature))
            missing.Add("Signatur");
        if (start is null)
            missing.Add("Vorgangsbeginn");
        if (end is null)
            missing.Add("Vorgangsende");

        return missing.Count == 0
            ? new(
                true,
                response.CbReceiptReference,
                transaction,
                counter,
                tseSerial,
                signature,
                start,
                end,
                qr,
                "fiskaltrust Sign erfolgreich.")
            : new(
                false,
                response.CbReceiptReference,
                transaction,
                counter,
                tseSerial,
                signature,
                start,
                end,
                qr,
                "Unvollständige fiskaltrust/TSE-Antwort: " +
                string.Join(", ", missing));
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static Uri BuildEndpoint(string endpoint, string suffix)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException(
                "fiskaltrust Queue-Endpoint fehlt.");

        var http = endpoint.StartsWith(
                "rest://",
                StringComparison.OrdinalIgnoreCase)
            ? "http://" + endpoint["rest://".Length..]
            : endpoint;

        if (!Uri.TryCreate(
                http.TrimEnd('/') + "/" + suffix,
                UriKind.Absolute,
                out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                "Ungültiger fiskaltrust Queue-Endpoint.");
        }

        // R176 local on-premise only. Cloud endpoints need token handling and
        // are intentionally not accepted by this credential-free adapter.
        if (!IsLoopback(uri.Host))
            throw new InvalidOperationException(
                "R176 fiskaltrust adapter erlaubt nur lokale Queue-Endpunkte.");

        return uri;
    }

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);

    private static string TransactionFromIdentification(string identification)
    {
        if (string.IsNullOrWhiteSpace(identification))
            return "";

        var hash = identification.LastIndexOf('#');
        if (hash < 0 || hash + 1 >= identification.Length)
            return "";

        var tail = identification[(hash + 1)..];
        var firstDigit = tail
            .Select((c, i) => (c, i))
            .FirstOrDefault(x => char.IsDigit(x.c));

        return char.IsDigit(firstDigit.c)
            ? tail[firstDigit.i..]
            : "";
    }

    private static DateTimeOffset? ParseDate(string text) =>
        DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal |
            DateTimeStyles.AdjustToUniversal,
            out var value)
            ? value
            : null;

    private static string SafeMessage(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return "leere Fehlerantwort";

        var text = payload
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();

        return text.Length <= 500
            ? text
            : text[..500] + "…";
    }
}
