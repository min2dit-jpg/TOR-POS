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

        var zeroVatBlocked = false;
        try
        {
            _ = FiskaltrustDeCases.ChargeItemCaseForVat(0m);
        }
        catch (InvalidOperationException ex)
        {
            zeroVatBlocked = ex.Message.Contains("getrennte DE-Fälle", StringComparison.Ordinal);
        }

        assert(
            FiskaltrustDeCases.ChargeItemCaseForVat(19m) == 0x4445000000000001UL &&
            FiskaltrustDeCases.ChargeItemCaseForVat(7m) == 0x4445000000000002UL &&
            zeroVatBlocked,
            "fiskaltrust DE auto-mapping handles 19/7 % and refuses to guess the legal 0 % category");

        assert(
            FiskaltrustDeCases.DebitCardPayment != FiskaltrustDeCases.CreditCardPayment &&
            FiskaltrustDeCases.CashPayment != FiskaltrustDeCases.DebitCardPayment,
            "fiskaltrust DE payment constants keep cash, debit and credit card semantically distinct");

        var stateObject = System.Text.Json.JsonDocument.Parse("{\"TseInfo\":{\"State\":\"Ready\"}}").RootElement.Clone();
        var contractProbe = new FiskaltrustReceiptResponse
        {
            FtQueueRow = ulong.MaxValue,
            FtStateData = stateObject,
            FtSignatures =
            [
                new FiskaltrustSignatureItem
                {
                    FtSignatureFormat = FiskaltrustSignatureFormats.OptionalPrintFlag |
                        FiskaltrustSignatureFormats.Text,
                    FtSignatureType = FiskaltrustDeSignatureTypes.ProcessType,
                    Data = "Kassenbeleg-V1"
                }
            ]
        };
        assert(
            contractProbe.FtQueueRow == ulong.MaxValue &&
            contractProbe.FtStateData?.ValueKind == System.Text.Json.JsonValueKind.Object &&
            FiskaltrustSignatureFormats.BaseFormat(contractProbe.FtSignatures[0].FtSignatureFormat) ==
                FiskaltrustSignatureFormats.Text,
            "fiskaltrust v1 response model preserves uint64 queue/format values and structured ftStateData");

        assert(
            FiskaltrustDeState.IsReady(FiskaltrustDeState.Ready) &&
            FiskaltrustDeState.HasFlag(
                0x4445000000000002UL,
                FiskaltrustDeState.TseCommunicationFailedFlag) &&
            FiskaltrustDeState.HasFlag(
                0x4445000000000100UL,
                FiskaltrustDeState.ScuSwitchingFlag) &&
            !FiskaltrustDeState.HasFlag(
                0x4154000000000002UL,
                FiskaltrustDeState.TseCommunicationFailedFlag),
            "fiskaltrust DE ftState decoder recognizes only documented German communication/switch states");

        var zero = FiskaltrustSandboxRequests.ZeroReceipt(
            "TOR-ZERO-0001",
            DateTimeOffset.Parse("2026-09-19T06:25:00Z"));
        assert(
            zero.CbReceiptReference == "TOR-ZERO-0001" &&
            zero.CbChargeItems.Count == 0 &&
            zero.CbPayItems.Count == 0 &&
            zero.FtReceiptCase ==
                FiskaltrustDeCases.WithImplicitFlow(FiskaltrustDeCases.ZeroReceipt),
            "fiskaltrust DE ZeroReceipt builder keeps charge/pay blocks empty and uses the required implicit flow");

        var zeroInfo = FiskaltrustSandboxRequests.ZeroReceiptWithTseInfo(
            "TOR-ZERO-INFO-0001",
            DateTimeOffset.Parse("2026-09-19T06:25:30Z"));
        assert(
            zeroInfo.CbChargeItems.Count == 0 &&
            zeroInfo.CbPayItems.Count == 0 &&
            zeroInfo.FtReceiptCase ==
                (FiskaltrustDeCases.WithImplicitFlow(FiskaltrustDeCases.ZeroReceipt) |
                 FiskaltrustDeCases.ZeroReceiptTseInfoFlag) &&
            (zeroInfo.FtReceiptCase &
             FiskaltrustDeCases.ZeroReceiptSelfTestAndTimeUpdateFlag) == 0,
            "fiskaltrust DE TSE-info ZeroReceipt requests status details without forcing self-test/time-update");

        var simpleCashSale = new TorPos.Core.Sale
        {
            ReceiptNumber = 1001,
            CreatedAt = DateTimeOffset.Parse("2026-09-19T06:26:00Z"),
            StartedAt = DateTimeOffset.Parse("2026-09-19T06:25:40Z"),
            PaymentMethod = TorPos.Core.PaymentMethod.Cash,
            CashPortionCents = 1190,
            CardPortionCents = 0,
            TotalCents = 1190,
            TransactionType = "SALE",
            ImHaus = false,
            OperatorName = "TEST",
            Lines =
            [
                new TorPos.Core.CartLine
                {
                    ProductId = 1,
                    ProductName = "Getränk",
                    Quantity = 1,
                    UnitPriceCents = 595,
                    VatRate = 19m
                },
                new TorPos.Core.CartLine
                {
                    ProductId = 2,
                    ProductName = "Snack",
                    Quantity = 1,
                    UnitPriceCents = 595,
                    VatRate = 7m,
                    ImHausApplicable = true
                }
            ]
        };
        var simpleRequest =
            FiskaltrustSandboxRequests.SimpleCashSale(simpleCashSale, "BON-1001");

        assert(
            simpleRequest.FtReceiptCase ==
                FiskaltrustDeCases.WithImplicitFlow(FiskaltrustDeCases.PosReceipt) &&
            simpleRequest.CbChargeItems.Count == 2 &&
            simpleRequest.CbPayItems.Count == 1 &&
            simpleRequest.CbReceiptAmount == 11.90m &&
            simpleRequest.CbChargeItems[0].Moment ==
                simpleCashSale.StartedAt &&
            simpleRequest.CbChargeItems[1].Moment ==
                simpleCashSale.CreatedAt,
            "fiskaltrust sandbox simple cash sale preserves TOR action start and exact receipt total");

        assert(
            simpleRequest.CbChargeItems[0].FtChargeItemCase ==
                FiskaltrustDeCases.StandardChargeItem &&
            simpleRequest.CbChargeItems[1].FtChargeItemCase ==
                (FiskaltrustDeCases.ReducedChargeItem |
                 FiskaltrustDeCases.TakeAwayChargeItemFlag) &&
            simpleRequest.CbPayItems[0].FtPayItemCase ==
                FiskaltrustDeCases.CashPayment,
            "fiskaltrust sandbox simple cash sale maps 19/7 VAT and take-away/cash cases explicitly");

        var blockedCard = false;
        try
        {
            _ = FiskaltrustSandboxRequests.SimpleCashSale(
                new TorPos.Core.Sale
                {
                    PaymentMethod = TorPos.Core.PaymentMethod.Card,
                    CardPortionCents = 100,
                    TotalCents = 100,
                    Lines =
                    [
                        new TorPos.Core.CartLine
                        {
                            ProductName = "Test",
                            Quantity = 1,
                            UnitPriceCents = 100,
                            VatRate = 19m
                        }
                    ]
                },
                "BON-CARD");
        }
        catch (InvalidOperationException ex)
        {
            blockedCard = ex.Message.Contains("Barzahlung", StringComparison.Ordinal);
        }
        assert(
            blockedCard,
            "fiskaltrust sandbox refuses generic CARD mapping until debit/credit evidence is available");

        var blockedPfand = false;
        try
        {
            _ = FiskaltrustSandboxRequests.SimpleCashSale(
                new TorPos.Core.Sale
                {
                    PaymentMethod = TorPos.Core.PaymentMethod.Cash,
                    CashPortionCents = 125,
                    TotalCents = 125,
                    TransactionType = "SALE",
                    Lines =
                    [
                        new TorPos.Core.CartLine
                        {
                            ProductId = 77,
                            ProductName = "Getränk inkl. Pfand",
                            Quantity = 1,
                            UnitPriceCents = 125,
                            VatRate = 19m,
                            PfandCents = 25
                        }
                    ]
                },
                "BON-PFAND");
        }
        catch (InvalidOperationException ex)
        {
            blockedPfand = ex.Message.Contains("Pfand", StringComparison.Ordinal);
        }
        assert(
            blockedPfand,
            "fiskaltrust sandbox refuses Pfand until dedicated DSFinV-K/fiskaltrust deposit mapping exists");

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

        var journalPath = Path.Combine(
            Path.GetTempPath(),
            "torpos-ft-journal-" + Guid.NewGuid().ToString("N") + ".db");
        var journalDb = new SqliteDatabase(journalPath);
        var journal = new FiskaltrustSignJournal(journalDb);
        await journal.InitializeAsync();

        var journalRequest = new FiskaltrustReceiptRequest
        {
            CbReceiptReference = "JOURNAL-1",
            CbReceiptMoment = DateTimeOffset.Parse("2026-09-19T06:30:00Z"),
            FtReceiptCase =
                FiskaltrustDeCases.WithImplicitFlow(FiskaltrustDeCases.ZeroReceipt),
            CbChargeItems = [],
            CbPayItems = []
        };

        var prepared = await journal.BeginAsync(journalRequest);
        var preparedAgain = await journal.BeginAsync(journalRequest);
        assert(
            prepared.Id == preparedAgain.Id &&
            preparedAgain.State == FiskaltrustSignState.Prepared,
            "fiskaltrust journal is idempotent for the same cbReceiptReference and identical payload");

        var changedPayloadRejected = false;
        try
        {
            _ = await journal.BeginAsync(
                journalRequest with
                {
                    CbReceiptAmount = 1m
                });
        }
        catch (InvalidOperationException ex)
        {
            changedPayloadRejected =
                ex.Message.Contains("anderen fiskaltrust Payload", StringComparison.Ordinal);
        }
        assert(
            changedPayloadRejected,
            "fiskaltrust journal refuses reusing one cbReceiptReference with a different payload");

        await journal.MarkSentAsync(prepared.Id);

        // Simulate process restart: a fresh repository instance must still see
        // SENT and the exact original request, so caller can only ReceiptRequest-recover.
        var afterRestart = new FiskaltrustSignJournal(journalDb);
        await afterRestart.InitializeAsync();
        var recoveryCandidates = await afterRestart.GetRecoveryCandidatesAsync();
        assert(
            recoveryCandidates.Count == 1 &&
            recoveryCandidates[0].State == FiskaltrustSignState.Sent &&
            recoveryCandidates[0].Request.CbReceiptReference == "JOURNAL-1" &&
            recoveryCandidates[0].Request.FtReceiptCase == journalRequest.FtReceiptCase,
            "fiskaltrust journal survives restart and exposes SENT operations for ReceiptRequest recovery");

        await afterRestart.MarkUnknownAsync(
            prepared.Id,
            "HTTP timeout after POST /Sign");
        var unknown = await afterRestart.GetByReferenceAsync("JOURNAL-1");
        assert(
            unknown is
            {
                State: FiskaltrustSignState.Unknown,
                Evidence: "HTTP timeout after POST /Sign"
            },
            "fiskaltrust journal preserves UNKNOWN evidence after an ambiguous external effect");

        var committedResponse = new FiskaltrustReceiptResponse
        {
            CbReceiptReference = "JOURNAL-1",
            FtReceiptIdentification = "FT-TEST-1",
            FtQueueItemId = "queue-item-1",
            FtState = FiskaltrustDeState.Ready
        };
        await afterRestart.MarkCommittedAsync(
            prepared.Id,
            committedResponse,
            "Recovered with ReceiptRequest");

        var committed = await afterRestart.GetByReferenceAsync("JOURNAL-1");
        var noneOpen = await afterRestart.GetRecoveryCandidatesAsync();
        assert(
            committed?.State == FiskaltrustSignState.Committed &&
            committed.Response?.FtReceiptIdentification == "FT-TEST-1" &&
            committed.Evidence == "Recovered with ReceiptRequest" &&
            noneOpen.Count == 0,
            "fiskaltrust journal commits recovered response durably and removes it from recovery candidates");

        var invalidResendTransitionRejected = false;
        try
        {
            await afterRestart.MarkSentAsync(prepared.Id);
        }
        catch (InvalidOperationException)
        {
            invalidResendTransitionRejected = true;
        }
        assert(
            invalidResendTransitionRejected,
            "fiskaltrust journal blocks moving a COMMITTED operation back to SENT");

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
