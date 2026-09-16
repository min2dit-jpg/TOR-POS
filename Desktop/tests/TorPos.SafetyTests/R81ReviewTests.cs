using TorPos.Core;
using TorPos.Infrastructure;

// R81: optional QR code on the printed receipt instead of the five spelled-out
// TSE text lines (eAS/TSE/Transaktion/Signaturzähler/Prüfwert), so the receipt
// comes out shorter. Default OFF - existing receipts are unchanged unless an
// admin opts in via Einstellungen -> Bon -> Druckverhalten.
public static class R81ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool,string> assert,
        Func<Func<Task>,string,Task> reject)
    {
        var dir = Path.Combine(root, "r81-tse-qr-code");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r81.db"));
        var settings = new SettingsRepository(db);

        var loaded = await settings.LoadAllAsync();
        assert(
            loaded.GetValueOrDefault("receipt.tse_qr_code.enabled") == "false",
            "R81 the TSE QR code setting defaults to off - existing receipts print unchanged");

        var job = new ReceiptPrintJob(
            ReceiptNumber: 123,
            CreatedAt: DateTimeOffset.Now,
            CompanyName: "TOR",
            CompanyAddress: "",
            TaxNumber: "",
            VatId: "",
            Header: "",
            Footer: "",
            PaymentLabel: "Bar",
            DiscountCents: 0,
            TotalCents: 1000,
            Lines: Array.Empty<CartLine>(),
            FiscalTestMode: false,
            EasSerial: "EAS-1",
            TseSerial: "TSE-SN-2",
            TseTransactionNumber: "42",
            SignatureCounter: 7,
            VerificationValue: "ABCDEF12");

        var payload = TseQrCodePayload.Build(job);
        assert(
            payload.Contains("eAS:EAS-1") && payload.Contains("TSE:TSE-SN-2") &&
            payload.Contains("TXN:42") && payload.Contains("CTR:7") && payload.Contains("CHK:ABCDEF12"),
            "R81 the QR payload carries all five TSE fields the text lines would otherwise print");

        assert(
            !new ReceiptPrintJob(0, DateTimeOffset.Now, "", "", "", "", "", "", "", 0, 0, Array.Empty<CartLine>()).TseQrCode,
            "R81 ReceiptPrintJob.TseQrCode defaults to false for any caller that doesn't set it explicitly");
    }
}
