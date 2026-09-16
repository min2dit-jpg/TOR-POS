using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

// R77: IMBISS-Küchenrouting nach Warengruppe/Station (Grill, Fritteuse, Getränke).
// Jede Warengruppe kann optional eine feste Küchenstation tragen; Bestellzeilen
// werden danach gruppiert und an den je Station konfigurierten Drucker geroutet,
// mit Rückfall auf den einen Standard-Küchendrucker.
public static class R77ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool,string> assert,
        Func<Func<Task>,string,Task> reject)
    {
        var dir = Path.Combine(root, "r77-kitchen-routing");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r77.db"));
        var products = new ProductRepository(db);
        var settings = new SettingsRepository(db);

        assert(KitchenStations.Normalize("grill") == KitchenStations.Grill,
            "R77 kitchen station normalization is case-insensitive");
        assert(KitchenStations.Normalize("  Fritteuse  ") == KitchenStations.Fritteuse,
            "R77 kitchen station normalization trims whitespace");
        assert(KitchenStations.Normalize("NICHT-EXISTENT") == KitchenStations.None,
            "R77 an unknown kitchen station falls back to the default printer, not a typo'd station");
        assert(KitchenStations.Normalize(null) == KitchenStations.None,
            "R77 a missing kitchen station normalizes to the default printer");

        var groupId = await products.SaveGroupAsync(new ProductGroup(0, "R77 GROUP", 0));
        var grillCategoryId = await products.SaveCategoryAsync(
            new Category(0, groupId, "R77 GRILL", 19m, 0, "#112233", KitchenStations.Grill));
        var plainCategoryId = await products.SaveCategoryAsync(
            new Category(0, groupId, "R77 STANDARD", 19m, 1, "#445566"));

        var reloaded = await products.GetCategoriesAsync();
        assert(reloaded.Single(x => x.Id == grillCategoryId).KitchenStation == KitchenStations.Grill,
            "R77 a saved kitchen station round-trips through the repository");
        assert(reloaded.Single(x => x.Id == plainCategoryId).KitchenStation == KitchenStations.None,
            "R77 a category without a chosen station stays on the default printer");

        var grillProductId = await products.SaveAsync(new Product
        {
            CategoryId = grillCategoryId,
            Name = "R77 Döner",
            BasePriceCents = 500,
            VatRate = 19m
        });
        var plainProductId = await products.SaveAsync(new Product
        {
            CategoryId = plainCategoryId,
            Name = "R77 Cola",
            BasePriceCents = 200,
            VatRate = 19m
        });

        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["device.kitchen_printer.enabled"] = "true",
            ["device.kitchen_printer.auto_print"] = "true",
            ["device.kitchen_printer.name"] = "STD-KUECHE",
            [KitchenStations.SettingsPrefix(KitchenStations.Grill) + ".enabled"] = "true",
            [KitchenStations.SettingsPrefix(KitchenStations.Grill) + ".name"] = "GRILL-DRUCKER",
        });

        var parked = new ParkedReceiptRepository(db);

        var mixedOrder = await parked.ParkAsync(
            new CartLine[]
            {
                new() { ProductId = grillProductId, ProductName = "R77 Döner", Quantity = 1, UnitPriceCents = 500 },
                new() { ProductId = plainProductId, ProductName = "R77 Cola", Quantity = 1, UnitPriceCents = 200 },
            },
            discountCents: 0,
            createdBy: "tester",
            assignPickupNumber: true,
            training: false,
            orderPrint: true);

        var outbox = new OrderPrintOutbox(db);
        var pendingMixed = await outbox.PendingAsync();
        var mixedKitchenJobs = pendingMixed.Where(x => x.Kitchen is not null).ToList();

        assert(mixedKitchenJobs.Count == 2,
            "R77 an order spanning two kitchen stations queues one print job per station");
        assert(mixedKitchenJobs.Any(x => x.Printer == "GRILL-DRUCKER" && x.Kitchen!.Lines.Any(l => l.Name.Contains("Döner"))),
            "R77 the Grill line is routed to the Grill station's configured printer");
        assert(mixedKitchenJobs.Any(x => x.Printer == "STD-KUECHE" && x.Kitchen!.Lines.Any(l => l.Name.Contains("Cola"))),
            "R77 a line without a station stays on the default kitchen printer");
        assert(mixedKitchenJobs.Single(x => x.Printer == "GRILL-DRUCKER").Kitchen!.Lines.All(l => l.Name.Contains("Döner")),
            "R77 the Grill printer's job never receives lines from another station");

        // Legacy/no-routing case: nothing has a station assigned, so exactly ONE
        // kitchen job is queued on the default printer, unchanged from before R77.
        var plainOnlyOrder = await parked.ParkAsync(
            new CartLine[]
            {
                new() { ProductId = plainProductId, ProductName = "R77 Cola", Quantity = 2, UnitPriceCents = 200 },
            },
            discountCents: 0,
            createdBy: "tester",
            assignPickupNumber: true,
            training: false,
            orderPrint: true);

        var pendingAfterPlain = await outbox.PendingAsync();
        var plainOnlyKitchenJobs = pendingAfterPlain
            .Where(x => x.Kitchen is not null && x.Kitchen!.ParkNumber == plainOnlyOrder.ParkNumber)
            .ToList();
        assert(plainOnlyKitchenJobs.Count == 1 && plainOnlyKitchenJobs[0].Printer == "STD-KUECHE",
            "R77 an order with no station-specific items still queues exactly one default-printer job");
    }
}
