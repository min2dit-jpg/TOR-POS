using TorPos.Core;
using TorPos.Infrastructure;
using TorPos.App;

static class R49ReviewTests
{
    public static async Task Run(string root, Action<bool,string> assert, Func<Func<Task>,string,Task> reject)
    {
        var path = Path.Combine(root, "r49.db");
        var db = await SafetyDatabase.CreateCurrentAsync(path);
        var repo = new ProductRepository(db);
        var settings = new SettingsRepository(db);
        var category = (await repo.GetCategoriesAsync()).First();

        var componentA = await repo.SaveWithStockAsync(new Product
        {
            CategoryId = category.Id,
            Name = "R49 Döner",
            Barcode = "490001",
            BasePriceCents = 700
        }, 20, 0, "r49");
        var componentB = await repo.SaveWithStockAsync(new Product
        {
            CategoryId = category.Id,
            Name = "R49 Pommes",
            Barcode = "490002",
            BasePriceCents = 300
        }, 30, 0, "r49");
        var menuId = await repo.SaveWithStockAsync(new Product
        {
            CategoryId = category.Id,
            Name = "R49 Menü",
            Barcode = "490003",
            BasePriceCents = 950
        }, 0, 0, "r49");
        await repo.ReplaceComboItemsAsync(menuId, new[]
        {
            new ProductComboItem(menuId, componentA, "R49 Döner", 1m, 0),
            new ProductComboItem(menuId, componentB, "R49 Pommes", 2m, 1)
        });
        var menu = (await repo.GetByIdAsync(menuId))!;
        assert(menu.IsCombo && menu.ComboItems.Count == 2, "R49 menu/combo components persist");
        assert(menu.ComboItems[1].ComponentProductId == componentB && menu.ComboItems[1].Quantity == 2m,
            "R49 combo component quantity is preserved");

        var parkedRepo = new ParkedReceiptRepository(db);
        var line = new CartLine
        {
            ProductId = menuId,
            ProductName = "R49 Menü",
            Barcode = "490003",
            Quantity = 1m,
            UnitPriceCents = 950,
            VatRate = category.VatRate
        };
        var order1 = await parkedRepo.ParkAsync(new[] { line }, 0, "r49", assignPickupNumber: true);
        var order2 = await parkedRepo.ParkAsync(new[] { line }, 0, "r49", assignPickupNumber: true);
        assert(order1.PickupNumber > 0 && order2.PickupNumber == order1.PickupNumber + 1,
            "R49 ORDER pickup numbers advance at order acceptance");
        var editedLine = new CartLine
        {
            ProductId = line.ProductId, ProductName = line.ProductName, Barcode = line.Barcode,
            Quantity = 2m, UnitPriceCents = line.UnitPriceCents, VatRate = line.VatRate, PfandCents = line.PfandCents
        };
        await parkedRepo.UpdateAsync(order1.Id, new[] { editedLine }, 0);
        var reopened = await parkedRepo.GetOpenByIdAsync(order1.Id);
        assert(reopened is not null && reopened.PickupNumber == order1.PickupNumber,
            "R49 editing an accepted order keeps its pickup number");

        var allSettings = await settings.LoadAllAsync();
        assert(allSettings.GetText("imbiss.pickup_number.mode", "") is "OFF" or "SALE" or "ORDER",
            "R49 pickup mode setting is valid");
        assert(!allSettings.ContainsKey("ui.language"), "a fresh till stores no interface language and therefore starts in German");
        assert(!allSettings.GetBool("device.kitchen_printer.enabled", true), "R49 kitchen printer is opt-in");

        // The examples are fiscal terms of art on purpose. A cashier word only
        // happens to be untranslated until someone translates it, so it would
        // turn this contract red the day the next slice lands; Z-Bericht and
        // DSFinV-K are German records by law and must never gain an entry.
        UiLanguage.Set("TR");
        assert(UiLanguage.T("KASSE") == "KASA" && UiLanguage.T("Z-Bericht") == "Z-Bericht" &&
               UiLanguage.T("DSFinV-K") == "DSFinV-K",
            "the Turkish till translates its own words and leaves the German fiscal terms alone");
        UiLanguage.Set("EN");
        assert(UiLanguage.T("KASSE") == "TILL" && UiLanguage.T("Z-Bericht") == "Z-Bericht" &&
               UiLanguage.T("DSFinV-K") == "DSFinV-K",
            "the English till translates its own words and leaves the German fiscal terms alone");
        UiLanguage.Set("DE");
        assert(UiLanguage.T("KASSE") == "KASSE" && UiLanguage.T("Z-Bericht") == "Z-Bericht",
            "R49 German operator UI remains the source text even for strings that have translations");

        var journal = new PrintJobJournal(Path.Combine(root, "r49-print-journal"));
        var kitchen = new KitchenPrintJob(DateTimeOffset.Now, 12, order1.ParkNumber, "tester",
            new[] { new KitchenPrintLine("Döner Menü", 1), new KitchenPrintLine("Pommes", 2, true) });
        var pickup = new PickupSlipPrintJob(DateTimeOffset.Now, 12, order1.ParkNumber, "TOR");
        await journal.SaveAsync(new PrintJobRecord("kitchen-r49", "QUEUED", "fake", null, null, "", Kitchen: kitchen));
        await journal.SaveAsync(new PrintJobRecord("pickup-r49", "QUEUED", "fake", null, null, "", PickupSlip: pickup));
        var queued = await journal.GetUncertainAsync();
        assert(queued.Any(x => x.Kitchen?.PickupNumber == 12) && queued.Any(x => x.PickupSlip?.PickupNumber == 12),
            "R49 kitchen and pickup print jobs survive durable queue serialization");
    }
}
