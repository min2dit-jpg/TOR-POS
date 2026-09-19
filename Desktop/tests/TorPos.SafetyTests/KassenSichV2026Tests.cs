using TorPos.Core;

public static class KassenSichV2026Tests
{
    public static Task Run(Action<bool, string> assert)
    {
        var now = DateTimeOffset.UtcNow;
        var line = new CartLine
        {
            ProductId = 1,
            ProductName = "Testartikel",
            Quantity = 1m,
            UnitPriceCents = 119,
            ListUnitPriceCents = 119,
            VatRate = 19m
        };

        var sale = new Sale
        {
            ReceiptNumber = 1,
            CreatedAt = now,
            StartedAt = now.AddSeconds(-5),
            PaymentMethod = PaymentMethod.Cash,
            CashPortionCents = 119,
            TotalCents = 119,
            Lines = new[] { line },
            TseClientId = "REGISTER-1",
            TseTransactionNumber = "42",
            TseSignatureCounter = "77",
            TseSerialNumber = "TSE-1",
            TseSignature = "SIGNATURE",
            TseLogTime = now
        };

        assert(
            KassenSichV2026.IsReady(
                KassenSichV2026.ValidateTransaction(sale)),
            "KassenSichV §2 checker accepts a complete signed transaction");

        var badPayment = KassenSichV2026.ValidateTransaction(
            new Sale
            {
                ReceiptNumber = 2,
                CreatedAt = now,
                StartedAt = now.AddSeconds(-5),
                PaymentMethod = PaymentMethod.Mixed,
                CashPortionCents = 50,
                CardPortionCents = 50,
                TotalCents = 119,
                Lines = new[] { line },
                TseClientId = "REGISTER-1",
                TseTransactionNumber = "43",
                TseSignatureCounter = "78",
                TseSerialNumber = "TSE-1",
                TseSignature = "SIGNATURE",
                TseLogTime = now
            });

        assert(
            badPayment.Any(x =>
                x.Code == "KASSENSICHV_2_PAYMENT" &&
                !x.Ready),
            "KassenSichV §2 checker rejects inconsistent payment totals");

        var fakeOutage = KassenSichV2026.ValidateTransaction(
            new Sale
            {
                ReceiptNumber = 3,
                CreatedAt = now,
                StartedAt = now.AddSeconds(-2),
                PaymentMethod = PaymentMethod.Cash,
                CashPortionCents = 119,
                TotalCents = 119,
                Lines = new[] { line },
                TseClientId = "REGISTER-1",
                TseOutage = true,
                TseTransactionNumber = "invented",
                TseSignatureCounter = "1",
                TseSignature = "invented"
            });

        assert(
            fakeOutage.Any(x =>
                x.Code == "KASSENSICHV_2_TSE_OUTAGE" &&
                !x.Ready),
            "KassenSichV outage checker refuses invented TSE values");

        var receipt = new ReceiptPrintJob(
            ReceiptNumber: 1,
            CreatedAt: now,
            CompanyName: "Testunternehmen",
            CompanyAddress: "Testanschrift",
            TaxNumber: "",
            VatId: "",
            Header: "",
            Footer: "",
            PaymentLabel: "BAR",
            DiscountCents: 0,
            TotalCents: 119,
            Lines: new[] { line },
            FiscalTestMode: false,
            EasSerial: "REGISTER-1",
            TseSerial: "TSE-1",
            TseTransactionNumber: "42",
            SignatureCounter: 77,
            ProcessStart: now.AddSeconds(-5),
            ProcessEnd: now,
            VerificationValue: "SIGNATURE");

        assert(
            KassenSichV2026.IsReady(
                KassenSichV2026.ValidateReceipt(receipt)),
            "KassenSichV §6 checker accepts a complete fiscal receipt");

        var missingReceipt = KassenSichV2026.ValidateReceipt(
            receipt with
            {
                TseTransactionNumber = "",
                SignatureCounter = 0
            });

        assert(
            missingReceipt.Any(x =>
                x.Code == "KASSENSICHV_6_FISCAL_FIELDS" &&
                !x.Ready),
            "KassenSichV §6 checker exposes missing TSE receipt fields");

        var unsupportedVatReceipt = KassenSichV2026.ValidateReceipt(
            receipt with
            {
                Lines = new[]
                {
                    new CartLine
                    {
                        ProductName = "Unbekannter Steuersatz",
                        Quantity = 1m,
                        UnitPriceCents = 105,
                        ListUnitPriceCents = 105,
                        VatRate = 5m
                    }
                },
                TotalCents = 105
            });

        assert(
            unsupportedVatReceipt.Any(x =>
                x.Code == "KASSENSICHV_6_VAT" &&
                !x.Ready),
            "KassenSichV §6 checker rejects unsupported tax rates");

        var digitalUnsupported = DigitalReceiptDocument.From(
            receipt with
            {
                Lines = new[]
                {
                    new CartLine
                    {
                        ProductName = "Unbekannter Steuersatz",
                        Quantity = 1m,
                        UnitPriceCents = 105,
                        ListUnitPriceCents = 105,
                        VatRate = 5m
                    }
                },
                TotalCents = 105
            },
            DigitalReceiptDocument.PaymentsFor(
                PaymentMethod.Cash,
                105,
                0));
        assert(
            digitalUnsupported.MissingFields.Any(x =>
                x.Contains("Steuersatz", StringComparison.Ordinal)),
            "KassenSichV §6 digital receipt exposes unsupported VAT instead of claiming completeness");

        var discounted = KassenSichV2026.ValidateReceipt(
            receipt with
            {
                Lines = new[]
                {
                    new CartLine
                    {
                        ProductName = "19 Prozent",
                        Quantity = 1m,
                        UnitPriceCents = 1000,
                        ListUnitPriceCents = 1000,
                        VatRate = 19m
                    },
                    new CartLine
                    {
                        ProductName = "7 Prozent",
                        Quantity = 1m,
                        UnitPriceCents = 500,
                        ListUnitPriceCents = 500,
                        VatRate = 7m
                    }
                },
                DiscountCents = 100,
                TotalCents = 1400
            });

        assert(
            discounted.Single(x =>
                x.Code == "KASSENSICHV_6_TAX_TOTAL").Ready,
            "KassenSichV §6 tax summary stays cent-exact after manual discount");

        return Task.CompletedTask;
    }
}
