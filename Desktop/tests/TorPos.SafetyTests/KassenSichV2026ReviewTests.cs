using TorPos.Core;

public static class KassenSichV2026ReviewTests
{
    public static Task Run(Action<bool, string> assert)
    {
        ReceiptPrintJob ValidJob(IReadOnlyList<CartLine>? lines = null, long total = 1190) => new(
            ReceiptNumber: 151001,
            CreatedAt: DateTimeOffset.Now,
            CompanyName: "TOR Test GmbH",
            CompanyAddress: "Teststraße 1, 10115 Berlin",
            TaxNumber: "12/345/67890",
            VatId: "DE123456789",
            Header: "",
            Footer: "",
            PaymentLabel: "BAR",
            DiscountCents: 0,
            TotalCents: total,
            Lines: lines ?? new[]
            {
                new CartLine
                {
                    ProductId = 1,
                    ProductName = "Testartikel",
                    Quantity = 1m,
                    UnitPriceCents = 1190,
                    ListUnitPriceCents = 1190,
                    VatRate = 19m
                }
            },
            FiscalTestMode: false,
            EasSerial: "TOR-TEST-001",
            TseSerial: "TSE-001",
            TseTransactionNumber: "101",
            SignatureCounter: 202,
            ProcessStart: DateTimeOffset.Now.AddMinutes(-1),
            ProcessEnd: DateTimeOffset.Now,
            VerificationValue: "SIGNATURE");

        assert(
            FiscalReceiptFields.Missing(ValidJob()).Count == 0,
            "KassenSichV 2026 §6 complete receipt passes central production validator");

        var empty = FiscalReceiptFields.Missing(ValidJob(Array.Empty<CartLine>(), total: 0));
        assert(
            empty.Contains("Menge/Art der Leistung") &&
            empty.Contains("Steuersatz/Steuerbetrag"),
            "KassenSichV 2026 §6 blocks a production receipt without goods/service and VAT data");

        var inconsistent = FiscalReceiptFields.Missing(ValidJob(total: 999));
        assert(
            inconsistent.Contains("Entgelt"),
            "KassenSichV 2026 §6 blocks a receipt whose printed total differs from immutable position totals");

        var unsupportedVat = FiscalReceiptFields.Missing(ValidJob(new[]
        {
            new CartLine
            {
                ProductId = 2,
                ProductName = "Unzulässiger Satz",
                Quantity = 1m,
                UnitPriceCents = 1000,
                ListUnitPriceCents = 1000,
                VatRate = 12m
            }
        }, total: 1000));
        assert(
            unsupportedVat.Contains("Steuersatz/Steuerbetrag"),
            "KassenSichV 2026 §6 refuses an unclassified VAT rate on a production receipt");

        var completeTse = SaleTseResult.FromSuccessfulTse(
            "TOR-TEST-001", "123", "456", "TSE-001", "SIGNATURE",
            DateTimeOffset.Now, DateTimeOffset.Now.AddSeconds(-2));
        assert(
            completeTse.Signed && completeTse.OutageMessage.Length == 0,
            "KassenSichV 2026 §2 accepts a complete TSE success result as signed");

        var incompleteTse = SaleTseResult.FromSuccessfulTse(
            "TOR-TEST-001", "123", "", "", "",
            DateTimeOffset.Now, DateTimeOffset.Now.AddSeconds(-2));
        assert(
            !incompleteTse.Signed && incompleteTse.OutageMessage.Contains("Signaturzähler") &&
            incompleteTse.OutageMessage.Contains("TSE-Seriennummer") &&
            incompleteTse.OutageMessage.Contains("Prüfwert/Signatur"),
            "KassenSichV 2026 §2 converts an incomplete API success into a documented outage");

        var badCounter = SaleTseResult.FromSuccessfulTse(
            "TOR-TEST-001", "not-a-number", "456", "TSE-001", "SIGNATURE",
            DateTimeOffset.Now, DateTimeOffset.Now.AddSeconds(-2));
        assert(
            !badCounter.Signed && badCounter.OutageMessage.Contains("Transaktionsnummer"),
            "KassenSichV 2026 §2 refuses a non-numeric TSE transaction number");

        var consistentSale = new Sale
        {
            ReceiptNumber = 151002,
            PaymentMethod = PaymentMethod.Mixed,
            CashPortionCents = 500,
            CardPortionCents = 690,
            TotalCents = 1190,
            Lines = new[]
            {
                new CartLine
                {
                    ProductId = 3,
                    ProductName = "Kassenbeleg-Test",
                    Quantity = 1m,
                    UnitPriceCents = 1190,
                    ListUnitPriceCents = 1190,
                    VatRate = 19m
                }
            }
        };
        assert(
            FiscalProcessData.KassenbelegText(consistentSale).Contains("5.00:Bar_6.90:Unbar"),
            "KassenSichV 2026 §2 Kassenbeleg-V1 reconciles VAT gross and mixed payment totals before signing");

        var inconsistentSale = new Sale
        {
            ReceiptNumber = 151003,
            PaymentMethod = PaymentMethod.Cash,
            CashPortionCents = 999,
            CardPortionCents = 0,
            TotalCents = 999,
            Lines = consistentSale.Lines
        };
        try
        {
            _ = FiscalProcessData.KassenbelegText(inconsistentSale);
            assert(false, "KassenSichV 2026 §2 should reject inconsistent VAT gross");
        }
        catch (InvalidOperationException ex)
        {
            assert(
                ex.Message.Contains("MwSt.-Brutto"),
                "KassenSichV 2026 §2 blocks TSE processData when VAT gross differs from receipt total");
        }

        assert(
            !FiscalRelease.Enabled &&
            FiscalRelease.MissingQualifications().Count == 6 &&
            FiscalRelease.MissingQualifications().Contains("physische TSE-E2E-Abnahme"),
            "KassenSichV 2026 production release stays locked until all six evidence qualifications are complete");

        return Task.CompletedTask;
    }
}
