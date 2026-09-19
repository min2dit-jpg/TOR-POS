using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

public static class R153ReviewTests
{
    public static async Task Run(string root, Action<bool,string> assert)
    {
        var food = new Product
        {
            Id = 15301,
            Name = "Döner",
            BasePriceCents = 700,
            VatRate = 7m,
            ImHausApplicable = true
        };
        var cola = new Product
        {
            Id = 15302,
            Name = "Cola",
            BasePriceCents = 300,
            VatRate = 19m,
            ImHausApplicable = false
        };
        var fanta = new Product
        {
            Id = 15303,
            Name = "Fanta",
            BasePriceCents = 300,
            VatRate = 19m,
            ImHausApplicable = false
        };
        var energy = new Product
        {
            Id = 15304,
            Name = "Energy",
            BasePriceCents = 400,
            VatRate = 19m,
            ImHausApplicable = false
        };
        var water = new Product
        {
            Id = 15305,
            Name = "Wasser",
            BasePriceCents = 250,
            VatRate = 19m,
            ImHausApplicable = false
        };
        var menu = new Product
        {
            Id = 15306,
            Name = "Döner Menü",
            BasePriceCents = 900,
            VatRate = 7m,
            ComboItems = new[]
            {
                new ProductComboItem(15306, food.Id, food.Name, 1m, 0),
                new ProductComboItem(15306, cola.Id, cola.Name, 1m, 1, "GETRÄNK"),
                new ProductComboItem(15306, fanta.Id, fanta.Name, 1m, 2, "GETRÄNK"),
                new ProductComboItem(15306, energy.Id, energy.Name, 1m, 3, "GETRÄNK")
            }
        };
        var catalog = new[] { food, cola, fanta, energy, water, menu };

        MenuComponentSnapshot[] Select(Product drink) =>
        [
            new(food.Id, food.Name, 1m, food.BasePriceCents, food.VatRate, food.ImHausApplicable, ""),
            new(drink.Id, drink.Name, 1m, drink.BasePriceCents, drink.VatRate, drink.ImHausApplicable, "GETRÄNK")
        ];

        var colaSelection = Select(cola);
        var energySelection = Select(energy);

        assert(
            MenuVatPolicy.EffectiveMenuPrice(menu, catalog, colaSelection) == 900,
            "R153 cheapest configured drink keeps the stored 9.00 EUR menu base price with no manual Aufpreis");

        assert(
            MenuVatPolicy.EffectiveMenuPrice(menu, catalog, energySelection) == 1000,
            "R153 a 1.00 EUR more expensive Artikel option automatically raises the menu by exactly its real Artikel price difference");

        var colaVat = MenuVatPolicy.Analyze(
            menu, catalog, imHaus:false, menuGrossCents:900, selectedComponents:colaSelection);
        assert(
            colaVat.IsValid &&
            colaVat.Allocations.Single(x => x.VatRate == 7m).GrossCents == 630 &&
            colaVat.Allocations.Single(x => x.VatRate == 19m).GrossCents == 270,
            "R153 Cola selection allocates the one menu price by the selected Artikel market values");

        var energyVat = MenuVatPolicy.Analyze(
            menu, catalog, imHaus:false, menuGrossCents:1000, selectedComponents:energySelection);
        assert(
            energyVat.IsValid &&
            energyVat.Allocations.Single(x => x.VatRate == 7m).GrossCents == 636 &&
            energyVat.Allocations.Single(x => x.VatRate == 19m).GrossCents == 364,
            "R153 Energy selection recalculates the VAT split from its own Artikel price while the receipt remains one menu price");

        var outsider = Select(water);
        var outsiderAnalysis = MenuVatPolicy.Analyze(
            menu, catalog, false, 900, outsider);
        assert(
            !outsiderAnalysis.IsValid && outsiderAnalysis.Message.Contains("gehört nicht"),
            "R153 an Artikel not configured in the choice group cannot be injected into menu stock or VAT");

        var missingChoice = MenuVatPolicy.Analyze(
            menu,
            catalog,
            false,
            900,
            new[] { colaSelection[0] });
        assert(
            !missingChoice.IsValid && missingChoice.Message.Contains("genau einen"),
            "R153 checkout refuses a choice menu when its required group has no selected Artikel");

        var validLine = new CartLine
        {
            ProductId = menu.Id,
            ProductName = menu.Name,
            Quantity = 1,
            UnitPriceCents = 900,
            VatRate = 7m,
            MenuComponents = colaSelection
        };
        var invalidLine = new CartLine
        {
            ProductId = menu.Id,
            ProductName = menu.Name,
            Quantity = 1,
            UnitPriceCents = 900,
            VatRate = 7m,
            MenuComponents = outsider
        };
        assert(
            MenuVatPolicy.BlockingMenus(new[] { validLine, invalidLine }, catalog, false).Count == 1,
            "R153 fiscal preflight validates every same-name menu line separately instead of checking only the first selection");

        var engine = new SaleEngine();
        engine.Add(menu, quantity:1, menuComponents:colaSelection, unitPriceOverrideCents:900);
        engine.Add(menu, quantity:1, menuComponents:colaSelection, unitPriceOverrideCents:900);
        engine.Add(menu, quantity:1, menuComponents:energySelection, unitPriceOverrideCents:1000);
        assert(
            engine.Cart.Count == 2 &&
            engine.Cart.Single(x => x.UnitPriceCents == 900).Quantity == 2m &&
            engine.Cart.Single(x => x.UnitPriceCents == 1000).Quantity == 1m,
            "R153 identical choices merge in the cart but a different selected Artikel remains its own menu line");

        var net = OrderBestellungDelta.Net(engine.Cart);
        assert(
            net.Count == 2 &&
            net.SelectMany(x => x.MenuComponents).Any(x => x.ProductId == cola.Id) &&
            net.SelectMany(x => x.MenuComponents).Any(x => x.ProductId == energy.Id),
            "R153 Bestellung delta identity preserves different hidden menu selections");

        var receiptLine = MenuVatPolicy.ApplyAllocations(
            new[] { engine.Cart.Single(x => x.UnitPriceCents == 900) },
            catalog,
            false).Single();
        var digital = DigitalReceiptDocument.From(
            new ReceiptPrintJob(
                153001,
                DateTimeOffset.Now,
                "TOR Test",
                "",
                "",
                "",
                "",
                "",
                "Bar",
                0,
                receiptLine.LineTotalCents,
                new[] { receiptLine }),
            DigitalReceiptDocument.PaymentsFor(PaymentMethod.Cash, receiptLine.LineTotalCents, 0));
        assert(
            digital.Lines.Count == 1 &&
            digital.Lines[0].Name == "Döner Menü" &&
            !digital.Lines[0].Name.Contains("Cola", StringComparison.OrdinalIgnoreCase) &&
            !digital.Lines[0].Name.Contains("Döner ·", StringComparison.OrdinalIgnoreCase),
            "R153 customer digital receipt shows only one Döner Menü line and never exposes its component Artikel");

        var dir = Path.Combine(root, "r153-menu-choice");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r153.db"));

        static bool HasColumn(SqliteConnection c, string table, string column)
        {
            using var q = c.CreateCommand();
            q.CommandText = $"PRAGMA table_info({table});";
            using var r = q.ExecuteReader();
            while (r.Read())
                if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        using (var c = db.OpenConnection())
        {
            assert(
                HasColumn(c, "product_combo_items", "choice_group") &&
                HasColumn(c, "sale_items", "menu_components_json") &&
                HasColumn(c, "parked_receipt_items", "menu_components_json"),
                "R153 schema migration 21 stores menu choice groups and immutable selected-component snapshots");
        }

        var products = new ProductRepository(db);
        var category = (await products.GetCategoriesAsync()).First();
        var foodId = await products.SaveWithStockAsync(new Product
        {
            CategoryId = category.Id,
            Name = "R153 Food",
            Barcode = "1530001",
            BasePriceCents = 700
        }, 20, 0, "r153");
        var colaId = await products.SaveWithStockAsync(new Product
        {
            CategoryId = category.Id,
            Name = "R153 Cola",
            Barcode = "1530002",
            BasePriceCents = 300
        }, 30, 0, "r153");
        var fantaId = await products.SaveWithStockAsync(new Product
        {
            CategoryId = category.Id,
            Name = "R153 Fanta",
            Barcode = "1530003",
            BasePriceCents = 300
        }, 40, 0, "r153");
        var menuId = await products.SaveWithStockAsync(new Product
        {
            CategoryId = category.Id,
            Name = "R153 Menü",
            Barcode = "1530004",
            BasePriceCents = 900
        }, 0, 0, "r153");

        await products.ReplaceComboItemsAsync(menuId, new[]
        {
            new ProductComboItem(menuId, foodId, "R153 Food", 1m, 0),
            new ProductComboItem(menuId, colaId, "R153 Cola", 1m, 1, "GETRÄNK"),
            new ProductComboItem(menuId, fantaId, "R153 Fanta", 1m, 2, "GETRÄNK")
        });
        var storedMenu = (await products.GetByIdAsync(menuId))!;
        assert(
            storedMenu.ComboItems.Count == 3 &&
            storedMenu.ComboItems.Count(x => x.ChoiceGroup == "GETRÄNK") == 2 &&
            storedMenu.ComboItems.Single(x => x.ComponentProductId == foodId).ChoiceGroup == "",
            "R153 Artikelverwaltung persists fixed components and GETRÄNK alternatives in the product recipe");

        var parked = new ParkedReceiptRepository(db);
        var parkedLine = new CartLine
        {
            ProductId = menuId,
            ProductName = "R153 Menü",
            Quantity = 1m,
            UnitPriceCents = 900,
            VatRate = category.VatRate,
            MenuComponents =
            [
                new(foodId, "R153 Food", 1m, 700, category.VatRate, true, ""),
                new(colaId, "R153 Cola", 1m, 300, category.VatRate, true, "GETRÄNK")
            ]
        };
        var order = await parked.ParkAsync(new[] { parkedLine }, 0, "r153");
        var reloaded = await parked.GetOpenByIdAsync(order.Id);
        assert(
            reloaded is not null &&
            reloaded.Lines.Single().MenuComponents.Length == 2 &&
            reloaded.Lines.Single().MenuComponents.Single(x => x.ChoiceGroup == "GETRÄNK").ProductId == colaId,
            "R153 parked/order data persists the exact selected Artikel instead of re-reading a later menu recipe");
    }
}
