using System.Linq;
using TorPos.Core;
using TorPos.Infrastructure;

// R97: user-requested follow-up to R95 - an explicit, visible per-Warengruppe
// switch for whether the Im-Haus/Außer-Haus VAT rule applies at all, instead
// of relying purely on the implicit "ImHausVat.Effective only ever touches a
// 7% rate" behavior. "Warengruppe is the VAT master" - the same pattern
// already used for VatRate itself: category_master_data.im_haus_applicable
// is authoritative and gets propagated to every product in that category on
// every category save, mirroring products.vat_rate exactly.
public static class R97ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert,
        Func<Func<Task>, string, Task> reject)
    {
        assert(
            ImHausVat.Effective(7m, imHaus: true, categoryApplies: false) == 7m,
            "R97 a Warengruppe with the rule switched off stays at 7% even while the global Im-Haus toggle is on");
        assert(
            ImHausVat.Effective(7m, imHaus: true, categoryApplies: true) == 19m,
            "R97 a Warengruppe with the rule switched on (the default) still elevates 7% to 19% exactly as R95 did");

        var dir = Path.Combine(root, "r97-category-im-haus");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r97.db"));
        var repo = new ProductRepository(db);
        var groupId = await repo.SaveGroupAsync(new ProductGroup(0, "R97 Gruppe", 0));

        var foodCategoryId = await repo.SaveCategoryAsync(new Category(0, groupId, "R97 Speisen", 7m, 0, "#17466A", "", ImHausApplicable: true));
        var drinkCategoryId = await repo.SaveCategoryAsync(new Category(0, groupId, "R97 Getränke", 19m, 1, "#17466A", "", ImHausApplicable: false));

        var categories = await repo.GetCategoriesAsync();
        assert(
            categories.Single(x => x.Id == foodCategoryId).ImHausApplicable &&
            !categories.Single(x => x.Id == drinkCategoryId).ImHausApplicable,
            "R97 GetCategoriesAsync round-trips each Warengruppe's own Im-Haus switch correctly");

        var foodProductId = await repo.SaveWithStockAsync(
            new Product { CategoryId = foodCategoryId, Name = "R97 Döner", BasePriceCents = 600 }, 0, 0, "tester");
        var opeoutProductId = await repo.SaveWithStockAsync(
            new Product { CategoryId = drinkCategoryId, Name = "R97 Cola", BasePriceCents = 300 }, 0, 0, "tester");

        var foodProduct = await repo.GetByIdAsync(foodProductId);
        var drinkProduct = await repo.GetByIdAsync(opeoutProductId);
        assert(
            foodProduct!.ImHausApplicable && !drinkProduct!.ImHausApplicable,
            "R97 a newly-saved product inherits its Warengruppe's Im-Haus switch, the same way it already inherits VatRate");

        // Flip the food category's switch off after the product already exists -
        // "Warengruppe is the VAT master" must propagate to existing products too.
        await repo.SaveCategoryAsync(new Category(foodCategoryId, groupId, "R97 Speisen", 7m, 0, "#17466A", "", ImHausApplicable: false));
        var foodProductAfterFlip = await repo.GetByIdAsync(foodProductId);
        assert(
            !foodProductAfterFlip!.ImHausApplicable,
            "R97 switching a Warengruppe's Im-Haus rule off retroactively updates every existing product in it, not just future ones");

        // End-to-end: the cart/checkout snapshot path respects the switched-off category.
        var engine = new SaleEngine();
        engine.Add(foodProductAfterFlip!);
        var snapshotOff = CheckoutSnapshot.CopyLines(engine.Cart, imHaus: true);
        assert(
            snapshotOff[0].VatRate == 7m,
            "R97 CaptureCheckout's CopyLines never elevates a line whose category has the rule switched off, even with the global toggle on");

        // Parked round-trip: the per-line ImHausApplicable snapshot survives Parken/reopen.
        var orders = new ParkedReceiptRepository(db);
        var parked = await orders.ParkAsync(engine.Cart, 0, "tester", imHaus: true);
        var reopened = await orders.GetOpenByIdAsync(parked.Id);
        assert(
            !reopened!.Lines.Single().ImHausApplicable,
            "R97 a parked line's Im-Haus-applicable snapshot (false, from the switched-off Warengruppe) survives being reloaded");
    }
}
