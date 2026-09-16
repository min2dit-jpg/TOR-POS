using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

public static class R71ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool,string> assert,
        Func<Func<Task>,string,Task> reject)
    {
        var dir = Path.Combine(root, "r71");
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, "promotion.db");
        var db = new SqliteDatabase(path);
        var backup = new DatabaseBackupService(db);
        var migrator = new SchemaMigrationService(
            db,
            backup,
            Path.Combine(dir, "migration-backups"));

        var migration = await migrator.InitializeDatabaseAsync();
        assert(
            migration.ToVersion == SchemaMigrationService.TargetSchemaVersion &&
            SchemaMigrationService.TargetSchemaVersion >= 3,
            "R71 Angebot schema and immutable sale snapshot columns survive later ordered migrations");

        var service = new PromotionCampaignService(db);
        var today = DateOnly.FromDateTime(DateTime.Now);

        var globalId = await service.CreateAsync(
            new PromotionCreateRequest(
                "GLOBAL 15",
                15,
                today.AddDays(-1),
                today.AddDays(2),
                PromotionScope.All,
                0,
                "Alle Artikel"),
            "admin");

        var global = await service.GetBestForProductAsync(100, 10, today);
        assert(
            global is not null &&
            global.PromotionId == globalId &&
            global.DiscountPercent == 15,
            "R71 ALL promotion automatically applies to catalog products during active date range");

        await service.CreateAsync(
            new PromotionCreateRequest(
                "FUTURE 30",
                30,
                today.AddDays(5),
                today.AddDays(8),
                PromotionScope.All,
                0,
                "Alle Artikel"),
            "admin");

        var dateFiltered = await service.GetBestForProductAsync(100, 10, today);
        assert(
            dateFiltered is not null && dateFiltered.DiscountPercent == 15,
            "R71 future Angebot does not become active before its start date");

        var categoryId = await service.CreateAsync(
            new PromotionCreateRequest(
                "CATEGORY 20",
                20,
                today,
                today.AddDays(3),
                PromotionScope.Category,
                10,
                "Döner"),
            "admin");

        var category = await service.GetBestForProductAsync(101, 10, today);
        assert(
            category is not null &&
            category.PromotionId == categoryId &&
            category.DiscountPercent == 20,
            "R71 Warengruppe promotion overrides lower global promotion");

        var productId = await service.CreateAsync(
            new PromotionCreateRequest(
                "PRODUCT 20",
                20,
                today,
                today.AddDays(3),
                PromotionScope.Product,
                100,
                "Döner Teller"),
            "admin");

        var product = await service.GetBestForProductAsync(100, 10, today);
        assert(
            product is not null &&
            product.PromotionId == productId,
            "R71 equal percentage tie resolves PRODUCT before CATEGORY before ALL");

        var engine = new SaleEngine();
        engine.Add(
            new Product
            {
                Id = 100,
                CategoryId = 10,
                Name = "Test Getränk",
                BasePriceCents = 1000,
                PfandCents = 25,
                VatRate = 19m
            },
            promotion: product);

        var line = engine.Cart.Single();
        assert(
            line.EffectiveListUnitPriceCents == 1025 &&
            line.PromotionDiscountUnitCents == 200 &&
            line.UnitPriceCents == 825 &&
            line.PfandCents == 25,
            "R71 Angebot discounts merchandise but never Pfand");

        var checkout = new CheckoutSnapshot(
            Guid.NewGuid().ToString("N"),
            CheckoutSnapshot.CopyLines(engine.Cart),
            0,
            PaymentMethod.Cash,
            "tester",
            null);

        assert(
            checkout.Lines.Single().PromotionId == productId &&
            checkout.Lines.Single().PromotionName == "PRODUCT 20" &&
            checkout.Lines.Single().PromotionDiscountCents == 200,
            "R71 checkout deep copy preserves immutable Angebot snapshot");

        var parkedRepo = new ParkedReceiptRepository(db);
        var parked = await parkedRepo.ParkAsync(
            checkout.Lines,
            0,
            "tester",
            training: true);

        var reloadedPark = await parkedRepo.GetOpenByIdAsync(
            parked.Id,
            training: true);

        assert(
            reloadedPark is not null &&
            reloadedPark.Lines.Single().PromotionId == productId &&
            reloadedPark.Lines.Single().EffectiveListUnitPriceCents == 1025 &&
            reloadedPark.Lines.Single().UnitPriceCents == 825,
            "R71 parked order keeps original Angebot price snapshot even for later checkout");

        await service.DisableAsync(
            productId,
            "admin",
            "Aktion beendet");

        var afterDisable = await service.GetBestForProductAsync(100, 10, today);
        assert(
            afterDisable is not null &&
            afterDisable.PromotionId == categoryId,
            "R71 disabling a product Angebot immediately falls back to next valid campaign");

        using (var c = db.OpenConnection())
        {
            using var q = c.CreateCommand();
            q.CommandText = """
                SELECT COUNT(*)
                FROM audit_log
                WHERE event_type IN ('PROMOTION_CREATED','PROMOTION_DISABLED');
                """;
            assert(
                Convert.ToInt32(q.ExecuteScalar()) == 5,
                "R71 Angebot create/disable actions are recorded in immutable audit_log");
        }

        await reject(
            async () =>
            {
                using var c = db.OpenConnection();
                using var q = c.CreateCommand();
                q.CommandText = "UPDATE promotion_campaigns SET discount_percent=30 WHERE id=$id;";
                q.Parameters.AddWithValue("$id", categoryId);
                await q.ExecuteNonQueryAsync();
            },
            "R71 campaign terms cannot be edited in place; disable and create new is enforced");

        await reject(
            async () =>
            {
                using var c = db.OpenConnection();
                using var q = c.CreateCommand();
                q.CommandText = "DELETE FROM promotion_campaigns WHERE id=$id;";
                q.Parameters.AddWithValue("$id", categoryId);
                await q.ExecuteNonQueryAsync();
            },
            "R71 campaign history cannot be deleted");

        // Insert one immutable completed-sale snapshot directly for report testing.
        // Production SaleRepository stays fiscal-gated and is intentionally not bypassed by application code.
        using (var c = db.OpenConnection())
        using (var tx = c.BeginTransaction())
        {
            long saleId;
            using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = """
                    INSERT INTO sales(
                        receipt_number,pickup_number,created_at,payment_method,
                        subtotal_cents,discount_cents,total_cents,fiscal_status,
                        list_subtotal_cents,promotion_discount_cents,
                        transaction_type,original_sale_id)
                    VALUES(
                        900001,0,$created,'CASH',
                        800,100,700,'TEST_FIXTURE',
                        1000,200,'SALE',NULL);
                    SELECT last_insert_rowid();
                    """;
                q.Parameters.AddWithValue("$created", DateTimeOffset.Now.ToString("O"));
                saleId = Convert.ToInt64(q.ExecuteScalar());
            }

            using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = """
                    INSERT INTO sale_items(
                        sale_id,product_id,product_name,variant_name,barcode,quantity,
                        unit_price_cents,vat_rate,pfand_cents,line_total_cents,
                        list_unit_price_cents,list_line_total_cents,
                        promotion_id,promotion_name,promotion_percent,
                        promotion_discount_unit_cents,promotion_discount_cents,
                        promotion_start_date,promotion_end_date)
                    VALUES(
                        $sale,100,'Döner Teller','','',1,
                        800,19,0,800,
                        1000,1000,
                        $promotion,'CATEGORY 20',20,
                        200,200,$start,$end);
                    """;
                q.Parameters.AddWithValue("$sale", saleId);
                q.Parameters.AddWithValue("$promotion", categoryId);
                q.Parameters.AddWithValue("$start", today.ToString("yyyy-MM-dd"));
                q.Parameters.AddWithValue("$end", today.AddDays(3).ToString("yyyy-MM-dd"));
                q.ExecuteNonQuery();
            }

            tx.Commit();
        }

        var management = new BusinessManagementService(
            db,
            new SettingsRepository(db),
            new AuditLogRepository(db));

        var x = await management.BuildXReportAsync();
        var report = string.Join("\n", x.Lines);

        // German report expectations must stay fixed on English CI runners too.
        // Keep this independent of the production formatter so a regression fails.
        static string Euro(decimal amount) =>
            amount.ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) + " EUR";

        assert(
            report.Contains("Listenwert vor Angebot/Rabatt: " + Euro(10m)) &&
            report.Contains("Angebote / Aktionen: -" + Euro(2m)) &&
            report.Contains("Manuelle Rabatte: -" + Euro(1m)) &&
            report.Contains("Umsatz nach Rabatt (brutto): " + Euro(7m)) &&
            report.Contains(
                "19 % · Brutto " + Euro(7m) +
                " · Netto " + Euro(5.88m) +
                " · Steuer " + Euro(1.12m)) &&
            report.Contains("CATEGORY 20 · -20%"),
            "R71 X/Z turnover model separates list value, Angebot, manual Rabatt, actual revenue and VAT after discounts");
    }
}
