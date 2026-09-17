using TorPos.Core;

// R103: digital/QR receipt - a paperless alternative to the printed Bon.
//
// R145: the receipt is published to TOR Cloud instead of being served by a web
// server on the till inside the shop's WiFi. That server went, and with it the
// checks of its HTTP handling (here) and of its binding, expiry and limits
// (R115); token, expiry and limits of the Cloud service are tested in
// Cloud/tests. What stays is the point of R103: the digital receipt carries the
// same legally relevant content as the printed one - it may be the customer's
// only copy.
public static class R103ReviewTests
{
    public static Task Run(Action<bool, string> assert)
    {
        var testJob = new ReceiptPrintJob(
            103001, DateTimeOffset.Now, "R103 Testladen", "Teststraße 1", "12/345", "DE123456789", "", "", "Bar", 0, 1000,
            new[] { new CartLine { ProductName = "R103 Artikel", Quantity = 2, UnitPriceCents = 500, VatRate = 19m } },
            FiscalTestMode: true);
        var test = DigitalReceiptDocument.From(testJob, DigitalReceiptDocument.PaymentsFor(PaymentMethod.Mixed, 400, 600));
        assert(
            test.BusinessName == "R103 Testladen" && test.ReceiptNumber == "103001" && test.Lines.Single().Name == "R103 Artikel" &&
            test.TestReceipt && test.Tse.Count == 0 &&
            test.Payments.Select(p => $"{p.Label} {p.AmountCents}").SequenceEqual(new[] { "Bar 400", "Karte 600" }),
            "R103 the digital receipt carries company name, receipt number, line items, the test-mode mark and the Bar/Karte split");

        var realJob = new ReceiptPrintJob(
            103002, DateTimeOffset.Now, "R103 Testladen", "Teststr. 1, 10115 Berlin", "", "", "", "", "Bar", 0, 500,
            new[] { new CartLine { ProductName = "R103 Artikel 2", Quantity = 1, UnitPriceCents = 500, VatRate = 19m } },
            FiscalTestMode: false,
            EasSerial: "TORPOS-R103",
            TseSerial: "TSE-SERIAL-1",
            TseTransactionNumber: "42",
            SignatureCounter: 7,
            ProcessStart: DateTimeOffset.Now.AddSeconds(-20),
            ProcessEnd: DateTimeOffset.Now,
            VerificationValue: "abcdef123456");
        var real = DigitalReceiptDocument.From(realJob, DigitalReceiptDocument.PaymentsFor(PaymentMethod.Cash, 500, 0));
        assert(
            !real.TestReceipt && real.Field(DigitalReceiptDocument.TseSerialLabel) == "TSE-SERIAL-1" &&
            real.Field(DigitalReceiptDocument.VerificationValueLabel) == "abcdef123456" &&
            real.MissingFields.Count == 0 && real.Notes.Contains(DigitalReceiptDocument.CompleteNote),
            "R103 a real sale's digital receipt shows the TSE fields, no test mark, and the § 6 KassenSichV statement");

        return Task.CompletedTask;
    }
}
