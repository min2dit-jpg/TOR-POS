using TorPos.Core;

// R140: the receipt QR code follows DSFinV-K Anhang I, TSE times are printed as
// the TSE delivered them, and the serial on the receipt is the one the TSE logged.
//
// AEAO zu § 146a Nr. 2.4.1: "Der QR-Code hat der DSFinV-K zu entsprechen".
// Nr. 2.4.4: TSE data are printed "in dem Format …, in dem sie von der TSE …
// zurückgeliefert wurden. Nachträgliches Runden, Abschneiden oder Verändern
// dieser Daten ist unzulässig" (a unixTime may be printed as UTC); Nr. 6: the
// serial logged under § 2 Satz 2 Nr. 8 KassenSichV. Until R140 the QR code had a
// format of TOR's own, the times were local and cut to seconds, and the receipt
// showed TOR's system identity instead of the client id the TSE logged.
public static class R140ReviewTests
{
    public static Task Run(string root, Action<bool, string> assert)
    {
        // ---------- the official example of DSFinV-K Anhang I Tz. 2 ----------
        const string signature = "K8zsZ6NjsBzo/Yd3Hba84aH3oT+c4Og5VcfJ7s6Dxz7UmAwtcKmzW16OkS/lm8pDE/37JHoRlYofUTLNF+9bY5Jv7C2P4nuEaHwlVarbiJs3bYlgQmIXQDZnf+8FhfBm";
        const string publicKey = "BBXNYQErM4d9sk9Iy+0T6A4sdTocijml5X78Gq/At2uXTcs/3bZNNpyuHd+dpmY59yLh0xZcr9osHhkDAxsQumgjmtb3d9GIVnTaPCslEki84P1iHPiHKHfcszeQajPk3A==";
        var example = Job(
            clientId: "AMA-2642",
            processData: "Beleg^4.05_3.00_0.00_0.00_0.00^7.05:Bar",
            transaction: "13",
            counter: 44131,
            start: new DateTimeOffset(2019, 11, 22, 11, 29, 48, TimeSpan.Zero),
            end: new DateTimeOffset(2019, 11, 22, 11, 29, 49, TimeSpan.Zero),
            signature: signature,
            publicKey: publicKey);
        assert(TseQrCodePayload.Build(example) ==
               "V0;AMA-2642;Kassenbeleg-V1;Beleg^4.05_3.00_0.00_0.00_0.00^7.05:Bar;13;44131;2019-11-22T11:29:48.000Z;2019-11-22T11:29:49.000Z;ecdsa-plain-SHA384;unixTime;" +
               signature + ";" + publicKey,
            "R140 the QR code of the DSFinV-K Anhang I example is reproduced exactly: V0;client id;processType;processData;TANR;counter;start;end;algorithm;time format;signature;public key");

        // ---------- no QR code without every field ----------
        assert(TseQrCodePayload.Build(example with { TsePublicKey = "" }) == "" &&
               TseQrCodePayload.Build(example with { TseStartLogTime = null }) == "" &&
               TseQrCodePayload.Build(example with { TseOutage = true }) == "" &&
               TseQrCodePayload.Build(example with { FiscalTestMode = true }) == "" &&
               TseQrCodePayload.Build(example with { TseSignatureAlgorithm = "" }) == "",
            "R140 without public key, start time, algorithm - or during an outage or in test mode - no QR code is built and the TSE data are printed as text");

        // ---------- TSE times unchanged ----------
        var berlin = new DateTimeOffset(2026, 9, 17, 12, 0, 5, 123, TimeSpan.FromHours(2));
        assert(TseReceiptTime.Format(berlin) == "2026-09-17T10:00:05.123Z",
            "R140 a TSE time is printed in UTC with its milliseconds, not converted to local time or cut to seconds (AEAO zu § 146a Nr. 2.4.4)");

        // ---------- digital receipt ----------
        // R145: built from the print job, whose serial is the client id the TSE
        // logged (MainWindow.BuildReceiptPrintJob, as for the paper receipt).
        var digital = DigitalReceiptDocument.From(
            new ReceiptPrintJob(
                140001, berlin, "R140 Imbiss", "Hauptstraße 1, 10115 Berlin", "27/123/45678", "", "", "", "Bar", 0, 700,
                new[] { new CartLine { ProductName = "Döner", Quantity = 1, UnitPriceCents = 700, VatRate = 7m } },
                FiscalTestMode: false,
                EasSerial: "KASSE-7",
                TseSerial: "SERIAL",
                TseTransactionNumber: "140",
                SignatureCounter: 281,
                ProcessStart: berlin.AddSeconds(-30),
                ProcessEnd: berlin,
                VerificationValue: "c2ln",
                TseClientId: "KASSE-7",
                TseStartLogTime: berlin.AddSeconds(-30)),
            DigitalReceiptDocument.PaymentsFor(PaymentMethod.Cash, 700, 0));
        assert(digital.Field(DigitalReceiptDocument.SerialLabel) == "KASSE-7" &&
               digital.Field(DigitalReceiptDocument.ProcessStartLabel) == "2026-09-17T09:59:35.123Z" &&
               digital.Field(DigitalReceiptDocument.ProcessEndLabel) == "2026-09-17T10:00:05.123Z" &&
               !digital.Tse.Any(field => field.Value.StartsWith("17.09.2026")),
            "R140 the digital receipt shows the client id the TSE logged as serial and the TSE times unchanged in UTC");

        return Task.CompletedTask;
    }

    private static ReceiptPrintJob Job(string clientId, string processData, string transaction, long counter,
        DateTimeOffset start, DateTimeOffset end, string signature, string publicKey) =>
        new(
            ReceiptNumber: 1,
            CreatedAt: end,
            CompanyName: "TOR",
            CompanyAddress: "Hauptstraße 1",
            TaxNumber: "",
            VatId: "",
            Header: "",
            Footer: "",
            PaymentLabel: "Bar",
            DiscountCents: 0,
            TotalCents: 705,
            Lines: Array.Empty<CartLine>(),
            FiscalTestMode: false,
            EasSerial: clientId,
            TseSerial: "SERIAL",
            TseTransactionNumber: transaction,
            SignatureCounter: counter,
            ProcessStart: start,
            ProcessEnd: end,
            VerificationValue: signature,
            TseClientId: clientId,
            TseProcessType: "Kassenbeleg-V1",
            TseProcessData: processData,
            TseStartLogTime: start,
            TseSignatureAlgorithm: "ecdsa-plain-SHA384",
            TseLogTimeFormat: "unixTime",
            TsePublicKey: publicKey);
}
