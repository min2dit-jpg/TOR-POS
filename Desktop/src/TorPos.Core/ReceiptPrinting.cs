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
    bool OpenCashDrawer = false,
    // R137: start of the first order transaction (DSFinV-K 2.7.2).
    DateTimeOffset? OrderStart = null,
    // R140: what the DSFinV-K Anhang I QR code carries - the TSE data exactly
    // as signed, and the TSE's public key, algorithm and log time format.
    string TseClientId = "",
    string TseProcessType = "",
    string TseProcessData = "",
    DateTimeOffset? TseStartLogTime = null,
    string TseSignatureAlgorithm = "",
    string TseLogTimeFormat = "",
    string TsePublicKey = "");

/// <summary>
/// R140: TSE times on the receipt. AEAO zu § 146a Nr. 2.4.4: the data the TSE
/// returns are printed "in dem Format …, in dem sie von der TSE an das
/// elektronische Aufzeichnungssystem zurückgeliefert wurden. Nachträgliches
/// Runden, Abschneiden oder Verändern dieser Daten ist unzulässig. Es wird nicht
/// beanstandet, wenn ein als UnixTime gelieferter Zeitstempel als Coordinated
/// Universal Time (UTC) ohne zusätzliche Zeitzone ausgegeben wird." Until R140 the
/// receipt showed them converted to local time and cut to seconds. Printed now
/// in UTC with milliseconds - the format of DSFinV-K Anhang I and E.
/// </summary>
public static class TseReceiptTime
{
    public static string Format(DateTimeOffset value) => DsfinvkCsv.TseTime(value);
}

/// <summary>
/// R140: the QR code for machine-verifiable receipts, DSFinV-K Anhang I Tz. 2 -
/// <c>V0;kassen-seriennummer;processType;processData;transaktions-nummer;signatur-zaehler;start-zeit;log-time;sig-alg;log-time-format;signatur;public-key</c>.
/// AEAO zu § 146a Nr. 2.4.1: "Der QR-Code hat der DSFinV-K zu entsprechen";
/// only then does it stand in for the printed TSE data (Nr. 2.4.4). Until R140
/// TOR printed a format of its own ("eAS:…|TSE:…|TXN:…") in their place, which no
/// verification tool can read. When a field is missing - a TSE outage, TSE
/// master data not yet read from the TSE export - no QR code is built and the
/// printer prints the text lines.
/// </summary>
public static class TseQrCodePayload
{
    public const string Version = "V0";

    public static string Build(ReceiptPrintJob job)
    {
        if (job.FiscalTestMode || job.TseOutage || job.SignatureCounter <= 0 ||
            job.TseStartLogTime is not { } start || job.ProcessEnd is not { } end)
            return "";

        var fields = new[]
        {
            job.TseClientId, job.TseProcessType, job.TseProcessData, job.TseTransactionNumber,
            job.TseSignatureAlgorithm, job.TseLogTimeFormat, job.VerificationValue, job.TsePublicKey
        };
        if (fields.Any(string.IsNullOrWhiteSpace))
            return "";

        return string.Join(";",
            Version,
            job.TseClientId,
            job.TseProcessType,
            job.TseProcessData,
            job.TseTransactionNumber,
            job.SignatureCounter.ToString(System.Globalization.CultureInfo.InvariantCulture),
            TseReceiptTime.Format(start),
            TseReceiptTime.Format(end),
            job.TseSignatureAlgorithm,
            job.TseLogTimeFormat,
            job.VerificationValue,
            job.TsePublicKey);
    }
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
    // R176: Epson ESC/POS and Star StarPRNT are NOT the same command language.
    // Epson: GS V for cut, ESC p for drawer pin 2/5.
    // StarPRNT: ESC d n for cut; ESC BEL n1 n2 + BEL (device 1) or SUB
    // (device 2) for the DK/external device. mC-Print3 uses StarPRNT mode.
    public static readonly byte[] EpsonEscPosPartialCut =
        { 0x1D, 0x56, 0x42, 0x00 };

    public static readonly byte[] StarPrntPartialCut =
        { 0x1B, 0x64, 0x01 };

    public static byte[] EpsonEscPosOpenCashDrawer(
        byte pin = 0,
        byte onUnits2Ms = 25,
        byte offUnits2Ms = 250)
    {
        if (pin is not (0 or 1 or 48 or 49))
            throw new ArgumentOutOfRangeException(
                nameof(pin),
                "ESC/POS drawer pin must be 0/48 (pin 2) or 1/49 (pin 5).");

        return new byte[]
        {
            0x1B, 0x70, pin, onUnits2Ms, offUnits2Ms
        };
    }

    public static byte[] StarPrntOpenCashDrawer(
        int externalDevice = 1,
        byte energizing10Ms = 20,
        byte delay10Ms = 20)
    {
        if (externalDevice == 1)
        {
            if (energizing10Ms is < 1 or > 127)
                throw new ArgumentOutOfRangeException(nameof(energizing10Ms));
            if (delay10Ms is < 1 or > 127)
                throw new ArgumentOutOfRangeException(nameof(delay10Ms));

            // ESC BEL n1 n2 sets the device-1 pulse, BEL executes it.
            return new byte[]
            {
                0x1B, 0x07, energizing10Ms, delay10Ms, 0x07
            };
        }

        if (externalDevice == 2)
        {
            // SUB drives external device 2 with StarPRNT's fixed pulse.
            return new byte[] { 0x1A };
        }

        throw new ArgumentOutOfRangeException(
            nameof(externalDevice),
            "StarPRNT external device must be 1 or 2.");
    }
}

public interface IReceiptPrinterService : IAsyncDisposable
{
    string SupportedModel { get; }
    string DriverMode { get; }

    IReadOnlyList<string> GetInstalledPrinterNames();

    IReadOnlyList<PrinterDeviceInfo> GetInstalledPrinterDevices();

    Task<PrinterProbeResult> ProbeAsync(
        string configuredPrinterName = "",
        CancellationToken ct = default);

    Task PrintTestAsync(
        string printerName,
        CancellationToken ct = default);

    Task TestCashDrawerAsync(
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
