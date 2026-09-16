using TorPos.Core;
using TorPos.Infrastructure;

// R100: fixes a real, reproducible bug found while writing R99's tests.
// ux_products_barcode is a GLOBAL unique index (not scoped by
// edition_scope), but SafetyDatabase.CreateCurrentAsync - and the real
// production seeding path in Infrastructure.cs it mirrors - unconditionally
// seeds the SAME demo barcode (5449000000996, "Cola 0,33 l") under KIOSK
// scope on every freshly created database. UpsertProductAsync's own
// duplicate-lookup was scope-filtered, so importing a catalog between two
// independently-created TOR POS databases (a real scenario: migrating to a
// new till, syncing catalogs between stations) always threw
// "UNIQUE constraint failed: products.barcode" even though neither side's
// own scope-filtered product list showed a conflict. Fixed by making the
// lookup match the real constraint (global, not scope-filtered).
public static class R100ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "r100-cross-install-barcode-collision");
        Directory.CreateDirectory(dir);

        // Two INDEPENDENTLY freshly-seeded databases - exactly what two real
        // TOR POS installations look like before any manual product entry.
        // Both already carry the identical KIOSK-scope demo barcode.
        var sourceDb = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "source.db"));
        var sourceManagement = new BusinessManagementService(sourceDb, new SettingsRepository(sourceDb), new AuditLogRepository(sourceDb));
        var sourceRepo = new ProductRepository(sourceDb);

        // Give source one real, non-colliding product too, so the import
        // isn't a no-op and actually exercises the normal insert path
        // alongside the collision.
        var groupId = await sourceRepo.SaveGroupAsync(new ProductGroup(0, "R100 Gruppe", 0));
        var categoryId = await sourceRepo.SaveCategoryAsync(new Category(0, groupId, "R100 Kategorie", 19m, 0, "#123456", ""));
        await sourceRepo.SaveWithStockAsync(
            new Product { CategoryId = categoryId, Name = "R100 Produkt", Barcode = "100000001", BasePriceCents = 500 }, 0, 0, "tester");

        var targetDb = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "target.db"));
        var targetManagement = new BusinessManagementService(targetDb, new SettingsRepository(targetDb), new AuditLogRepository(targetDb));

        // No try/catch here on purpose: if the UNIQUE constraint bug ever
        // regresses, this should fail the whole suite loudly, not be
        // quietly swallowed as a passing "rejected" case - this operation
        // must succeed, not throw.
        await targetManagement.ImportArticlesFromDatabaseAsync(Path.Combine(dir, "source.db"), "tester");
        assert(
            true,
            "R100 importing between two independently-seeded databases (both carrying the same demo Cola barcode under KIOSK scope) no longer throws UNIQUE constraint failed: products.barcode");

        long barcodeRowCount;
        await using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir, "target.db")}"))
        {
            await raw.OpenAsync();
            await using var cmd = raw.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM products WHERE barcode='5449000000996';";
            barcodeRowCount = (long)(await cmd.ExecuteScalarAsync())!;
        }
        assert(
            barcodeRowCount == 1,
            $"R100 the target's own pre-existing demo row absorbs the import instead of a duplicate row being created (actual row count for the shared barcode: {barcodeRowCount})");

        long realProductCount;
        await using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir, "target.db")}"))
        {
            await raw.OpenAsync();
            await using var cmd = raw.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM products WHERE barcode='100000001';";
            realProductCount = (long)(await cmd.ExecuteScalarAsync())!;
        }
        assert(
            realProductCount == 1,
            "R100 the real, non-colliding product from source is still imported normally alongside the fixed collision");
    }
}
