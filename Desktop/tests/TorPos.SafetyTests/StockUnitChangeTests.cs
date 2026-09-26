using TorPos.Core;
using TorPos.Infrastructure;

// Changing an article's unit (Stück -> kg) kept the counted numbers and read
// them in the new unit: 12 Stück became 12 kg, 0,5 kg became 0,5 Stück, and
// the Mindestbestand followed. A unit change on an article with a count now
// needs both values counted again in the new unit.
public static class StockUnitChangeTests
{
    private const string Header = "GRUPPE;WARENGRUPPE;ARTIKEL;ARTIKELNUMMER;EAN;PREIS_CENT;UST;PFAND_CENT;EINHEIT;AKTIV;BESTAND;MINDESTBESTAND;EINKAUFSPREIS_CENT\n";

    public static async Task Run(string root, Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "stock-unit-change");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "unit.db"));
        var repo = new ProductRepository(db);
        var management = new BusinessManagementService(db, new SettingsRepository(db), new AuditLogRepository(db));
        var csv = Path.Combine(dir, "import.csv");
        await File.WriteAllTextAsync(csv, Header +
            "Laden;Obst;Äpfel lose;;7700001;299;7;0;Stück;1;12;3;0\n" +
            "Laden;Obst;Kirschen;;7700002;899;7;0;kg;1;0,5;0,2;0\n" +
            "Laden;Obst;Neue Birne;;7700003;199;7;0;Stück;1;0;0;0\n");
        await management.ImportArticlesCsvAsync(csv, "tester");
        async Task<Product> Load(string ean) => (await repo.GetActiveProductsAsync()).Single(x => x.Barcode == ean);
        async Task<string> Error(Func<Task> action)
        {
            try { await action(); return ""; }
            catch (StockUnitChangeException ex) { return ex.Message; }
        }

        assert(
            StockUnitRules.NeedsRecount("Stück", "kg", 12m, 0m) && StockUnitRules.NeedsRecount("kg", "Stück", 0m, 0.2m) &&
            !StockUnitRules.NeedsRecount("Stück", " stück ", 12m, 3m) && !StockUnitRules.NeedsRecount("Stück", "kg", 0m, 0m),
            "Unit change: a new unit needs a recount when the article has a stock or a Mindestbestand; the same unit or an empty article does not");

        var apples = await Load("7700001");
        var toKg = Clone(apples, "kg");
        var silent = await Error(() => repo.SaveWithStockAsync(toKg, null, apples.StockQuantity, "tester"));
        var unconfirmed = await Error(() => repo.SaveWithStockAsync(toKg, 12m, apples.StockQuantity, "tester"));
        var noCount = await Error(() => repo.SaveWithStockAsync(toKg, null, apples.StockQuantity, "tester", unitChangeRecounted: true));
        var cherries = await Load("7700002");
        var toPiece = await Error(() => repo.SaveWithStockAsync(Clone(cherries, "Stück"), null, cherries.StockQuantity, "tester"));
        var applesAfter = await Load("7700001");
        var cherriesAfter = await Load("7700002");
        assert(
            silent.Contains("Einheit von \"Stück\" auf \"kg\"", StringComparison.Ordinal) && silent.Contains("Bestand 12", StringComparison.Ordinal) &&
            unconfirmed.Length > 0 && noCount.Length > 0 && toPiece.Contains("Mindestbestand 0,2", StringComparison.Ordinal) &&
            applesAfter.Unit == "Stück" && applesAfter.StockQuantity == 12m && applesAfter.MinStockQuantity == 3m &&
            cherriesAfter.Unit == "kg" && cherriesAfter.StockQuantity == 0.5m,
            "Unit change: 12 Stück never silently become 12 kg (nor 0,5 kg 0,5 Stück) - the save is refused and nothing changes unless the recount is confirmed with a counted stock");

        var recounted = Clone(apples, "kg");
        recounted.MinStockQuantity = 1m;
        await repo.SaveWithStockAsync(recounted, 2.4m, apples.StockQuantity, "tester", unitChangeRecounted: true);
        var kgApples = await Load("7700001");
        var pear = await Load("7700003");
        await repo.SaveWithStockAsync(Clone(pear, "kg"), null, 0m, "tester");
        assert(
            kgApples.Unit == "kg" && kgApples.StockQuantity == 2.4m && kgApples.MinStockQuantity == 1m &&
            (await Load("7700003")).Unit == "kg",
            "Unit change: a confirmed recount stores stock and Mindestbestand in the new unit; an article without any count changes unit freely");

        await File.WriteAllTextAsync(csv, Header + "Laden;Obst;Kirschen;;7700002;899;7;0;Stück;1;;;0\n");
        string importError;
        try { await management.ImportArticlesCsvAsync(csv, "tester"); importError = ""; }
        catch (InvalidDataException ex) { importError = ex.Message; }
        var cherriesCsv = await Load("7700002");
        await File.WriteAllTextAsync(csv,
            "GRUPPE;WARENGRUPPE;ARTIKEL;ARTIKELNUMMER;EAN;PREIS_CENT;UST\n" +
            "Laden;Obst;Kirschen;;7700002;949;7\n");
        await management.ImportArticlesCsvAsync(csv, "tester");
        var cherriesShort = await Load("7700002");
        await File.WriteAllTextAsync(csv, Header + "Laden;Obst;Kirschen;;7700002;949;7;0;Schale;1;4;1;0\n");
        await management.ImportArticlesCsvAsync(csv, "tester");
        var cherriesBowl = await Load("7700002");
        assert(
            importError.Contains("Zeile 2 (Kirschen): Einheit von \"kg\" auf \"Stück\"", StringComparison.Ordinal) &&
            cherriesCsv.Unit == "kg" && cherriesCsv.StockQuantity == 0.5m &&
            cherriesShort.Unit == "kg" && cherriesShort.BasePriceCents == 949 && cherriesShort.StockQuantity == 0.5m &&
            cherriesBowl.Unit == "Schale" && cherriesBowl.StockQuantity == 4m && cherriesBowl.MinStockQuantity == 1m,
            "Unit change (CSV): a row changing the unit without BESTAND/MINDESTBESTAND is refused with its line, a file without EINHEIT keeps kg instead of resetting it to Stück, a row with both counts is taken");

        var editor = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/ProductEditorWindow.cs"));
        var confirm = editor.IndexOf("await ConfirmUnitRecountAsync(", StringComparison.Ordinal);
        var save = editor.IndexOf("await _repo.SaveWithStockAsync(product,", StringComparison.Ordinal);
        assert(
            confirm > 0 && save > confirm &&
            editor.IndexOf("return;", confirm, StringComparison.Ordinal) < save &&
            editor.Contains("unitChangeRecounted:unitRecount", StringComparison.Ordinal) &&
            editor.Contains("stock != expectedStock || unitRecount ? stock : null", StringComparison.Ordinal),
            "Unit change (Artikel-Editor): the editor asks for the recount in the new unit before saving and then stores the entered stock as a count");
    }

    private static Product Clone(Product p, string unit) => new()
    {
        Id = p.Id, CategoryId = p.CategoryId, Name = p.Name, Sku = p.Sku, Barcode = p.Barcode,
        BasePriceCents = p.BasePriceCents, VatRate = p.VatRate, PfandCents = p.PfandCents, Unit = unit,
        ImagePath = p.ImagePath, IsActive = p.IsActive, SortOrder = p.SortOrder,
        MinStockQuantity = p.MinStockQuantity, PurchasePriceCents = p.PurchasePriceCents
    };

    private static string FindRepoFile(string relativePath)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relativePath);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        throw new FileNotFoundException(relativePath);
    }
}
