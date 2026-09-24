using System.Text;
using TorPos.Core;
using TorPos.Infrastructure;

// O-12: the article CSV import turned an unknown USt into 19 %, let one row
// overwrite the USt of the whole Warengruppe, set stock to 0 when the column
// was missing, changed stock without an audit trail and misread Excel's
// Windows-1252 files.
public static class O12CsvImportTests
{
    private const string Header = "GRUPPE;WARENGRUPPE;ARTIKEL;ARTIKELNUMMER;EAN;PREIS_CENT;UST;PFAND_CENT;EINHEIT;AKTIV;BESTAND;MINDESTBESTAND;EINKAUFSPREIS_CENT\n";

    public static async Task Run(string root, Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "o12-csv-import");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "o12.db"));
        var repo = new ProductRepository(db);
        var management = new BusinessManagementService(db, new SettingsRepository(db), new AuditLogRepository(db));
        var csv = Path.Combine(dir, "import.csv");

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        await File.WriteAllBytesAsync(csv, Encoding.GetEncoding(1252).GetBytes(
            Header + "O12 Gruppe;O12 Speisen;O12 Döner Spezial;;1200001;650;7;0;Stück;1;12;0;0\r\n"));
        var imported = await management.ImportArticlesCsvAsync(csv, "tester");
        var doener = (await repo.GetActiveProductsAsync()).SingleOrDefault(x => x.Barcode == "1200001");
        assert(
            imported == 1 && doener?.Name == "O12 Döner Spezial" && doener.StockQuantity == 12m,
            $"O-12 a Windows-1252 CSV saved by Excel keeps its umlauts ({doener?.Name})");

        await File.WriteAllTextAsync(csv,
            Header +
            "O12 Gruppe;O12 Getränke;O12 Ayran;;1200002;250;19;0;Stück;1;0;0;0\n" +
            "O12 Gruppe;O12 Getränke;O12 Tee;;1200003;200;16;0;Stück;1;0;0;0\n");
        var vatError = await ErrorAsync(() => management.ImportArticlesCsvAsync(csv, "tester"));
        assert(
            vatError.Contains("Zeile 3 (O12 Tee): USt \"16\" ungültig", StringComparison.Ordinal) &&
            (await repo.GetActiveProductsAsync()).All(x => x.Barcode != "1200002"),
            "O-12 a row with an unknown USt stops the import with the line number instead of becoming 19 %, and nothing of the file is written");

        await File.WriteAllTextAsync(csv,
            Header + "O12 Gruppe;O12 Speisen;O12 Cola;;1200004;300;19;0;Stück;1;0;0;0\n");
        var categoryError = await ErrorAsync(() => management.ImportArticlesCsvAsync(csv, "tester"));
        var speisen = (await repo.GetCategoriesAsync()).Single(x => x.Name == "O12 Speisen");
        assert(
            categoryError.Contains("Warengruppe \"O12 Speisen\" hat USt 7 %", StringComparison.Ordinal) &&
            speisen.VatRate == 7m,
            "O-12 a CSV row cannot silently change the USt of an existing Warengruppe (and so of all its articles)");

        await File.WriteAllTextAsync(csv,
            "GRUPPE;WARENGRUPPE;ARTIKEL;ARTIKELNUMMER;EAN;PREIS_CENT;UST\n" +
            "O12 Gruppe;O12 Speisen;O12 Döner Spezial;;1200001;700;7\n");
        await management.ImportArticlesCsvAsync(csv, "tester");
        var kept = (await repo.GetActiveProductsAsync()).Single(x => x.Barcode == "1200001");

        await File.WriteAllTextAsync(csv,
            Header + "O12 Gruppe;O12 Speisen;O12 Döner Spezial;;1200001;700;7;0;Stück;1;5;0;0\n");
        await management.ImportArticlesCsvAsync(csv, "tester");
        var counted = (await repo.GetActiveProductsAsync()).Single(x => x.Barcode == "1200001");
        string stockAudit;
        await using (var c = db.OpenConnection())
        await using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT COALESCE(group_concat(details,' | '),'') FROM audit_log WHERE event_type='ARTICLE_IMPORT_STOCK';";
            stockAudit = Convert.ToString(await q.ExecuteScalarAsync()) ?? "";
        }
        assert(
            kept.BasePriceCents == 700 && kept.StockQuantity == 12m &&
            counted.StockQuantity == 5m &&
            stockAudit.Contains("O12 Döner Spezial: 12 -> 5", StringComparison.Ordinal),
            $"O-12 a CSV without a BESTAND column keeps the counted stock, and a stock set by import is written to the audit log ({stockAudit})");
    }

    private static async Task<string> ErrorAsync(Func<Task> action)
    {
        try
        {
            await action();
            return "";
        }
        catch (InvalidDataException ex)
        {
            return ex.Message;
        }
    }
}
