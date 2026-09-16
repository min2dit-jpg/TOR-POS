using TorPos.Core;
using TorPos.Infrastructure;

static class R50ReviewTests
{
    public static async Task Run(string root, Action<bool,string> assert, Func<Func<Task>,string,Task> reject)
    {
        var path = Path.Combine(root, "r50.db");
        var db = await SafetyDatabase.CreateCurrentAsync(path);
        var settings = new SettingsRepository(db);
        var repo = new ProductRepository(db);
        var starter = new ImbissStarterCatalogService(db);

        await settings.SaveManyAsync(new Dictionary<string,string> { ["business.mode"] = "KIOSK" });
        assert(!await starter.EnsureAsync("KIOSK"), "R50 IMBISS starter never runs for KIOSK");
        var kioskBefore = await repo.GetCategoriesAsync();
        assert(!kioskBefore.Any(x => x.Name is "Döner" or "Burger" or "Fingerfood" or "Pizza"),
            "R50 fresh KIOSK has no IMBISS food Warengruppen");

        await settings.SaveManyAsync(new Dictionary<string,string> { ["business.mode"] = "IMBISS" });
        assert(await starter.EnsureAsync("IMBISS"), "R50 IMBISS starter is applied once");
        assert(!await starter.EnsureAsync("IMBISS"), "R50 IMBISS starter is idempotent after marker");

        var cats = await repo.GetCategoriesAsync();
        foreach (var name in new[] { "Döner", "Burger", "Fingerfood", "Pizza", "Getränke" })
            assert(cats.Any(x => x.Name == name), $"R50 IMBISS Warengruppe exists: {name}");

        var products = await repo.GetActiveProductsAsync();
        foreach (var name in new[]
        {
            "Döner", "Big Döner", "Dürüm Döner", "Döner Box Klein", "Döner Box Groß",
            "Hamburger", "Cheeseburger", "Chickenburger", "Doppelburger",
            "Pommes Klein", "Pommes Groß", "Chicken Wings 6er",
            "Coca-Cola 0,33 l", "fritz-kola 0,33 l", "Ayran 0,33 l", "Wasser still 0,5 l",
            "Berliner Pilsner 0,5 l", "Schultheiss Pilsener 0,5 l"
        })
            assert(products.Any(x => x.Name == name), $"R50 starter article exists: {name}");

        var pizzas = products.Where(x => x.Name.StartsWith("Pizza ", StringComparison.Ordinal)).ToArray();
        assert(pizzas.Length >= 9, "R50 pizza starter contains the planned pizza selection");
        assert(pizzas.All(x => x.Variants.Count == 2 && x.Variants[0].Name == "Klein 26 cm" && x.Variants[1].Name == "Groß 32 cm"),
            "R50 starter pizzas have exactly two selectable sizes");

        var starterProducts = products.Where(x => x.Name != "Cola 0,33 l").ToArray();
        assert(starterProducts.Where(x => x.Name is "Big Döner" or "Hamburger" or "Pizza Sucuk").All(x => !string.IsNullOrWhiteSpace(x.Sku)),
            "R50 starter products receive automatic article numbers");

        await settings.SaveManyAsync(new Dictionary<string,string> { ["business.mode"] = "KIOSK" });
        var kioskAfter = await repo.GetActiveProductsAsync();
        assert(!kioskAfter.Any(x => x.Name is "Big Döner" or "Hamburger" or "Pizza Sucuk" or "Berliner Pilsner 0,5 l"),
            "R50 IMBISS starter articles are hidden in KIOSK");

        await settings.SaveManyAsync(new Dictionary<string,string> { ["business.mode"] = "IMBISS" });
        var doener = (await repo.GetActiveProductsAsync()).First(x => x.Name == "Döner");
        var line = new CartLine
        {
            ProductId = doener.Id, ProductName = doener.Name, Barcode = doener.Barcode,
            Quantity = 1, UnitPriceCents = doener.BasePriceCents, VatRate = doener.VatRate
        };
        var parked = new ParkedReceiptRepository(db);
        var trainingOrder = await parked.ParkAsync(new[] { line }, 0, "training", assignPickupNumber: true, training: true);
        var realOrder = await parked.ParkAsync(new[] { line }, 0, "real", assignPickupNumber: true, training: false);
        assert(trainingOrder.PickupNumber == 1 && realOrder.PickupNumber == 1,
            "R50 training and real ORDER pickup sequences are independent");
        assert(await parked.GetOpenCountAsync(training: true) == 1 && await parked.GetOpenCountAsync(training: false) == 1,
            "R50 training open orders are isolated from real open orders");
        assert((await parked.GetOpenAsync(training: true)).Single().Id == trainingOrder.Id,
            "R50 training order list returns only training orders");
    }
}
