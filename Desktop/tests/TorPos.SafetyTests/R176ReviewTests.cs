using TorPos.Core;
using TorPos.Infrastructure;

public static class R176ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert)
    {
        var weighted = new CartLine
        {
            ProductId = 1760,
            ProductName = "Oliven",
            Quantity = 1.000m,
            Unit = "kg",
            UnitPriceCents = 1691,
            ListUnitPriceCents = 1990,
            VatRate = 7m,
            PromotionId = 176,
            PromotionName = "GEWICHT 15",
            PromotionPercent = 15,
            PromotionDiscountUnitCents = 299
        };

        var first = weighted.LineTotalCentsSlice(0m, 0.333m);
        var second = weighted.LineTotalCentsSlice(0.333m, 0.333m);
        var third = weighted.LineTotalCentsSlice(0.666m, 0.334m);

        assert(
            first == 564 &&
            second == 562 &&
            third == 565 &&
            first + second + third == weighted.LineTotalCentsFor(1.000m),
            "R176 cumulative weighted return slices absorb rounding remainder and telescope exactly to the original line");

        var listParts =
            weighted.ListLineTotalCentsSlice(0m, 0.333m) +
            weighted.ListLineTotalCentsSlice(0.333m, 0.333m) +
            weighted.ListLineTotalCentsSlice(0.666m, 0.334m);
        var promotionParts =
            weighted.PromotionDiscountCentsSlice(0m, 0.333m) +
            weighted.PromotionDiscountCentsSlice(0.333m, 0.333m) +
            weighted.PromotionDiscountCentsSlice(0.666m, 0.334m);

        assert(
            listParts == weighted.ListLineTotalCents &&
            promotionParts == weighted.PromotionDiscountCents &&
            listParts - promotionParts == first + second + third,
            "R176 list-price and promotion cents use the same cumulative allocation as the returned net line");

        var dir = Path.Combine(root, "r176");
        Directory.CreateDirectory(dir);
        var db = new SqliteDatabase(Path.Combine(dir, "business-date.db"));
        var backup = new DatabaseBackupService(db);
        var migrator = new SchemaMigrationService(
            db,
            backup,
            Path.Combine(dir, "migration-backups"));
        await migrator.InitializeDatabaseAsync();

        var old = DateTimeOffset.Now.AddYears(-2);
        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = """
                INSERT INTO sales(
                    receipt_number,created_at,payment_method,
                    subtotal_cents,discount_cents,total_cents,fiscal_status)
                VALUES(176001,$created,'CASH',100,0,100,'TEST_FIXTURE');
                """;
            q.Parameters.AddWithValue("$created", old.ToString("O"));
            await q.ExecuteNonQueryAsync();
        }

        var businessDate =
            await new PromotionCampaignService(db)
                .GetBusinessDateAsync();

        assert(
            businessDate == DateOnly.FromDateTime(DateTime.Now),
            "R176 a database with historical sales but no Z closing uses today's business date instead of the first sale ever");

        var config = new FiskaltrustLocalQueueConfiguration(
            "test.json",
            "cashbox-test",
            "queue-test",
            "rest://localhost:1500/queue-test");

        var cashSale = new Sale
        {
            Id = 176,
            ReceiptNumber = 176001,
            CreatedAt = DateTimeOffset.UtcNow,
            PaymentMethod = PaymentMethod.Cash,
            CashPortionCents = 895,
            CardPortionCents = 0,
            TotalCents = 895,
            DiscountCents = 0,
            TransactionType = "SALE",
            OperatorName = "tester",
            ImHaus = false,
            Lines =
            [
                new CartLine
                {
                    ProductId = 1,
                    ProductName = "Baklava",
                    Quantity = 0.500m,
                    Unit = "kg",
                    UnitPriceCents = 1790,
                    ListUnitPriceCents = 1990,
                    VatRate = 7m,
                    PromotionId = 1,
                    PromotionName = "ANGEBOT",
                    PromotionPercent = 10,
                    PromotionDiscountUnitCents = 200
                }
            ]
        };

        var request = FiskaltrustSaleMapper.CreatePosReceipt(
            cashSale,
            config,
            "tor-pos-test",
            "TOR-1",
            "sale-176",
            null,
            "Kasse 1");

        assert(
            request.FtReceiptCase == FiskaltrustDeCases.PosReceipt &&
            request.CbReceiptReference == "sale-176" &&
            request.CbChargeItems.Sum(x => x.Amount) == 8.95m &&
            request.CbPayItems.Single().FtPayItemCase == FiskaltrustDeCases.PayCashEur &&
            request.CbPayItems.Single().Amount == 8.95m,
            "R176 TOR sale mapper creates a reconciled explicit DE POS receipt request");

        assert(
            request.CbChargeItems.Single().Quantity == 1m &&
            request.CbChargeItems.Single().UnitQuantity == 0.500m &&
            request.CbChargeItems.Single().Unit == "kg" &&
            (request.CbChargeItems.Single().FtChargeItemCase & FiskaltrustDeCases.TakeAwayFlag) != 0,
            "R176 weighted take-away sale maps kg to unit quantity and carries the DE take-away flag");

        var cardSale = new Sale
        {
            Id = 177,
            ReceiptNumber = 176002,
            CreatedAt = DateTimeOffset.UtcNow,
            PaymentMethod = PaymentMethod.Card,
            CashPortionCents = 0,
            CardPortionCents = 100,
            TotalCents = 100,
            TransactionType = "SALE",
            Lines =
            [
                new CartLine
                {
                    ProductId = 2,
                    ProductName = "Test",
                    Quantity = 1m,
                    UnitPriceCents = 100,
                    ListUnitPriceCents = 100,
                    VatRate = 19m
                }
            ]
        };

        var missingTenderRejected = false;
        try
        {
            _ = FiskaltrustSaleMapper.CreatePosReceipt(
                cardSale,
                config,
                "tor-pos-test",
                "TOR-1",
                "sale-card",
                null);
        }
        catch (InvalidOperationException ex)
        {
            missingTenderRejected =
                ex.Message.Contains("Kartenart", StringComparison.Ordinal);
        }

        assert(
            missingTenderRejected,
            "R176 generic TOR 'Karte' is never guessed as debit/credit for fiskaltrust");

        var response = new FiskaltrustReceiptResponse(
            "cashbox-test",
            "queue-test",
            "queue-item",
            1,
            "TOR-1",
            "sale-176",
            "TOR-1",
            "ftC#T10",
            DateTimeOffset.UtcNow,
            [
                new FiskaltrustSignature(1, FiskaltrustDeCases.SignatureTransactionNumber, "", "10"),
                new FiskaltrustSignature(1, FiskaltrustDeCases.SignatureCounter, "", "19"),
                new FiskaltrustSignature(1, FiskaltrustDeCases.SignatureStartTime, "", "2026-09-21T10:47:42.000Z"),
                new FiskaltrustSignature(1, FiskaltrustDeCases.SignatureLogTime, "", "2026-09-21T10:48:17.000Z"),
                new FiskaltrustSignature(1, FiskaltrustDeCases.SignatureValue, "", "SIG"),
                new FiskaltrustSignature(1, FiskaltrustDeCases.SignatureTseSerial, "", "TSE-SERIAL"),
                new FiskaltrustSignature(3, FiskaltrustDeCases.SignatureQr, "", "V0;...")
            ],
            0,
            "");

        var parsed =
            FiskaltrustQueueClient.ParseFiscalResult(response);

        assert(
            parsed.Success &&
            parsed.TransactionNumber == 10 &&
            parsed.SignatureCounter == 19 &&
            parsed.TseSerialNumber == "TSE-SERIAL" &&
            parsed.Signature == "SIG" &&
            parsed.StartLogTime is not null &&
            parsed.LogTime is not null,
            "R176 parses mandatory TSE receipt fields from fiskaltrust DE signature items");

        var clientSource = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Infrastructure/FiskaltrustQueueClient.cs"));
        var mapperSource = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Infrastructure/FiskaltrustSaleMapper.cs"));
        var mainSource = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"));
        var repositorySource = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Infrastructure/Infrastructure.cs"));

        assert(
            clientSource.Contains("json/v1/Sign", StringComparison.Ordinal) &&
            clientSource.Contains("StartTransaction", StringComparison.Ordinal) &&
            clientSource.Contains("PosReceipt", StringComparison.Ordinal),
            "R176 fiskaltrust client uses Middleware v1 Sign with explicit start and POS-receipt cases");

        assert(
            !clientSource.Contains("accesstoken", StringComparison.OrdinalIgnoreCase) &&
            clientSource.Contains("erlaubt nur lokale Queue-Endpunkte", StringComparison.Ordinal),
            "R176 transaction client is local-only and contains no access-token handling");

        assert(
            mapperSource.Contains("STORNO/RETURN noch nicht freigegeben", StringComparison.Ordinal) &&
            mapperSource.Contains("Kartenart fehlt", StringComparison.Ordinal),
            "R176 unsupported reversal/card semantics fail closed instead of inventing fiskaltrust cases");

        assert(
            mainSource.Contains("QuoteReturnAsync(", StringComparison.Ordinal) &&
            repositorySource.Contains("LineTotalCentsSlice(", StringComparison.Ordinal) &&
            repositorySource.Contains("PromotionDiscountCentsSlice(", StringComparison.Ordinal),
            "R176 terminal refund preview and persisted return share the same cumulative-cent allocation");

        var fiscalGate = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Core/CheckoutSafety.cs"));

        assert(
            fiscalGate.Contains("PhysicalTseE2EValidated = false", StringComparison.Ordinal) &&
            fiscalGate.Contains("IndependentFiscalReviewValidated = false", StringComparison.Ordinal),
            "R176 fiskaltrust transaction foundation does not open the fiscal production gate");

        var manifest = File.ReadAllText(
            FindRepoFile("Desktop/manifest.json"));

        assert(
            manifest.Contains("\"alternative_transaction_client_implemented\": true", StringComparison.Ordinal) &&
            manifest.Contains("\"alternative_transaction_path_implemented\": false", StringComparison.Ordinal),
            "R176 manifest distinguishes implemented fiskaltrust client/mapping from not-yet-released runtime transaction path");
    }

    private static string FindRepoFile(string relativePath)
    {
        foreach (var start in new[]
                 {
                     AppContext.BaseDirectory,
                     Directory.GetCurrentDirectory()
                 })
        {
            for (var dir = new DirectoryInfo(start);
                 dir is not null;
                 dir = dir.Parent)
            {
                var candidate = Path.Combine(
                    dir.FullName,
                    relativePath.Replace(
                        '/',
                        Path.DirectorySeparatorChar));

                if (File.Exists(candidate))
                    return candidate;
            }
        }

        throw new FileNotFoundException(
            $"R176 review could not locate repository file: {relativePath}");
    }
}
