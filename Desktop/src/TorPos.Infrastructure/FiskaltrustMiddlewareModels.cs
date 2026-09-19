using System.Text.Json;
using System.Text.Json.Serialization;

namespace TorPos.Infrastructure;

/// <summary>
/// Minimal fiskaltrust Middleware API v1 contracts used by the TOR sandbox
/// integration. Property names intentionally mirror the official API.
/// </summary>
public sealed record FiskaltrustMiddlewareOptions(
    Uri BaseUri,
    Guid CashBoxId,
    Guid PosSystemId,
    string TerminalId,
    string AccessToken = "",
    bool UseSaasHeaders = false)
{
    /// <summary>
    /// The fiskaltrust.Portal displays local REST endpoints as rest://... .
    /// HttpClient needs the actual transport URL, which is the same endpoint
    /// with http://. SaaS/CloudCashbox URLs remain https://.
    /// </summary>
    public Uri HttpBaseUri
    {
        get
        {
            if (!BaseUri.IsAbsoluteUri)
                throw new InvalidOperationException("fiskaltrust BaseUri muss absolut sein.");

            if (string.Equals(BaseUri.Scheme, "rest", StringComparison.OrdinalIgnoreCase))
            {
                var builder = new UriBuilder(BaseUri)
                {
                    Scheme = Uri.UriSchemeHttp,
                    Port = BaseUri.Port
                };
                return builder.Uri;
            }

            if (BaseUri.Scheme is not ("http" or "https"))
                throw new InvalidOperationException(
                    $"Nicht unterstütztes fiskaltrust REST-Schema: {BaseUri.Scheme}");

            return BaseUri;
        }
    }

    public Uri Endpoint(string relative)
    {
        var root = HttpBaseUri.ToString().TrimEnd('/') + "/";
        return new Uri(new Uri(root), relative.TrimStart('/'));
    }
}

public sealed record FiskaltrustEchoRequest(
    [property: JsonPropertyName("message")] string Message);

public sealed record FiskaltrustEchoResponse(
    [property: JsonPropertyName("message")] string? Message);

public sealed record FiskaltrustReceiptRequest
{
    [JsonPropertyName("ftCashBoxID")]
    public string FtCashBoxId { get; init; } = "";

    [JsonPropertyName("ftQueueID")]
    public string? FtQueueId { get; init; }

    [JsonPropertyName("ftPosSystemId")]
    public string FtPosSystemId { get; init; } = "";

    [JsonPropertyName("cbTerminalID")]
    public string CbTerminalId { get; init; } = "";

    [JsonPropertyName("cbReceiptReference")]
    public string CbReceiptReference { get; init; } = "";

    [JsonPropertyName("cbReceiptMoment")]
    public DateTimeOffset CbReceiptMoment { get; init; }

    [JsonPropertyName("cbChargeItems")]
    public IReadOnlyList<FiskaltrustChargeItem> CbChargeItems { get; init; } =
        Array.Empty<FiskaltrustChargeItem>();

    [JsonPropertyName("cbPayItems")]
    public IReadOnlyList<FiskaltrustPayItem> CbPayItems { get; init; } =
        Array.Empty<FiskaltrustPayItem>();

    [JsonPropertyName("ftReceiptCase")]
    public ulong FtReceiptCase { get; init; }

    [JsonPropertyName("ftReceiptCaseData")]
    public string? FtReceiptCaseData { get; init; }

    [JsonPropertyName("cbReceiptAmount")]
    public decimal? CbReceiptAmount { get; init; }

    [JsonPropertyName("cbUser")]
    public string? CbUser { get; init; }

    [JsonPropertyName("cbArea")]
    public string? CbArea { get; init; }

    [JsonPropertyName("cbCustomer")]
    public string? CbCustomer { get; init; }

    [JsonPropertyName("cbSettlement")]
    public string? CbSettlement { get; init; }

    [JsonPropertyName("cbPreviousReceiptReference")]
    public string? CbPreviousReceiptReference { get; init; }
}

public sealed record FiskaltrustChargeItem
{
    [JsonPropertyName("position")]
    public decimal Position { get; init; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("amount")]
    public decimal Amount { get; init; }

    [JsonPropertyName("vatRate")]
    public decimal VatRate { get; init; }

    [JsonPropertyName("ftChargeItemCase")]
    public ulong FtChargeItemCase { get; init; }

    [JsonPropertyName("ftChargeItemCaseData")]
    public string? FtChargeItemCaseData { get; init; }

    [JsonPropertyName("vatAmount")]
    public decimal? VatAmount { get; init; }

    [JsonPropertyName("accountNumber")]
    public string? AccountNumber { get; init; }

    [JsonPropertyName("costCenter")]
    public string? CostCenter { get; init; }

    [JsonPropertyName("productGroup")]
    public string? ProductGroup { get; init; }

    [JsonPropertyName("productNumber")]
    public string? ProductNumber { get; init; }

    [JsonPropertyName("productBarcode")]
    public string? ProductBarcode { get; init; }

    [JsonPropertyName("unit")]
    public string? Unit { get; init; }

    [JsonPropertyName("unitQuantity")]
    public decimal? UnitQuantity { get; init; }

    [JsonPropertyName("unitPrice")]
    public decimal? UnitPrice { get; init; }

    [JsonPropertyName("moment")]
    public DateTimeOffset? Moment { get; init; }
}

public sealed record FiskaltrustPayItem
{
    [JsonPropertyName("position")]
    public decimal Position { get; init; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("amount")]
    public decimal Amount { get; init; }

    [JsonPropertyName("ftPayItemCase")]
    public ulong FtPayItemCase { get; init; }

    [JsonPropertyName("ftPayItemCaseData")]
    public string? FtPayItemCaseData { get; init; }

    [JsonPropertyName("accountNumber")]
    public string? AccountNumber { get; init; }

    [JsonPropertyName("costCenter")]
    public string? CostCenter { get; init; }

    [JsonPropertyName("moneyGroup")]
    public string? MoneyGroup { get; init; }

    [JsonPropertyName("moneyNumber")]
    public string? MoneyNumber { get; init; }

    [JsonPropertyName("moment")]
    public DateTimeOffset? Moment { get; init; }
}

public sealed record FiskaltrustReceiptResponse
{
    [JsonPropertyName("ftCashBoxID")]
    public string FtCashBoxId { get; init; } = "";

    [JsonPropertyName("ftQueueID")]
    public string FtQueueId { get; init; } = "";

    [JsonPropertyName("ftQueueItemID")]
    public string FtQueueItemId { get; init; } = "";

    [JsonPropertyName("ftQueueRow")]
    public ulong FtQueueRow { get; init; }

    [JsonPropertyName("cbTerminalID")]
    public string CbTerminalId { get; init; } = "";

    [JsonPropertyName("cbReceiptReference")]
    public string CbReceiptReference { get; init; } = "";

    [JsonPropertyName("ftCashBoxIdentification")]
    public string FtCashBoxIdentification { get; init; } = "";

    [JsonPropertyName("ftReceiptIdentification")]
    public string FtReceiptIdentification { get; init; } = "";

    [JsonPropertyName("ftReceiptMoment")]
    public DateTimeOffset? FtReceiptMoment { get; init; }

    [JsonPropertyName("ftReceiptHeader")]
    public IReadOnlyList<string> FtReceiptHeader { get; init; } =
        Array.Empty<string>();

    [JsonPropertyName("ftChargeItems")]
    public IReadOnlyList<FiskaltrustChargeItem> FtChargeItems { get; init; } =
        Array.Empty<FiskaltrustChargeItem>();

    [JsonPropertyName("ftChargeLines")]
    public IReadOnlyList<string> FtChargeLines { get; init; } =
        Array.Empty<string>();

    [JsonPropertyName("ftPayItems")]
    public IReadOnlyList<FiskaltrustPayItem> FtPayItems { get; init; } =
        Array.Empty<FiskaltrustPayItem>();

    [JsonPropertyName("ftPayLines")]
    public IReadOnlyList<string> FtPayLines { get; init; } =
        Array.Empty<string>();

    [JsonPropertyName("ftSignatures")]
    public IReadOnlyList<FiskaltrustSignatureItem> FtSignatures { get; init; } =
        Array.Empty<FiskaltrustSignatureItem>();

    [JsonPropertyName("ftReceiptFooter")]
    public IReadOnlyList<string> FtReceiptFooter { get; init; } =
        Array.Empty<string>();

    [JsonPropertyName("ftState")]
    public ulong FtState { get; init; }

    [JsonPropertyName("ftStateData")]
    public JsonElement? FtStateData { get; init; }
}

public sealed record FiskaltrustSignatureItem
{
    [JsonPropertyName("ftSignatureItemId")]
    public string? FtSignatureItemId { get; init; }

    [JsonPropertyName("ftSignatureFormat")]
    public ulong FtSignatureFormat { get; init; }

    [JsonPropertyName("ftSignatureType")]
    public ulong FtSignatureType { get; init; }

    [JsonPropertyName("caption")]
    public string Caption { get; init; } = "";

    [JsonPropertyName("data")]
    public string Data { get; init; } = "";
}

/// <summary>
/// German-market values used by the first TOR sandbox adapter. These constants
/// are kept separate from TOR's own fiscal enums to avoid accidental coupling.
/// </summary>
public static class FiskaltrustDeCases
{
    public const ulong PosReceipt = 0x4445000000000001UL;
    public const ulong ZeroReceipt = 0x4445000000000002UL;
    public const ulong DailyClosing = 0x4445000000000007UL;
    public const ulong StartTransaction = 0x4445000000000008UL;
    public const ulong UpdateTransaction = 0x4445000000000009UL;
    public const ulong DeltaTransaction = 0x444500000000000AUL;

    // German charge-item cases. TOR currently supports 19 %, 7 % and 0 %.
    public const ulong StandardChargeItem = 0x4445000000000001UL; // 19 %
    public const ulong ReducedChargeItem = 0x4445000000000002UL;  // 7 %
    public const ulong NonTaxableChargeItem = 0x4445000000000005UL;
    public const ulong TaxFreeChargeItem = 0x4445000000000006UL;
    public const ulong UnknownVatChargeItem = 0x4445000000000007UL;
    public const ulong TakeAwayChargeItemFlag = 0x0000000000010000UL;
    public const ulong PositionCancellationChargeItemFlag = 0x0000000000200000UL;

    // German payment cases. Do not guess a generic card type: debit and credit
    // are separate fiskaltrust cases and must later come from terminal evidence.
    public const ulong CashPayment = 0x4445000000000001UL;
    public const ulong DebitCardPayment = 0x4445000000000004UL;
    public const ulong CreditCardPayment = 0x4445000000000005UL;
    public const ulong OnlinePayment = 0x4445000000000006UL;
    public const ulong SepaTransfer = 0x4445000000000008UL;
    public const ulong OtherBankTransfer = 0x4445000000000009UL;

    // Adds the Middleware's implicit start+finish flow to a receipt case.
    public const ulong ImplicitFlowFlag = 0x0000000100000000UL;

    // Recovery flag: retrieve an already processed receipt by
    // cbReceiptReference after an ambiguous communication failure.
    // Important: this is 0x0000800000000000 (not the generic low 0x8000 bit).
    public const ulong ReceiptRequestFlag = 0x0000800000000000UL;

    public static ulong WithImplicitFlow(ulong receiptCase) =>
        receiptCase | ImplicitFlowFlag;

    public static ulong WithReceiptRequest(ulong receiptCase) =>
        receiptCase | ReceiptRequestFlag;

    public static ulong ChargeItemCaseForVat(decimal vatRate) => vatRate switch
    {
        19m => StandardChargeItem,
        7m => ReducedChargeItem,
        0m => throw new InvalidOperationException(
            "0 % darf für fiskaltrust nicht allein aus dem Steuersatz abgeleitet werden. " +
            "Nicht steuerbar, steuerfrei und nicht ermittelbar haben getrennte DE-Fälle."),
        _ => throw new InvalidOperationException(
            $"fiskaltrust DE unterstützt in TOR aktuell keinen MwSt.-Satz {vatRate} %.")
    };
}


/// <summary>
/// Generic fiskaltrust signature display formats used by the sandbox receipt
/// projection. The lower format value identifies text/QR/Base64. Germany also
/// uses OptionalPrintFlag to mark text values that may be omitted when the QR
/// compliance signature is printed.
/// </summary>
public static class FiskaltrustSignatureFormats
{
    public const ulong Text = 0x0001UL;
    public const ulong QrCode = 0x0003UL;
    public const ulong Base64 = 0x000DUL;
    public const ulong OptionalPrintFlag = 0x00010000UL;

    public static ulong BaseFormat(ulong value) => value & 0xFFFFUL;

    public static bool IsOptionalWhenQrIsPrinted(ulong value) =>
        (value & OptionalPrintFlag) != 0;
}

/// <summary>
/// Germany-specific signature types returned by fiskaltrust Middleware 1.3.
/// Keep these values isolated from TOR's own TSE enums.
/// </summary>
public static class FiskaltrustDeSignatureTypes
{
    public const ulong KassenSichVQrPayload = 0x4445000000000001UL;
    public const ulong QrVersion = 0x4445000000000013UL;
    public const ulong CashRegisterSerial = 0x4445000000000014UL;
    public const ulong ProcessType = 0x4445000000000015UL;
    public const ulong ProcessData = 0x4445000000000016UL;
    public const ulong TransactionNumber = 0x4445000000000017UL;
    public const ulong SignatureCounter = 0x4445000000000018UL;
    public const ulong TransactionStartTime = 0x4445000000000019UL;
    public const ulong SignatureLogTime = 0x444500000000001AUL;
    public const ulong SignatureAlgorithm = 0x444500000000001BUL;
    public const ulong LogTimeFormat = 0x444500000000001CUL;
    public const ulong Signature = 0x444500000000001DUL;
    public const ulong PublicKey = 0x444500000000001EUL;
    public const ulong ProcessStartTime = 0x444500000000001FUL;
    public const ulong CertificationIdentification = 0x4445000000000022UL;
    public const ulong TseSerialNumber = 0x4445000000000023UL;
}
