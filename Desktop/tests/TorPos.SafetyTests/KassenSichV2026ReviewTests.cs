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

        return Task.CompletedTask;
    }
}
