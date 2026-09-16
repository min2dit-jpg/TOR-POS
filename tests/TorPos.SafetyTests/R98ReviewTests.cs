using System.Linq;
using TorPos.Core;
using TorPos.Infrastructure;

// R98: found while double-checking R97's readiness - ImportArticlesCsvAsync
// and ImportArticlesFromDatabaseAsync both funnel through the shared
// EnsureCategoryAsync/UpsertProductAsync helpers, which never read or wrote
// im_haus_applicable at all. A product inserted or updated through either
// import path would silently get the raw SQLite column default (true)
// instead of the Warengruppe's actual, possibly admin-switched-off setting
// - diverging from what the normal Warengruppe/Artikel editor
// (SaveWithStockAsync) already does correctly. Fixed by having
// EnsureCategoryAsync read the category's current im_haus_applicable back
// and UpsertProductAsync apply it on both INSERT and UPDATE - mirroring
// exactly how vat_rate itself already flows through import.
public static class R98ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert,
        Func<Func<Task>, string, Task> reject)
    {
        var dir = Path.Combine(root, "r98-import-im-haus");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r98.db"));
        var repo = new ProductRepository(db);
        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        var management = new BusinessManagementService(db, settings, audit);

        // An admin already excluded this 7%-Warengruppe from the Im-Haus rule
        // (e.g. Süßwaren) before any CSV import ever runs.
        var groupId = await repo.SaveGroupAsync(new ProductGroup(0, "R98 Gruppe", 0));
        var excludedCategoryId = await repo.SaveCategoryAsync(
            new Category(0, groupId, "R98 Süßwaren", 7m, 0, "#17466A", "", ImHausApplicable: false));

        var csvPath = Path.Combine(dir, "import.csv");
        await File.WriteAllTextAsync(csvPath,
            "Gruppe;Warengruppe;Name;Artikel-Nr;EAN;Preis;MwSt;Pfand;Einheit;Aktiv;Bestand;Mindestbestand;EK\n" +
            "R98 Gruppe;R98 Süßwaren;R98 Schokoriegel;;9800001;150;7;0;Stück;1;0;0;0\n" +
            "R98 Gruppe;R98 Neu;R98 Kaffee;;9800002;250;19;0;Stück;1;0;0;0\n");

        var imported = await management.ImportArticlesCsvAsync(csvPath, "tester");
        assert(imported == 2, "R98 both CSV rows import successfully");

        var excludedProduct = (await repo.GetActiveProductsAsync()).Single(x => x.Barcode == "9800001");
        assert(
            !excludedProduct.ImHausApplicable,
            "R98 a product imported via CSV into an already-excluded Warengruppe correctly inherits ImHausApplicable=false, not the raw column default");

        var newCategoryProduct = (await repo.GetActiveProductsAsync()).Single(x => x.Barcode == "9800002");
        assert(
            newCategoryProduct.ImHausApplicable,
            "R98 a brand-new Warengruppe created during CSV import defaults to Im-Haus-applicable=true, same as the normal editor default");

        // Re-import the same barcode with a changed price - exercises the
        // UPDATE branch of UpsertProductAsync, not just INSERT.
        await File.WriteAllTextAsync(csvPath,
            "Gruppe;Warengruppe;Name;Artikel-Nr;EAN;Preis;MwSt;Pfand;Einheit;Aktiv;Bestand;Mindestbestand;EK\n" +
            "R98 Gruppe;R98 Süßwaren;R98 Schokoriegel;;9800001;175;7;0;Stück;1;0;0;0\n");
        await management.ImportArticlesCsvAsync(csvPath, "tester");
        var updatedProduct = (await repo.GetActiveProductsAsync()).Single(x => x.Barcode == "9800001");
        assert(
            updatedProduct.BasePriceCents == 175 && !updatedProduct.ImHausApplicable,
            "R98 re-importing (UPDATE branch) an existing product keeps the Warengruppe's excluded Im-Haus setting, not just on first insert");
    }
}
