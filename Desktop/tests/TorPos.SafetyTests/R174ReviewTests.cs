using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

public static class R174ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert)
    {
        var promotion = new PromotionSnapshot(
            174,
            "GEWICHT 10",
            10,
            "2026-09-01",
            "2026-09-30",
            PromotionScope.All,
            0);

        var engine = new SaleEngine();
        engine.Add(
            new Product
            {
                Id = 1740,
                CategoryId = 174,
                Name = "Baklava",
                Unit = "kg",
                BasePriceCents = 1990,
                VatRate = 7m
            },
            quantity: 0.500m,
            promotion: promotion);

        var halfKg = engine.Cart.Single();
        assert(
            halfKg.ListLineTotalCents == 995 &&
            halfKg.PromotionDiscountCents == 100 &&
            halfKg.LineTotalCents == 895 &&
            halfKg.ListLineTotalCents - halfKg.PromotionDiscountCents == halfKg.LineTotalCents,
            "R174 0.500 kg at 19.90 EUR/kg with 10% promotion is cent-exact at 8.95 EUR");

        var thirds = new SaleEngine();
        thirds.Add(
            new Product
            {
                Id = 1741,
                CategoryId = 174,
                Name = "Oliven",
                Unit = "kg",
                BasePriceCents = 1990,
                VatRate = 7m
            },
            quantity: 0.333m,
            promotion: new PromotionSnapshot(
                175,
                "GEWICHT 15",
                15,
                "2026-09-01",
                "2026-09-30",
                PromotionScope.All,
                0));

        var thirdKg = thirds.Cart.Single();
        assert(
            thirdKg.ListLineTotalCents == 663 &&
            thirdKg.PromotionDiscountCents == 99 &&
            thirdKg.LineTotalCents == 564 &&
            thirdKg.ListLineTotalCents - thirdKg.PromotionDiscountCents == thirdKg.LineTotalCents,
            "R174 weighted promotion rounds once at line level instead of multiplying a rounded per-kg discount");

        var snapshot = new CheckoutSnapshot(
            Guid.NewGuid().ToString("N"),
            CheckoutSnapshot.CopyLines(engine.Cart),
            0,
            PaymentMethod.Cash,
            "tester",
            null);

        assert(
            snapshot.Lines.Single().Unit == "kg" &&
            snapshot.Lines.Single().IsWeighted &&
            snapshot.Lines.Single().LineTotalCents == 895 &&
            snapshot.Lines.Single().PromotionDiscountCents == 100,
            "R174 checkout deep copy preserves kg semantics and weighted promotion totals");

        var dir = Path.Combine(root, "r174");
        Directory.CreateDirectory(dir);
        var db = new SqliteDatabase(Path.Combine(dir, "promotion-business-day.db"));
        var backup = new DatabaseBackupService(db);
        var migrator = new SchemaMigrationService(
            db,
            backup,
            Path.Combine(dir, "migration-backups"));
        await migrator.InitializeDatabaseAsync();

        var calendarToday = DateOnly.FromDateTime(DateTime.Now);
        var businessDay = calendarToday.AddDays(-1);
        var closeLocal = businessDay.ToDateTime(new TimeOnly(2, 0));
        var saleLocal = businessDay.ToDateTime(new TimeOnly(20, 0));
        var closeAt = new DateTimeOffset(
            closeLocal,
            TimeZoneInfo.Local.GetUtcOffset(closeLocal));
        var saleAt = new DateTimeOffset(
            saleLocal,
            TimeZoneInfo.Local.GetUtcOffset(saleLocal));

        await using (var c = db.OpenConnection())
        {
            await using (var z = c.CreateCommand())
            {
                z.CommandText = """
                    INSERT INTO z_report_archive(
                        z_number,created_at,period_from,period_to,operator_name,
                        receipt_count,gross_cents,cash_cents,card_cents,
                        vat7_gross_cents,vat19_gross_cents,fiscal_status,snapshot_text)
                    VALUES(
                        174,$created,$from,$to,'tester',
                        0,0,0,0,
                        0,0,'TEST','R174');
                    """;
                z.Parameters.AddWithValue("$created", closeAt.ToString("O"));
                z.Parameters.AddWithValue("$from", closeAt.AddHours(-8).ToString("O"));
                z.Parameters.AddWithValue("$to", closeAt.ToString("O"));
                await z.ExecuteNonQueryAsync();
            }

            await using (var sale = c.CreateCommand())
            {
                sale.CommandText = """
                    INSERT INTO sales(
                        receipt_number,created_at,payment_method,
                        subtotal_cents,discount_cents,total_cents,fiscal_status)
                    VALUES(
                        174001,$created,'CASH',
                        1000,0,1000,'TEST_FIXTURE');
                    """;
                sale.Parameters.AddWithValue("$created", saleAt.ToString("O"));
                await sale.ExecuteNonQueryAsync();
            }
        }

        var service = new PromotionCampaignService(db);
        var resolvedBusinessDay = await service.GetBusinessDateAsync();

        assert(
            resolvedBusinessDay == businessDay &&
            resolvedBusinessDay != calendarToday,
            "R174 open Z period keeps the previous operating date after calendar midnight");

        var campaignId = await service.CreateAsync(
            new PromotionCreateRequest(
                "NACHTANGEBOT",
                20,
                businessDay,
                businessDay,
                PromotionScope.All,
                0,
                "Alle Artikel"),
            "tester");

        var active = await service.GetBestForProductAsync(
            999,
            999);

        assert(
            active is not null &&
            active.PromotionId == campaignId &&
            active.DiscountPercent == 20,
            "R174 campaign ending on the operating day remains active until the Z period is closed");

        var verifier = File.ReadAllText(
            FindRepoFile("Desktop/tools/Verify-Simulator-Contract.ps1"));

        assert(
            verifier.Contains(".IndexOf([string]$label, [System.StringComparison]::OrdinalIgnoreCase)", StringComparison.Ordinal) &&
            verifier.Contains(".IndexOf([string]$color, [System.StringComparison]::OrdinalIgnoreCase)", StringComparison.Ordinal) &&
            !verifier.Contains(".Contains([string]$label, [System.StringComparison]::OrdinalIgnoreCase)", StringComparison.Ordinal),
            "R174 simulator verifier is compatible with Windows PowerShell 5.1 string APIs");

        var main = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"));
        var promotionWindow = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.App/PromotionManagementWindow.cs"));

        assert(
            main.Contains("await _promotions.GetBestForProductAsync(", StringComparison.Ordinal) &&
            !main.Contains("if (p.IsWeighted)\n                    promotion = null;", StringComparison.Ordinal) &&
            promotionWindow.Contains("GetBusinessDateAsync()", StringComparison.Ordinal),
            "R174 cashier no longer suppresses weighted promotions and promotion UI uses the operating day");

        var infrastructure = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Infrastructure/Infrastructure.cs"));

        assert(
            infrastructure.Contains("originalLine.LineTotalCentsFor(request.Quantity)", StringComparison.Ordinal) &&
            infrastructure.Contains("originalLine.ListLineTotalCentsFor(quantity)", StringComparison.Ordinal) &&
            infrastructure.Contains("originalLine.PromotionDiscountCentsFor(quantity)", StringComparison.Ordinal) &&
            main.Contains("LineTotalCentsFor(x.Quantity)", StringComparison.Ordinal),
            "R174 partial-return terminal amount and persisted return rows reuse the same weighted line-allocation math");
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
            $"R174 review could not locate repository file: {relativePath}");
    }
}
