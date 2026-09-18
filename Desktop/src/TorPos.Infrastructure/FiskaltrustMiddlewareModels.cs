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
    public Uri Endpoint(string relative)
    {
        var root = BaseUri.ToString().TrimEnd('/') + "/";
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
    public int Position { get; init; }

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
    public int Position { get; init; }

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
    public long FtQueueRow { get; init; }

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

    [JsonPropertyName("ftSignatures")]
    public IReadOnlyList<FiskaltrustSignatureItem> FtSignatures { get; init; } =
        Array.Empty<FiskaltrustSignatureItem>();

    [JsonPropertyName("ftState")]
    public ulong FtState { get; init; }

    [JsonPropertyName("ftStateData")]
    public string? FtStateData { get; init; }
}

public sealed record FiskaltrustSignatureItem
{
    [JsonPropertyName("ftSignatureFormat")]
    public int FtSignatureFormat { get; init; }

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
    public const ulong StartTransaction = 0x4445000000000008UL;

    // The same DE code is used by the official examples for a normal charge item
    // and cash payment in national currency.
    public const ulong StandardChargeItem = 0x4445000000000001UL;
    public const ulong CashPayment = 0x4445000000000001UL;

    // Adds the middleware's implicit start+finish flow to a receipt case.
    public const ulong ImplicitFlowFlag = 0x0000000100000000UL;

    // Retry/recovery flag: ask the queue for the already stored result instead
    // of blindly creating a second fiscal action after a timeout.
    public const ulong ReceiptRequestFlag = 0x0000000000008000UL;

    public static ulong WithImplicitFlow(ulong receiptCase) =>
        receiptCase | ImplicitFlowFlag;

    public static ulong WithReceiptRequest(ulong receiptCase) =>
        receiptCase | ReceiptRequestFlag;
}
