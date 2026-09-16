namespace TorPos.Core;

public sealed record PrinterProbeResult(
    bool Success,
    string Message,
    string PrinterName = "",
    string Model = "");

public sealed record ErrorSlipPrintJob(
    DateTimeOffset CreatedAt,
    string Category,
    string Message,
    string ErrorId,
    string CashRegister,
    string OperatorName,
    bool AutoCut = true);


public enum ReportPaperFormat
{
    Receipt58,
    Receipt80,
    A4
}

public sealed record ReportPrintJob(
    string Title,
    IReadOnlyList<string> Lines,
    DateTimeOffset CreatedAt,
    ReportPaperFormat PaperFormat,
    bool AutoCut = true);


public sealed record KitchenPrintLine(
    string Name,
    decimal Quantity,
    bool IsComponent = false);

public sealed record KitchenPrintJob(
    DateTimeOffset CreatedAt,
    long PickupNumber,
    long ParkNumber,
    string OperatorName,
    IReadOnlyList<KitchenPrintLine> Lines,
    string Note = "",
    bool AutoCut = true);

public sealed record PickupSlipPrintJob(
    DateTimeOffset CreatedAt,
    long PickupNumber,
    long ParkNumber,
    string CompanyName = "",
    bool AutoCut = true);

public sealed record ReceiptPrintJob(
    long ReceiptNumber,
    DateTimeOffset CreatedAt,
    string CompanyName,
    string CompanyAddress,
    string TaxNumber,
    string VatId,
    string Header,
    string Footer,
    string PaymentLabel,
    long DiscountCents,
    long TotalCents,
    IReadOnlyList<CartLine> Lines,
    bool FiscalTestMode = true,
    string EasSerial = "",
    string TseSerial = "",
    string TseTransactionNumber = "",
    long SignatureCounter = 0,
    DateTimeOffset? ProcessStart = null,
    DateTimeOffset? ProcessEnd = null,
    string VerificationValue = "",
    bool TseOutage = false,
    string OperatorName = "",
    long TenderedCents = 0,
    long ChangeCents = 0,
    string LogoPath = "",
    long PickupNumber = 0,
    bool TseQrCode = false,
    bool AutoCut = true,
    bool OpenCashDrawer = false);

/// <summary>
/// Builds the compact payload for the optional QR code printed in place of
/// the five spelled-out TSE fields (eAS/TSE/Transaktion/Signaturzähler/
/// Prüfwert), when "receipt.tse_qr_code.enabled" is on - shortens the
/// printed receipt.
///
/// DRAFT - not a certified format: there is no single mandated QR payload
/// format for a KassenSichV Beleg. This pipe-delimited format is a
/// reasonable, human-decodable starting point, not verified against any
/// specific reader/auditor expectation. Re-review before relying on it.
/// </summary>
public static class TseQrCodePayload
{
    public static string Build(ReceiptPrintJob job) =>
        $"eAS:{job.EasSerial}|TSE:{job.TseSerial}|TXN:{job.TseTransactionNumber}|" +
        $"CTR:{job.SignatureCounter}|CHK:{job.VerificationValue}";
}

/// <summary>
/// Raw StarPRNT/ESC-POS byte commands sent directly to the printer through
/// the Windows RAW spooler datatype, bypassing GDI - this is how a receipt
/// printer's built-in cutter and cash-drawer kick are triggered without a
/// vendor SDK. TOR does not bundle or require the official Star StarIO10
/// SDK for this: cut (GS V) and drawer kick (ESC p) are part of the
/// standard StarPRNT/ESC-POS command set the mC-Print3 documents as
/// supported, not a proprietary Star-only protocol.
///
/// DRAFT byte values, not yet confirmed against the physical MCP31CBI -
/// treat as correct-by-documentation until verified in a real hardware
/// acceptance test.
/// </summary>
public static class StarPrntRawCommands
{
    public static readonly byte[] PartialCut = { 0x1D, 0x56, 0x42, 0x00 };

    public static byte[] OpenCashDrawer(byte pin = 0, byte onMs = 25, byte offMs = 250) =>
        new byte[] { 0x1B, 0x70, pin, onMs, offMs };
}

public interface IReceiptPrinterService : IAsyncDisposable
{
    string SupportedModel { get; }
    string DriverMode { get; }

    IReadOnlyList<string> GetInstalledPrinterNames();

    Task<PrinterProbeResult> ProbeAsync(
        string configuredPrinterName = "",
        CancellationToken ct = default);

    Task PrintTestAsync(
        string printerName,
        CancellationToken ct = default);

    Task PrintReceiptAsync(
        ReceiptPrintJob job,
        string printerName,
        CancellationToken ct = default);

    Task PrintErrorSlipAsync(
        ErrorSlipPrintJob job,
        string printerName,
        CancellationToken ct = default);

    Task PrintReportAsync(
        ReportPrintJob job,
        string printerName,
        CancellationToken ct = default);

    Task PrintKitchenAsync(
        KitchenPrintJob job,
        string printerName,
        CancellationToken ct = default);

    Task PrintPickupSlipAsync(
        PickupSlipPrintJob job,
        string printerName,
        CancellationToken ct = default);
}
