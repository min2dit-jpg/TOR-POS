using System.Net;
using System.Text;
using TorPos.Infrastructure;

public static class FiskaltrustIntegrationTests
{
    public static async Task Run(Action<bool, string> assert)
    {
        var localOptions = new FiskaltrustMiddlewareOptions(
            new Uri("rest://localhost:1500/queue-test"),
            Guid.Empty,
            Guid.Empty,
            "");

        assert(
            localOptions.HttpBaseUri.ToString().StartsWith(
                "http://localhost:1500/queue-test",
                StringComparison.OrdinalIgnoreCase),
            "fiskaltrust local rest:// endpoint is normalized to http:// without changing port/path");

        var localHandler = new FakeHandler();
        using (var http = new HttpClient(localHandler))
        {
            var client = new FiskaltrustMiddlewareClient(http, localOptions);
            var echo = await client.EchoAsync("TOR POS TEST");

            assert(
                echo == "TOR POS TEST" &&
                localHandler.LastPath == "/queue-test/json/v1/Echo" &&
                !localHandler.SawCashboxHeader &&
                !localHandler.SawAccessTokenHeader,
                "fiskaltrust local Echo works without CashBox/POS IDs and sends no portal credentials");
        }

        var saasCashBoxId = Guid.NewGuid();
        var saasHandler = new FakeHandler();
        using (var http = new HttpClient(saasHandler))
        {
            var client = new FiskaltrustMiddlewareClient(
                http,
                new FiskaltrustMiddlewareOptions(
                    new Uri("https://example.invalid/cashbox/"),
                    saasCashBoxId,
                    Guid.Empty,
                    "",
                    "secret-test-token",
                    UseSaasHeaders: true));

            _ = await client.EchoAsync("SAAS");

            assert(
                saasHandler.SawCashboxHeader &&
                saasHandler.CashboxHeader == saasCashBoxId.ToString() &&
                saasHandler.SawAccessTokenHeader &&
                saasHandler.AccessTokenHeader == "secret-test-token",
                "fiskaltrust SaaS mode adds CashBox and access-token headers only when explicitly enabled");
        }

        var signHandler = new FakeHandler();
        using (var http = new HttpClient(signHandler))
        {
            var client = new FiskaltrustMiddlewareClient(http, localOptions);
            var rejected = false;
            try
            {
                await client.SignAsync(new FiskaltrustReceiptRequest
                {
                    CbReceiptReference = "test-1"
                });
            }
            catch (InvalidOperationException ex)
            {
                rejected = ex.Message.Contains("CashBox-ID", StringComparison.Ordinal);
            }

            assert(
                rejected && signHandler.Calls == 0,
                "fiskaltrust Sign refuses missing fiscal identity before any network request");
        }

        var optional = FiskaltrustSignatureFormats.OptionalPrintFlag;
        var receipt = new FiskaltrustReceiptResponse
        {
            FtReceiptHeader = ["MW HEADER"],
            FtChargeLines = ["MW CHARGE"],
            FtPayLines = ["MW PAY"],
            FtReceiptFooter = ["MW FOOTER"],
            FtSignatures =
            [
                new FiskaltrustSignatureItem
                {
                    FtSignatureFormat = FiskaltrustSignatureFormats.QrCode,
                    FtSignatureType = FiskaltrustDeSignatureTypes.KassenSichVQrPayload,
                    Caption = "QR",
                    Data = "V0;KASSE-1;Kassenbeleg-V1;Beleg^...;42;7;2026-09-19T06:00:00.000Z;2026-09-19T06:00:01.000Z;ecdsa-plain-SHA256;unixTime;SIG;PUB"
                },
                new FiskaltrustSignatureItem
                {
                    FtSignatureFormat = FiskaltrustSignatureFormats.Text | optional,
                    FtSignatureType = FiskaltrustDeSignatureTypes.QrVersion,
                    Data = "V0"
                },
                new FiskaltrustSignatureItem
                {
                    FtSignatureFormat = FiskaltrustSignatureFormats.Text | optional,
                    FtSignatureType = FiskaltrustDeSignatureTypes.CashRegisterSerial,
                    Data = "KASSE-1"
                },
                new FiskaltrustSignatureItem
                {
                    FtSignatureFormat = FiskaltrustSignatureFormats.Text | optional,
                    FtSignatureType = FiskaltrustDeSignatureTypes.ProcessType,
                    Data = "Kassenbeleg-V1"
                },
                new FiskaltrustSignatureItem
                {
                    FtSignatureFormat = FiskaltrustSignatureFormats.Text | optional,
                    FtSignatureType = FiskaltrustDeSignatureTypes.ProcessData,
                    Data = "Beleg^..."
                },
                new FiskaltrustSignatureItem
                {
                    FtSignatureFormat = FiskaltrustSignatureFormats.Text | optional,
                    FtSignatureType = FiskaltrustDeSignatureTypes.TransactionNumber,
                    Data = "42"
                },
                new FiskaltrustSignatureItem
                {
                    FtSignatureFormat = FiskaltrustSignatureFormats.Text | optional,
                    FtSignatureType = FiskaltrustDeSignatureTypes.SignatureCounter,
                    Data = "7"
                },
                new FiskaltrustSignatureItem
                {
                    FtSignatureFormat = FiskaltrustSignatureFormats.Text | optional,
                    FtSignatureType = FiskaltrustDeSignatureTypes.TransactionStartTime,
                    Data = "2026-09-19T06:00:00.000Z"
                },
                new FiskaltrustSignatureItem
                {
                    FtSignatureFormat = FiskaltrustSignatureFormats.Text | optional,
                    FtSignatureType = FiskaltrustDeSignatureTypes.SignatureLogTime,
                    Data = "2026-09-19T06:00:01.000Z"
                },
                new FiskaltrustSignatureItem
                {
                    FtSignatureFormat = FiskaltrustSignatureFormats.Text | optional,
                    FtSignatureType = FiskaltrustDeSignatureTypes.SignatureAlgorithm,
                    Data = "ecdsa-plain-SHA256"
                },
                new FiskaltrustSignatureItem
                {
                    FtSignatureFormat = FiskaltrustSignatureFormats.Text | optional,
                    FtSignatureType = FiskaltrustDeSignatureTypes.LogTimeFormat,
                    Data = "unixTime"
                },
                new FiskaltrustSignatureItem
                {
                    FtSignatureFormat = FiskaltrustSignatureFormats.Text | optional,
                    FtSignatureType = FiskaltrustDeSignatureTypes.Signature,
                    Data = "SIG"
                },
                new FiskaltrustSignatureItem
                {
                    FtSignatureFormat = FiskaltrustSignatureFormats.Text | optional,
                    FtSignatureType = FiskaltrustDeSignatureTypes.PublicKey,
                    Data = "PUB"
                },
                new FiskaltrustSignatureItem
                {
                    FtSignatureFormat = FiskaltrustSignatureFormats.Text,
                    FtSignatureType = FiskaltrustDeSignatureTypes.ProcessStartTime,
                    Data = "2026-09-19T05:59:59.000Z"
                },
                new FiskaltrustSignatureItem
                {
                    FtSignatureFormat = FiskaltrustSignatureFormats.Text,
                    FtSignatureType = FiskaltrustDeSignatureTypes.CertificationIdentification,
                    Data = "BSI-K-TR-TEST"
                },
                new FiskaltrustSignatureItem
                {
                    FtSignatureFormat = FiskaltrustSignatureFormats.Text,
                    FtSignatureType = FiskaltrustDeSignatureTypes.TseSerialNumber,
                    Data = "TSE-SERIAL"
                }
            ]
        };

        var evidence = FiskaltrustGermanReceiptProjection.Extract(receipt);

        assert(
            evidence.HasQrPayload &&
            evidence.HasTextFiscalCore &&
            evidence.TseSerialNumber == "TSE-SERIAL" &&
            evidence.CertificationIdentification == "BSI-K-TR-TEST",
            "fiskaltrust German ReceiptResponse projects QR, text fiscal core, certification and TSE serial");

        assert(
            evidence.BuildComparableQrPayload() == receipt.FtSignatures[0].Data,
            "fiskaltrust German signature fields rebuild the exact V0 QR payload without timestamp/signature rewriting");

        var qrPrintable =
            FiskaltrustGermanReceiptProjection.PrintableSignatures(receipt, preferQr: true);
        assert(
            qrPrintable.Count == 4 &&
            qrPrintable.Any(x => x.FtSignatureType == FiskaltrustDeSignatureTypes.KassenSichVQrPayload) &&
            qrPrintable.Any(x => x.FtSignatureType == FiskaltrustDeSignatureTypes.ProcessStartTime) &&
            qrPrintable.Any(x => x.FtSignatureType == FiskaltrustDeSignatureTypes.CertificationIdentification) &&
            qrPrintable.Any(x => x.FtSignatureType == FiskaltrustDeSignatureTypes.TseSerialNumber),
            "fiskaltrust QR printing keeps mandatory German signature items and drops only explicitly optional text items");

        var textPrintable =
            FiskaltrustGermanReceiptProjection.PrintableSignatures(receipt, preferQr: false);
        assert(
            textPrintable.Count == receipt.FtSignatures.Count,
            "fiskaltrust text mode preserves every returned signature item instead of silently discarding compliance data");

        assert(
            receipt.FtReceiptHeader.SequenceEqual(["MW HEADER"]) &&
            receipt.FtChargeLines.SequenceEqual(["MW CHARGE"]) &&
            receipt.FtPayLines.SequenceEqual(["MW PAY"]) &&
            receipt.FtReceiptFooter.SequenceEqual(["MW FOOTER"]),
            "fiskaltrust ReceiptResponse model preserves Middleware-added printable header/charge/pay/footer supplements");

        assert(
            FiskaltrustDeCases.ReceiptRequestFlag == 0x0000800000000000UL &&
            FiskaltrustDeCases.WithReceiptRequest(FiskaltrustDeCases.PosReceipt) ==
                (FiskaltrustDeCases.PosReceipt | 0x0000800000000000UL),
            "fiskaltrust DE recovery uses the documented high ReceiptRequest flag, preventing blind duplicate fiscal actions");

        assert(
            FiskaltrustDeCases.ChargeItemCaseForVat(19m) == 0x4445000000000001UL &&
            FiskaltrustDeCases.ChargeItemCaseForVat(7m) == 0x4445000000000002UL &&
            FiskaltrustDeCases.ChargeItemCaseForVat(0m) == 0x4445000000000006UL,
            "fiskaltrust DE charge-item mapping distinguishes TOR 19 %, 7 % and 0 % VAT cases");

        assert(
            FiskaltrustDeCases.DebitCardPayment != FiskaltrustDeCases.CreditCardPayment &&
            FiskaltrustDeCases.CashPayment != FiskaltrustDeCases.DebitCardPayment,
            "fiskaltrust DE payment constants keep cash, debit and credit card semantically distinct");

        var recoveryCashBoxId = Guid.NewGuid();
        var recoveryPosId = Guid.NewGuid();
        var recoveryHandler = new FakeHandler();
        using (var http = new HttpClient(recoveryHandler))
        {
            var recoveryClient = new FiskaltrustMiddlewareClient(
                http,
                new FiskaltrustMiddlewareOptions(
                    new Uri("http://localhost:1500/queue-test/"),
                    recoveryCashBoxId,
                    recoveryPosId,
                    "TOR-POS-01"));

            var original = new FiskaltrustReceiptRequest
            {
                CbReceiptReference = "BON-4711",
                CbReceiptMoment = DateTimeOffset.Parse("2026-09-19T06:20:00Z"),
                FtReceiptCase = FiskaltrustDeCases.PosReceipt,
                CbChargeItems =
                [
                    new FiskaltrustChargeItem
                    {
                        Position = 1,
                        Quantity = 1,
                        Description = "Test",
                        Amount = 10m,
                        VatRate = 19m,
                        FtChargeItemCase = FiskaltrustDeCases.StandardChargeItem
                    }
                ],
                CbPayItems =
                [
                    new FiskaltrustPayItem
                    {
                        Position = 1,
                        Quantity = 1,
                        Description = "Bar",
                        Amount = 10m,
                        FtPayItemCase = FiskaltrustDeCases.CashPayment
                    }
                ]
            };

            _ = await recoveryClient.RecoverAsync(original);

            assert(
                recoveryHandler.LastPath == "/queue-test/json/v1/Sign" &&
                recoveryHandler.LastBody.Contains("\"cbReceiptReference\":\"BON-4711\"", StringComparison.Ordinal) &&
                recoveryHandler.LastBody.Contains("\"description\":\"Test\"", StringComparison.Ordinal) &&
                recoveryHandler.LastBody.Contains(
                    "\"ftReceiptCase\":" +
                    FiskaltrustDeCases.WithReceiptRequest(FiskaltrustDeCases.PosReceipt),
                    StringComparison.Ordinal),
                "fiskaltrust recovery reuses the original reference/items and adds only the documented ReceiptRequest flag");
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string LastPath { get; private set; } = "";
        public bool SawCashboxHeader { get; private set; }
        public string CashboxHeader { get; private set; } = "";
        public bool SawAccessTokenHeader { get; private set; }
        public string AccessTokenHeader { get; private set; } = "";
        public string LastBody { get; private set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastPath = request.RequestUri?.AbsolutePath ?? "";

            SawCashboxHeader =
                request.Headers.TryGetValues("cashboxid", out var cashboxValues);
            CashboxHeader =
                cashboxValues?.SingleOrDefault() ?? "";

            SawAccessTokenHeader =
                request.Headers.TryGetValues("accesstoken", out var tokenValues);
            AccessTokenHeader =
                tokenValues?.SingleOrDefault() ?? "";

            var requestedMessage = request.Content is null
                ? ""
                : request.Content.ReadAsStringAsync(cancellationToken)
                    .GetAwaiter()
                    .GetResult();
            LastBody = requestedMessage;

            var echo = requestedMessage.Contains("SAAS", StringComparison.Ordinal)
                ? "SAAS"
                : "TOR POS TEST";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"message\":\"" + echo + "\"}",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
