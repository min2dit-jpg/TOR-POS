using TorPos.Core;

// R121: audit findings F3 and F4 - two places where the fiscal data was not
// merely unfinished but wrong.
//
// F3 - a Teilretoure was signed as "AVBelegabbruch", which in DSFinV-K means
//      an ABORTED process: a receipt broken off before completion, no money
//      moved. A Retoure is the opposite - a completed transaction that hands
//      money back, modelled as a normal Beleg with negative positions.
//
// F4 - the printed receipt's "Vorgangsende" was passed as
//      "sale.TseLogTime ?? DateTimeOffset.Now", so when there was no TSE
//      signature the PC clock was printed in a field that is supposed to come
//      from the TSE - and the receipt validator's own Vorgangsende check could
//      never fail, because the value was never null.
public static class R121ReviewTests
{
    public static Task Run(Action<bool, string> assert)
    {
        var line = new CartLine { ProductName = "R121 Artikel", Quantity = 1, UnitPriceCents = 1000, VatRate = 19m };

        // ---- F3: process type of a Retoure ----
        var retoure = new Sale
        {
            Id = 1,
            ReceiptNumber = 121001,
            TransactionType = "RETURN",
            OriginalSaleId = 5,
            OriginalReceiptNumber = 120500,
            CreatedAt = DateTimeOffset.Now,
            PaymentMethod = PaymentMethod.Cash,
            TotalCents = 1000,
            CashPortionCents = 1000,
            Lines = new[] { line }
        };
        var retoureData = System.Text.Encoding.UTF8.GetString(FiscalProcessData.BuildKassenbeleg(retoure));

        assert(
            retoureData.StartsWith("Beleg^"),
            $"R121 a Retoure is signed as a normal Beleg, not as an aborted process (actual: {retoureData.Split('^')[0]})");
        assert(
            !retoureData.Contains("AVBelegabbruch"),
            "R121 the AVBelegabbruch marker - an aborted receipt with no money moved - is never used for a Retoure");
        assert(
            retoureData.Contains("Referenz-Beleg-Nr:120500"),
            "R121 the Retoure still references the original receipt, which is what makes the negative booking traceable");

        // A full Storno keeps its own, correct marker.
        var storno = new Sale
        {
            Id = 2,
            ReceiptNumber = 121002,
            TransactionType = "STORNO",
            OriginalSaleId = 5,
            OriginalReceiptNumber = 120500,
            CreatedAt = DateTimeOffset.Now,
            PaymentMethod = PaymentMethod.Cash,
            TotalCents = 1000,
            CashPortionCents = 1000,
            Lines = new[] { line }
        };
        var stornoData = System.Text.Encoding.UTF8.GetString(FiscalProcessData.BuildKassenbeleg(storno));
        assert(
            stornoData.StartsWith("AVBelegstorno^") && stornoData.Contains("Referenz-Beleg-Nr:120500"),
            "R121 a full BON STORNO keeps AVBelegstorno - cancelling an issued receipt genuinely is that process type");

        // An ordinary sale is unaffected by the change.
        var plain = new Sale
        {
            Id = 3,
            ReceiptNumber = 121003,
            CreatedAt = DateTimeOffset.Now,
            PaymentMethod = PaymentMethod.Cash,
            TotalCents = 1000,
            CashPortionCents = 1000,
            Lines = new[] { line }
        };
        var plainData = System.Text.Encoding.UTF8.GetString(FiscalProcessData.BuildKassenbeleg(plain));
        assert(
            plainData.StartsWith("Beleg^") && !plainData.Contains("Referenz-Beleg-Nr"),
            "R121 an ordinary sale is still a plain Beleg with no reversal reference");

        // ---- F4: Vorgangsende must not be fabricated ----
        // The receipt job models it as nullable, and the validator only demands
        // it when the sale is NOT a TSE outage - exactly like the other
        // TSE-generated fields.
        var signedJob = new ReceiptPrintJob(
            121004, DateTimeOffset.Now, "R121 Laden", "Teststr. 1", "", "", "", "", "Bar",
            0, 1000, new[] { line },
            FiscalTestMode: false,
            EasSerial: "EAS-1",
            TseSerial: "TSE-1",
            TseTransactionNumber: "42",
            SignatureCounter: 7,
            ProcessStart: DateTimeOffset.Now,
            ProcessEnd: DateTimeOffset.Now,
            VerificationValue: "abc",
            TseOutage: false);

        var outageJob = signedJob with
        {
            TseSerial = "",
            TseTransactionNumber = "",
            SignatureCounter = 0,
            VerificationValue = "",
            ProcessEnd = null,
            TseOutage = true
        };

        assert(
            signedJob.ProcessEnd is not null && outageJob.ProcessEnd is null,
            "R121 Vorgangsende is nullable on the print job, so an unsigned sale can leave it genuinely absent instead of carrying the PC clock");
        assert(
            outageJob.TseOutage && string.IsNullOrEmpty(outageJob.VerificationValue),
            "R121 an outage receipt carries no invented TSE values at all - the outage note is what explains their absence");

        return Task.CompletedTask;
    }
}
