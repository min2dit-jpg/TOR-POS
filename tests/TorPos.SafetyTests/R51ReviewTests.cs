using TorPos.Core;
using TorPos.Infrastructure;

static class R51ReviewTests
{
    public static async Task Run(string root, Action<bool,string> assert, Func<Func<Task>,string,Task> reject)
    {
        var path = Path.Combine(root, "r51.db");
        var db = await SafetyDatabase.CreateCurrentAsync(path);
        var settings = new SettingsRepository(db);
        var repo = new ProductRepository(db);
        var starter = new ImbissStarterCatalogService(db);

        await settings.SaveManyAsync(new Dictionary<string,string> { ["business.mode"] = "IMBISS" });
        assert(await starter.EnsureAsync("IMBISS"), "R51 IMBISS starter/template repair runs once");
        assert(!await starter.EnsureAsync("IMBISS"), "R51 IMBISS template is idempotent after marker");

        var groups = await repo.GetGroupsAsync();
        var foodGroup = groups.Single(x => x.Name == "Speisen");
        var drinkGroup = groups.Single(x => x.Name == "Getränke");
        var categories = await repo.GetCategoriesAsync();
        foreach (var name in new[] { "Döner", "Burger", "Fingerfood", "Pizza" })
            assert(categories.Single(x => x.Name == name).GroupId == foodGroup.Id,
                $"R51 {name} is listed under Speisen");
        assert(categories.Single(x => x.Name == "Getränke").GroupId == drinkGroup.Id,
            "R51 Getränke Warengruppe is listed under Getränke group");
        assert(!categories.Any(x => x.Name is "Schnellwahl" or "Snacks"),
            "R51 KIOSK demo Warengruppen are hidden in IMBISS");

        var products = await repo.GetActiveProductsAsync();
        assert(products.Count(x => x.Name is "Döner" or "Big Döner" or "Dürüm Döner" or "Döner Box Klein" or "Döner Box Groß") == 5,
            "R51 Döner starter articles are active");
        var doenerCategoryId = categories.Single(x => x.Name == "Döner").Id;
        assert(products.Where(x => x.Name is "Döner" or "Big Döner" or "Dürüm Döner" or "Döner Box Klein" or "Döner Box Groß")
                       .All(x => x.CategoryId == doenerCategoryId),
            "R51 Döner starter articles are assigned to the Döner Warengruppe");

        // Article soft-delete: historical IDs remain in the DB but active catalog no longer exposes them.
        var testArticleId = await repo.SaveWithStockAsync(new Product
        {
            CategoryId = doenerCategoryId,
            Name = "R51 Delete Article",
            Barcode = "5100001",
            BasePriceCents = 100,
            VatRate = 7m
        }, 2, 0, "r51");
        await repo.DeactivateProductAsync(testArticleId, "admin");
        assert(!(await repo.GetActiveProductsAsync()).Any(x => x.Id == testArticleId),
            "R51 Artikel löschen soft-deactivates the product");

        // Warengruppe soft-delete cascades only to active master data below it.
        var tempCategoryId = await repo.SaveCategoryAsync(new Category(0, foodGroup.Id, "R51 Temp Category", 7m, 900));
        var tempCategoryArticleId = await repo.SaveWithStockAsync(new Product
        {
            CategoryId = tempCategoryId,
            Name = "R51 Category Child",
            Barcode = "5100002",
            BasePriceCents = 100,
            VatRate = 7m
        }, 1, 0, "r51");
        await repo.DeactivateCategoryAsync(tempCategoryId, "admin");
        assert(!(await repo.GetCategoriesAsync()).Any(x => x.Id == tempCategoryId),
            "R51 Warengruppe löschen soft-deactivates the category");
        assert(!(await repo.GetActiveProductsAsync()).Any(x => x.Id == tempCategoryArticleId),
            "R51 Warengruppe löschen hides assigned articles");

        // Gruppe soft-delete cascades to its Warengruppen/articles, while other groups remain.
        var tempGroupId = await repo.SaveGroupAsync(new ProductGroup(0, "R51 Temp Group", 999));
        var tempGroupCategoryId = await repo.SaveCategoryAsync(new Category(0, tempGroupId, "R51 Group Child", 19m, 10));
        var tempGroupArticleId = await repo.SaveWithStockAsync(new Product
        {
            CategoryId = tempGroupCategoryId,
            Name = "R51 Group Article",
            Barcode = "5100003",
            BasePriceCents = 100,
            VatRate = 19m
        }, 1, 0, "r51");
        await repo.DeactivateGroupAsync(tempGroupId, "admin");
        assert(!(await repo.GetGroupsAsync()).Any(x => x.Id == tempGroupId),
            "R51 Gruppe löschen soft-deactivates the group");
        assert(!(await repo.GetCategoriesAsync()).Any(x => x.Id == tempGroupCategoryId),
            "R51 Gruppe löschen hides child Warengruppen");
        assert(!(await repo.GetActiveProductsAsync()).Any(x => x.Id == tempGroupArticleId),
            "R51 Gruppe löschen hides child articles");
        assert((await repo.GetGroupsAsync()).Any(x => x.Id == foodGroup.Id),
            "R51 deleting one group leaves unrelated IMBISS groups intact");

        using var c = db.OpenConnection();
        using var audit = c.CreateCommand();
        audit.CommandText = "SELECT COUNT(*) FROM audit_log WHERE event_type IN ('MASTERDATA_PRODUCT_DEACTIVATED','MASTERDATA_CATEGORY_DEACTIVATED','MASTERDATA_GROUP_DEACTIVATED');";
        assert(Convert.ToInt64(audit.ExecuteScalar()) >= 3,
            "R51 master-data delete operations leave audit records");
    }
}
