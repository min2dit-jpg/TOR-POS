using Microsoft.Data.Sqlite;

namespace TorPos.Infrastructure;

/// <summary>
/// R51 IMBISS starter assortment. Applied only after IMBISS is selected.
/// Existing customer prices/barcodes/stock and customer-selected product images
/// are preserved. R51 also repairs R50 category placement and adds bundled,
/// locally stored starter thumbnails.
/// </summary>
public sealed class ImbissStarterCatalogService
{
    private const string TemplateVersion = "R52-IMBISS-3";
    private readonly SqliteDatabase _db;

    public ImbissStarterCatalogService(SqliteDatabase db) => _db = db;

    public Task<bool> EnsureAsync(string edition, CancellationToken ct = default)
    {
        if (!string.Equals(edition?.Trim(), "IMBISS", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(false);

        return IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = await c.BeginTransactionAsync(ct);

            // The reserved tor-imbiss- prefix identifies bundled starter images only.
            await using(var images=c.CreateCommand()) {
                images.Transaction=(SqliteTransaction)tx;
                images.CommandText="UPDATE products SET image_path='' WHERE replace(image_path,char(92),'/') LIKE '%/tor-imbiss-%';";
                await images.ExecuteNonQueryAsync(ct);
            }
            await using (var marker = c.CreateCommand())
            {
                marker.Transaction = (SqliteTransaction)tx;
                marker.CommandText = "SELECT value FROM app_settings WHERE key='imbiss.catalog.template.version';";
                var existing = marker.ExecuteScalar() as string;
                if (string.Equals(existing, TemplateVersion, StringComparison.Ordinal))
                {
                    await tx.CommitAsync(ct);
                    return false;
                }
            }

            var foodGroup = await EnsureGroupAsync(c, (SqliteTransaction)tx, "Speisen", 10, ct);
            var drinkGroup = await EnsureGroupAsync(c, (SqliteTransaction)tx, "Getränke", 20, ct);

            var doener = await EnsureCategoryAsync(c, (SqliteTransaction)tx, foodGroup, "Döner", 10, 7m, "IMBISS", ct);
            var burger = await EnsureCategoryAsync(c, (SqliteTransaction)tx, foodGroup, "Burger", 20, 7m, "IMBISS", ct);
            var fingerfood = await EnsureCategoryAsync(c, (SqliteTransaction)tx, foodGroup, "Fingerfood", 30, 7m, "IMBISS", ct);
            var pizza = await EnsureCategoryAsync(c, (SqliteTransaction)tx, foodGroup, "Pizza", 40, 7m, "IMBISS", ct);
            // Getränke is shared as a Warengruppe, but the R51 starter articles are IMBISS-only.
            var drinks = await EnsureCategoryAsync(c, (SqliteTransaction)tx, drinkGroup, "Getränke", 50, 19m, "ALL", ct);

            await RenameLegacyDoenerAsync(c, (SqliteTransaction)tx, doener, ct);
            await HideLegacyKioskAssortmentAsync(c, (SqliteTransaction)tx, ct);

            var sort = 0;
            foreach (var item in new (string Name, long Price, string Image)[]
            {
                ("Döner", 800, "doener.png"),
                ("Big Döner", 950, "big-doener.png"),
                ("Dürüm Döner", 900, "dueruem-doener.png"),
                ("Döner Box Klein", 700, "doener-box-klein.png"),
                ("Döner Box Groß", 900, "doener-box-gross.png")
            })
                await EnsureProductAsync(c, (SqliteTransaction)tx, doener, item.Name, item.Price, 7m, 0, sort += 10, item.Image, ct);

            sort = 0;
            foreach (var item in new (string Name, long Price, string Image)[]
            {
                ("Hamburger", 700, "hamburger.png"),
                ("Cheeseburger", 750, "cheeseburger.png"),
                ("Chickenburger", 750, "chickenburger.png"),
                ("Doppelburger", 1050, "doppelburger.png"),
                ("Chili-Cheeseburger", 850, "chili-cheeseburger.png")
            })
                await EnsureProductAsync(c, (SqliteTransaction)tx, burger, item.Name, item.Price, 7m, 0, sort += 10, item.Image, ct);

            sort = 0;
            foreach (var item in new (string Name, long Price, string Image)[]
            {
                ("Pommes Klein", 350, "pommes-klein.png"),
                ("Pommes Groß", 500, "pommes-gross.png"),
                ("Chicken Nuggets 6er", 500, "nuggets-6.png"),
                ("Chicken Nuggets 9er", 650, "nuggets-9.png"),
                ("Chicken Wings 6er", 650, "wings-6.png"),
                ("Chicken Wings 9er", 850, "wings-9.png"),
                ("Mozzarella Sticks 6er", 600, "mozzarella-sticks-6.png"),
                ("Onion Rings 8er", 500, "onion-rings-8.png"),
                ("Chili Cheese Nuggets 6er", 600, "chili-cheese-nuggets-6.png")
            })
                await EnsureProductAsync(c, (SqliteTransaction)tx, fingerfood, item.Name, item.Price, 7m, 0, sort += 10, item.Image, ct);

            sort = 0;
            foreach (var item in new (string Name, long Small, long Large, string Image)[]
            {
                ("Pizza Margherita", 850, 1100, "pizza-margherita.png"),
                ("Pizza Salami", 950, 1250, "pizza-salami.png"),
                ("Pizza Funghi", 900, 1200, "pizza-funghi.png"),
                ("Pizza Tonno", 1050, 1350, "pizza-tonno.png"),
                ("Pizza Hawaii", 1000, 1300, "pizza-hawaii.png"),
                ("Pizza Sucuk", 1050, 1350, "pizza-sucuk.png"),
                ("Pizza Döner", 1100, 1400, "pizza-doener.png"),
                ("Pizza Vegetaria", 1000, 1300, "pizza-vegetaria.png"),
                ("Pizza Quattro Formaggi", 1050, 1350, "pizza-quattro-formaggi.png")
            })
            {
                var productId = await EnsureProductAsync(c, (SqliteTransaction)tx, pizza, item.Name, item.Small, 7m, 0, sort += 10, item.Image, ct);
                await EnsureTwoPizzaSizesAsync(c, (SqliteTransaction)tx, productId, item.Small, item.Large, ct);
            }

            sort = 0;
            // Starter prices are examples. Pfand remains 0 until the customer selects the actual packaging.
            foreach (var item in new (string Name, long Price, long Pfand, string Image)[]
            {
                ("Coca-Cola 0,33 l", 250, 0, "coca-cola-033.png"),
                ("Coca-Cola Zero 0,33 l", 250, 0, "coca-cola-zero-033.png"),
                ("Fanta 0,33 l", 250, 0, "fanta-033.png"),
                ("Sprite 0,33 l", 250, 0, "sprite-033.png"),
                ("fritz-kola 0,33 l", 300, 0, "fritz-kola-033.png"),
                ("fritz-kola ohne Zucker 0,33 l", 300, 0, "fritz-kola-zero-033.png"),
                ("fritz-limo Orange 0,33 l", 300, 0, "fritz-limo-orange-033.png"),
                ("fritz-limo Zitrone 0,33 l", 300, 0, "fritz-limo-zitrone-033.png"),
                ("fritz-mischmasch 0,33 l", 300, 0, "fritz-mischmasch-033.png"),
                ("Ayran 0,33 l", 200, 0, "ayran-033.png"),
                ("Coca-Cola 0,5 l", 350, 0, "coca-cola-05.png"),
                ("Coca-Cola Zero 0,5 l", 350, 0, "coca-cola-zero-05.png"),
                ("Fanta 0,5 l", 350, 0, "fanta-05.png"),
                ("Sprite 0,5 l", 350, 0, "sprite-05.png"),
                ("Wasser still 0,5 l", 250, 0, "wasser-still-05.png"),
                ("Wasser medium 0,5 l", 250, 0, "wasser-medium-05.png"),
                ("Berliner Pilsner 0,33 l", 300, 0, "berliner-pilsner-033.png"),
                ("Berliner Kindl 0,33 l", 300, 0, "berliner-kindl-033.png"),
                ("Beck's 0,33 l", 300, 0, "becks-033.png"),
                ("Heineken 0,33 l", 350, 0, "heineken-033.png"),
                ("Corona Extra 0,33 l", 400, 0, "corona-extra-033.png"),
                ("Berliner Pilsner 0,5 l", 350, 0, "berliner-pilsner-05.png"),
                ("Berliner Kindl Jubiläums Pilsener 0,5 l", 350, 0, "berliner-kindl-jubilaeum-05.png"),
                ("Schultheiss Pilsener 0,5 l", 350, 0, "schultheiss-05.png"),
                ("Beck's 0,5 l", 350, 0, "becks-05.png"),
                ("Sternburg Export 0,5 l", 300, 0, "sternburg-export-05.png"),
                ("Warsteiner 0,5 l", 350, 0, "warsteiner-05.png"),
                ("Krombacher Pils 0,5 l", 350, 0, "krombacher-05.png"),
                ("Augustiner Lagerbier Hell 0,5 l", 400, 0, "augustiner-hell-05.png")
            })
                await EnsureProductAsync(c, (SqliteTransaction)tx, drinks, item.Name, item.Price, 19m, item.Pfand, sort += 10, item.Image, ct);

            await EnsureExtraAsync(c, (SqliteTransaction)tx, doener, "Extra Käse", 100, 10, ct);
            await EnsureExtraAsync(c, (SqliteTransaction)tx, doener, "Extra Fleisch", 250, 20, ct);
            await EnsureExtraAsync(c, (SqliteTransaction)tx, doener, "Extra Soße", 50, 30, ct);
            await EnsureExtraAsync(c, (SqliteTransaction)tx, doener, "Extra Jalapeños", 100, 40, ct);

            await using (var marker = c.CreateCommand())
            {
                marker.Transaction = (SqliteTransaction)tx;
                marker.CommandText = "INSERT INTO app_settings(key,value) VALUES('imbiss.catalog.template.version',$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
                marker.Parameters.AddWithValue("$v", TemplateVersion);
                await marker.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return true;
        });
    }

    private static async Task<long> EnsureGroupAsync(SqliteConnection c, SqliteTransaction tx, string name, int sortOrder, CancellationToken ct)
    {
        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "INSERT OR IGNORE INTO product_groups(name,sort_order,is_active) VALUES($n,$s,1);";
            q.Parameters.AddWithValue("$n", name);
            q.Parameters.AddWithValue("$s", sortOrder);
            await q.ExecuteNonQueryAsync(ct);
        }
        await using var id = c.CreateCommand();
        id.Transaction = tx;
        id.CommandText = "SELECT id FROM product_groups WHERE name=$n COLLATE NOCASE LIMIT 1;";
        id.Parameters.AddWithValue("$n", name);
        return Convert.ToInt64(await id.ExecuteScalarAsync(ct));
    }

    private static async Task<long> EnsureCategoryAsync(SqliteConnection c, SqliteTransaction tx, long groupId,
        string name, int sortOrder, decimal vat, string scope, CancellationToken ct)
    {
        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "INSERT OR IGNORE INTO categories(name,sort_order,is_active,edition_scope) VALUES($name,$sort,1,$scope);";
            q.Parameters.AddWithValue("$name", name);
            q.Parameters.AddWithValue("$sort", sortOrder);
            q.Parameters.AddWithValue("$scope", scope);
            await q.ExecuteNonQueryAsync(ct);
        }

        long id;
        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT id FROM categories WHERE name=$name COLLATE NOCASE LIMIT 1;";
            q.Parameters.AddWithValue("$name", name);
            id = Convert.ToInt64(await q.ExecuteScalarAsync(ct));
        }

        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = scope == "ALL"
                ? "UPDATE categories SET sort_order=$sort,edition_scope='ALL' WHERE id=$id;"
                : "UPDATE categories SET sort_order=$sort,edition_scope='IMBISS' WHERE id=$id;";
            q.Parameters.AddWithValue("$sort", sortOrder);
            q.Parameters.AddWithValue("$id", id);
            await q.ExecuteNonQueryAsync(ct);
        }

        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = """
                INSERT INTO category_master_data(category_id,group_id,vat_rate)
                VALUES($c,$g,$v)
                ON CONFLICT(category_id) DO UPDATE SET group_id=excluded.group_id;
                """;
            q.Parameters.AddWithValue("$c", id);
            q.Parameters.AddWithValue("$g", groupId);
            q.Parameters.AddWithValue("$v", Convert.ToDouble(vat));
            await q.ExecuteNonQueryAsync(ct);
        }
        return id;
    }


    private static async Task HideLegacyKioskAssortmentAsync(SqliteConnection c, SqliteTransaction tx, CancellationToken ct)
    {
        await using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            UPDATE categories
            SET edition_scope='KIOSK'
            WHERE UPPER(name) IN ('HAUSHALTSWAREN','LEBENSMITTEL','TABAK','ZIGARETTEN','SCHNELLWAHL','SNACKS')
              AND is_active=1;

            UPDATE products
            SET edition_scope='KIOSK'
            WHERE category_id IN (
                SELECT id FROM categories
                WHERE UPPER(name) IN ('HAUSHALTSWAREN','LEBENSMITTEL','TABAK','ZIGARETTEN','SCHNELLWAHL','SNACKS')
            )
              AND is_active=1;
            """;
        await q.ExecuteNonQueryAsync(ct);
    }
    private static async Task RenameLegacyDoenerAsync(SqliteConnection c, SqliteTransaction tx, long categoryId, CancellationToken ct)
    {
        await using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            UPDATE products
            SET name='Dürüm Döner',edition_scope='IMBISS'
            WHERE category_id=$c AND name='Dürüm' COLLATE NOCASE
              AND NOT EXISTS(SELECT 1 FROM products WHERE name='Dürüm Döner' COLLATE NOCASE AND UPPER(COALESCE(edition_scope,'ALL'))='IMBISS');
            """;
        q.Parameters.AddWithValue("$c", categoryId);
        await q.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> EnsureProductAsync(SqliteConnection c, SqliteTransaction tx, long categoryId,
        string name, long priceCents, decimal vat, long pfandCents, int sortOrder, string assetFile, CancellationToken ct)
    {
        long? id = null;
        await using (var find = c.CreateCommand())
        {
            find.Transaction = tx;
            // R51 repair: R50 starter rows may exist under the wrong category.
            // Only IMBISS-scoped rows are moved; unrelated customer ALL/KIOSK rows are not touched.
            find.CommandText = """
                SELECT id
                FROM products
                WHERE name=$n COLLATE NOCASE
                  AND (category_id=$c OR UPPER(COALESCE(edition_scope,'ALL'))='IMBISS')
                ORDER BY CASE WHEN category_id=$c THEN 0 ELSE 1 END,id
                LIMIT 1;
                """;
            find.Parameters.AddWithValue("$c", categoryId);
            find.Parameters.AddWithValue("$n", name);
            var value = await find.ExecuteScalarAsync(ct);
            if (value is not null) id = Convert.ToInt64(value);
        }

        if (id is null)
        {
            var sku = await NextSkuAsync(c, tx, ct);
            await using var insert = c.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO products(
                  category_id,name,sku,barcode,base_price_cents,vat_rate,pfand_cents,
                  unit,image_path,is_active,sort_order,stock_quantity,min_stock_quantity,
                  purchase_price_cents,edition_scope)
                VALUES($c,$n,$sku,'',$price,$vat,$pfand,'Stück','',1,$sort,0,0,0,'IMBISS');
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$c", categoryId);
            insert.Parameters.AddWithValue("$n", name);
            insert.Parameters.AddWithValue("$sku", sku);
            insert.Parameters.AddWithValue("$price", priceCents);
            insert.Parameters.AddWithValue("$vat", Convert.ToDouble(vat));
            insert.Parameters.AddWithValue("$pfand", pfandCents);
            insert.Parameters.AddWithValue("$sort", sortOrder);
            id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
        }
        else
        {
            // Preserve customer-edited price/barcode/stock. Repair only template placement/scope/order.
            await using var update = c.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE products SET category_id=$c,edition_scope='IMBISS',sort_order=$sort WHERE id=$id;";
            update.Parameters.AddWithValue("$c", categoryId);
            update.Parameters.AddWithValue("$sort", sortOrder);
            update.Parameters.AddWithValue("$id", id.Value);
            await update.ExecuteNonQueryAsync(ct);
        }

        await EnsureStarterImageAsync(c, tx, id.Value, assetFile, ct);
        return id.Value;
    }

    private static Task EnsureStarterImageAsync(SqliteConnection c, SqliteTransaction tx, long productId, string assetFile, CancellationToken ct) => Task.CompletedTask;

    private static async Task<string> NextSkuAsync(SqliteConnection c, SqliteTransaction tx, CancellationToken ct)
    {
        await using var seq = c.CreateCommand();
        seq.Transaction = tx;
        seq.CommandText = """
            INSERT OR IGNORE INTO app_sequence(key,value) VALUES('article_number',99999);
            UPDATE app_sequence
            SET value=MAX(value,COALESCE((SELECT MAX(CAST(sku AS INTEGER)) FROM products WHERE TRIM(sku)<>'' AND sku NOT GLOB '*[^0-9]*'),99999))+1
            WHERE key='article_number';
            SELECT value FROM app_sequence WHERE key='article_number';
            """;
        var next = Convert.ToInt64(await seq.ExecuteScalarAsync(ct));
        return next.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task EnsureTwoPizzaSizesAsync(SqliteConnection c, SqliteTransaction tx, long productId,
        long smallCents, long largeCents, CancellationToken ct)
    {
        var names = new List<string>();
        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT name FROM product_variants WHERE product_id=$p AND is_active=1 ORDER BY sort_order,id;";
            q.Parameters.AddWithValue("$p", productId);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) names.Add(r.GetString(0));
        }

        // Existing variant names and prices belong to the customer, even if they
        // resemble an older template. Do not delete/reseed them on upgrade.
        if (names.Count > 0) return;

        foreach (var item in new[] { (Name: "Klein 26 cm", Price: smallCents, Sort: 0), (Name: "Groß 32 cm", Price: largeCents, Sort: 1) })
        {
            await using var q = c.CreateCommand();
            q.Transaction = tx;
            q.CommandText = "INSERT INTO product_variants(product_id,name,price_cents,sort_order,is_active) VALUES($p,$n,$price,$sort,1);";
            q.Parameters.AddWithValue("$p", productId);
            q.Parameters.AddWithValue("$n", item.Name);
            q.Parameters.AddWithValue("$price", item.Price);
            q.Parameters.AddWithValue("$sort", item.Sort);
            await q.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task EnsureExtraAsync(SqliteConnection c, SqliteTransaction tx, long categoryId,
        string name, long priceCents, int sortOrder, CancellationToken ct)
    {
        await using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            INSERT OR IGNORE INTO extras(category_id,name,price_cents,sort_order,is_active)
            VALUES($c,$n,$price,$sort,1);
            """;
        q.Parameters.AddWithValue("$c", categoryId);
        q.Parameters.AddWithValue("$n", name);
        q.Parameters.AddWithValue("$price", priceCents);
        q.Parameters.AddWithValue("$sort", sortOrder);
        await q.ExecuteNonQueryAsync(ct);
    }
}
