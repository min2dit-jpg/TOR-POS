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
            ProcessEnd: new DateTimeOffset(2026, 9, 17, 10, 0, 1, TimeSpan.Zero),
            VerificationValue: "ABCDEF12",
            TseClientId: "KASSE-1",
            TseProcessType: "Kassenbeleg-V1",
            TseProcessData: "Beleg^10.00_0.00_0.00_0.00_0.00^10.00:Bar",
            TseStartLogTime: new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero),
            TseSignatureAlgorithm: "ecdsa-plain-SHA384",
            TseLogTimeFormat: "unixTime",
            TsePublicKey: "BBXN");

        // R140: R81 pinned a format of TOR's own ("eAS:…|TSE:…|TXN:…") that no
        // verification tool reads; DSFinV-K Anhang I Tz. 2 defines the payload.
        var payload = TseQrCodePayload.Build(job);
        assert(
            payload == "V0;KASSE-1;Kassenbeleg-V1;Beleg^10.00_0.00_0.00_0.00_0.00^10.00:Bar;42;7;2026-09-17T10:00:00.000Z;2026-09-17T10:00:01.000Z;ecdsa-plain-SHA384;unixTime;ABCDEF12;BBXN",
            "R81/R140 the QR payload follows DSFinV-K Anhang I and carries the TSE data the text lines would otherwise print");

        assert(
            !new ReceiptPrintJob(0, DateTimeOffset.Now, "", "", "", "", "", "", "", 0, 0, Array.Empty<CartLine>()).TseQrCode,
            "R81 ReceiptPrintJob.TseQrCode defaults to false for any caller that doesn't set it explicitly");
    }
}
