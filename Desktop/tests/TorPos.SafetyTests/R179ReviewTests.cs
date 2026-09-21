using System.Text.Json;
using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.SafetyTests;

public static class R179ReviewTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var first = CumulativeReturnProration.Allocate(
            previousRawCents: 0,
            currentRawCents: 500,
            previousTotalCents: 0,
            previousCashCents: 0,
            originalSubtotalCents: 1000,
            originalTotalCents: 901,
            originalCashCents: 451);
        var second = CumulativeReturnProration.Allocate(
            previousRawCents: 500,
            currentRawCents: 500,
            previousTotalCents: first.TotalCents,
            previousCashCents: first.CashCents,
            originalSubtotalCents: 1000,
            originalTotalCents: 901,
            originalCashCents: 451);

        assert(
            first.TotalCents == 451 && second.TotalCents == 450 &&
            first.TotalCents + second.TotalCents == 901,
            "R179 partial-return manual discount is allocated cumulatively and cannot create an extra cent");
        assert(
            first.DiscountCents + second.DiscountCents == 99,
            "R179 cumulative partial-return discount absorbs the remaining cent exactly");
        assert(
            first.CashCents == 226 && second.CashCents == 225 &&
            first.CashCents + second.CashCents == 451 &&
            first.CardCents + second.CardCents == 450,
            "R179 mixed-payment partial returns telescope exactly to the original cash/card split");

        var mainXaml = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml"));
        var mainSource = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"));
        var printerSetup = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/PrinterSetupWindow.cs"));
        var settingsSource = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/SettingsWindow.axaml.cs"));
        var repositorySource = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Infrastructure/Infrastructure.cs"));
        var cloudSource = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Infrastructure/TorCloudSyncService.cs"));

        assert(
            mainXaml.Contains("x:Name=\"ScannerCapture\"", StringComparison.Ordinal) &&
            mainXaml.Contains("PlaceholderText=\"Scannen oder EAN eingeben\"", StringComparison.Ordinal) &&
            !mainXaml.Contains("Width=\"1\" Height=\"1\"", StringComparison.Ordinal),
            "R179 cashier scanner uses a visible focusable EAN field instead of the 1px transparent capture");
        assert(
            mainSource.Contains("Math.Clamp(waitMs, 100, 2000)", StringComparison.Ordinal) &&
            mainSource.Contains("maxCharacterGap", StringComparison.Ordinal) &&
            !mainSource.Contains("averageGapMs > 170", StringComparison.Ordinal),
            "R179 scanner respects realistic configured timing and no longer drops suffix-less scans on the old 170 ms heuristic");
        assert(
            settingsSource.Contains("Combo(\"scanner.mode\", \"HID\")", StringComparison.Ordinal) &&
            settingsSource.Contains("COM ist in diesem Build nicht als produktiver Scannerpfad implementiert", StringComparison.Ordinal),
            "R179 settings no longer present unimplemented COM scanner mode as a selectable production path");
        assert(
            printerSetup.Contains("device.drawer.r179_configured", StringComparison.Ordinal) &&
            printerSetup.Contains("device.drawer.enabled", StringComparison.Ordinal) &&
            mainSource.Contains("device.receipt_printer.profile_drawer", StringComparison.Ordinal),
            "R179 printer center and checkout share one canonical drawer switch with a bounded legacy migration bridge");
        assert(
            repositorySource.Contains("\"storno-\"+saleId", StringComparison.Ordinal) &&
            repositorySource.Contains("\"return-\"+saleId", StringComparison.Ordinal) &&
            cloudSource.Contains("transaction_type=transactionType", StringComparison.Ordinal),
            "R179 Storno and Retoure are enqueued into TOR Cloud in the same local transaction");

        var dir = Path.Combine(root, "r179");
        Directory.CreateDirectory(dir);
        var db = new SqliteDatabase(Path.Combine(dir, "cloud-outbox.db"));
        var migrator = new SchemaMigrationService(
            db,
            new DatabaseBackupService(db),
            Path.Combine(dir, "migration-backups"));
        await migrator.InitializeDatabaseAsync();

        await using (var c = db.OpenConnection())
        {
            await using var setup = c.CreateCommand();
            setup.CommandText = "INSERT INTO app_settings(key,value) VALUES('cloud.configuration',$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
            setup.Parameters.AddWithValue(
                "$v",
                JsonSerializer.Serialize(new TorCloudConfiguration(
                    "https://cloud.example/",
                    "R179-TEST",
                    "protected-token",
                    true)));
            await setup.ExecuteNonQueryAsync();

            await using var tx = await c.BeginTransactionAsync();
            TorCloudOutbox.EnqueueRecordedSale(
                c,
                (Microsoft.Data.Sqlite.SqliteTransaction)tx,
                "return-179",
                "RETURN",
                179001,
                179002,
                0,
                DateTimeOffset.UtcNow,
                PaymentMethod.Mixed,
                564,
                0,
                564,
                300,
                264,
                "tester",
                [new TorCloudSaleLine(
                    179,
                    "Oliven",
                    "",
                    0.333m,
                    "kg",
                    1691,
                    564,
                    7m,
                    1990,
                    663,
                    179,
                    "GEWICHT 15",
                    15,
                    99,
                    "",
                    "",
                    Array.Empty<MenuComponentSnapshot>())]);
            await tx.CommitAsync();
        }

        string payload;
        await using (var c = db.OpenConnection())
        await using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT payload FROM cloud_outbox WHERE event_id='return-179';";
            payload = (string)(await q.ExecuteScalarAsync() ?? "");
        }
        using var doc = JsonDocument.Parse(payload);
        var p = doc.RootElement;
        assert(
            p.GetProperty("transaction_type").GetString() == "RETURN" &&
            p.GetProperty("original_receipt_number").GetInt64() == 179001 &&
            p.GetProperty("payment_method").GetString() == "MIXED",
            "R179 Cloud reversal payload carries type, original receipt and mixed tender explicitly");
        var item = p.GetProperty("items").EnumerateArray().First();
        assert(
            item.GetProperty("line_total_cents").GetInt64() == 564 &&
            item.GetProperty("list_unit_price_cents").GetInt64() == 1990 &&
            item.GetProperty("promotion_discount_cents").GetInt64() == 99,
            "R179 Cloud payload carries the weighted-promotion line decomposition instead of forcing quantity times reduced unit price");
    }

    private static string FindRepoFile(string relative)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException(relative);
    }
}
