using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed record DuplicateArticleRow(
    string Kind,
    string Value,
    long ProductId,
    string Name,
    string Sku,
    string Barcode);

public sealed record InventoryArticleRow(
    long ProductId,
    string GroupName,
    string CategoryName,
    string Name,
    string Sku,
    string Barcode,
    decimal StockQuantity,
    decimal MinStockQuantity,
    string Unit,
    long PriceCents,
    long PurchasePriceCents,
    string LastInventoryAt)
{
    public bool IsLowStock => MinStockQuantity > 0m && StockQuantity <= MinStockQuantity;
    public long PurchaseStockValueCents => (long)Math.Round(StockQuantity * PurchasePriceCents, MidpointRounding.AwayFromZero);
    public long SalesStockValueCents => (long)Math.Round(StockQuantity * PriceCents, MidpointRounding.AwayFromZero);
}

public sealed record ZArchiveRow(
    long Id,
    long ZNumber,
    DateTimeOffset CreatedAt,
    DateTimeOffset PeriodFrom,
    DateTimeOffset PeriodTo,
    string OperatorName,
    int ReceiptCount,
    long GrossCents,
    long CashCents,
    long CardCents,
    string FiscalStatus,
    string SnapshotText);

public sealed record ReportDocument(
    string Title,
    IReadOnlyList<string> Lines,
    DateTimeOffset CreatedAt);

public sealed partial class BusinessManagementService
{
    private readonly SqliteDatabase _db;
    private readonly ISettingsRepository _settings;
    private readonly IAuditLog _audit;

    public BusinessManagementService(
        SqliteDatabase db,
        ISettingsRepository settings,
        IAuditLog audit)
    {
        _db = db;
        _settings = settings;
        _audit = audit;
    }
public async Task<IReadOnlyList<DuplicateArticleRow>> GetDuplicatesAsync(CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var result = new List<DuplicateArticleRow>();
        await using var c = _db.OpenConnection();
        foreach (var spec in new[]
        {
            (Kind: "EAN", Column: "barcode"),
            (Kind: "ARTIKELNUMMER", Column: "sku"),
            (Kind: "NAME", Column: "name")
        }

        )
        {
            await using var q = c.CreateCommand();
            q.CommandText = $"""
                SELECT p.id,p.name,p.sku,p.barcode,p.{spec.Column}
                FROM products p
                WHERE p.is_active=1
                  AND (UPPER(COALESCE(p.edition_scope,'ALL'))='ALL'
                       OR UPPER(p.edition_scope)=UPPER(COALESCE((SELECT value FROM app_settings WHERE key='business.mode'),'IMBISS')))
                  AND TRIM(p.{spec.Column})<>''
                  AND LOWER(TRIM(p.{spec.Column})) IN (
                    SELECT LOWER(TRIM({spec.Column}))
                    FROM products
                    WHERE is_active=1 AND TRIM({spec.Column})<>''
                      AND (UPPER(COALESCE(edition_scope,'ALL'))='ALL'
                           OR UPPER(edition_scope)=UPPER(COALESCE((SELECT value FROM app_settings WHERE key='business.mode'),'IMBISS')))
                    GROUP BY LOWER(TRIM({spec.Column}))
                    HAVING COUNT(*)>1
                  )
                ORDER BY LOWER(TRIM(p.{spec.Column})),p.id;
                """;
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                result.Add(new DuplicateArticleRow(spec.Kind, r.GetString(4), r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3)));
            }
        }

        return result;
    });
}public async Task<IReadOnlyList<InventoryArticleRow>> GetInventoryAsync(CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var result = new List<InventoryArticleRow>();
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT p.id,
                   COALESCE(g.name,'Standard'),
                   c.name,
                   p.name,p.sku,p.barcode,
                   COALESCE(p.stock_milli,CAST(ROUND(COALESCE(p.stock_quantity,0)*1000.0) AS INTEGER)),
                   COALESCE(p.min_stock_milli,CAST(ROUND(COALESCE(p.min_stock_quantity,0)*1000.0) AS INTEGER)),
                   p.unit,p.base_price_cents,COALESCE(p.purchase_price_cents,0),
                   COALESCE(p.last_inventory_at,'')
            FROM products p
            JOIN categories c ON c.id=p.category_id
            LEFT JOIN category_master_data m ON m.category_id=c.id
            LEFT JOIN product_groups g ON g.id=m.group_id
            WHERE p.is_active=1
              AND (UPPER(COALESCE(p.edition_scope,'ALL'))='ALL'
                   OR UPPER(p.edition_scope)=UPPER(COALESCE((SELECT value FROM app_settings WHERE key='business.mode'),'IMBISS')))
              AND (UPPER(COALESCE(c.edition_scope,'ALL'))='ALL'
                   OR UPPER(c.edition_scope)=UPPER(COALESCE((SELECT value FROM app_settings WHERE key='business.mode'),'IMBISS')))
            ORDER BY COALESCE(g.sort_order,0),g.name,c.sort_order,c.name,p.sort_order,p.name;
            """;
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            result.Add(new InventoryArticleRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), QuantityStorage.FromMilli(r.GetInt64(6)), QuantityStorage.FromMilli(r.GetInt64(7)), r.GetString(8), r.GetInt64(9), r.GetInt64(10), r.GetString(11)));
        }

        return result;
    });
}public Task<long> NextSimulationPickupAsync(bool training) => PickupSequence.NextSimulationAsync(_db,training);
public async Task SetInventoryAsync(long productId, decimal quantity, string actor, CancellationToken ct = default)
{
    await IoQueue.RunAsync(async () =>
    {
        if (quantity < 0)
            throw new InvalidOperationException("Bestand darf nicht negativ sein.");
        await using var c = _db.OpenConnection();
        using var tx=c.BeginTransaction();
        await using var q = c.CreateCommand();
        q.Transaction=tx;
        q.CommandText = """
            UPDATE products
            SET stock_quantity=$qty,
                stock_milli=$qtyMilli,
                last_inventory_at=$at
            WHERE id=$id;
            """;
        q.Parameters.AddWithValue("$qty", Convert.ToDouble(quantity));
        q.Parameters.AddWithValue("$qtyMilli", QuantityStorage.ToMilli(quantity));
        q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        q.Parameters.AddWithValue("$id", productId);
        await q.ExecuteNonQueryAsync(ct);
        await WriteInventoryAuditAsync(c,tx,productId,quantity,actor,ct);
        tx.Commit();
    });
}public async Task<bool> TrySetInventoryAsync(
    long productId,
    decimal expectedQuantity,
    decimal newQuantity,
    string actor,
    CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        if (newQuantity < 0)
            throw new InvalidOperationException("Bestand darf nicht negativ sein.");

        await using var c = _db.OpenConnection();
        using var tx=c.BeginTransaction();
        await using var q = c.CreateCommand();
        q.Transaction=tx;
        q.CommandText = """
            UPDATE products
            SET stock_quantity=$new,
                stock_milli=$newMilli,
                last_inventory_at=$at
            WHERE id=$id
              AND COALESCE(stock_milli,CAST(ROUND(COALESCE(stock_quantity,0)*1000.0) AS INTEGER))=$expectedMilli;
            """;
        q.Parameters.AddWithValue("$new", Convert.ToDouble(newQuantity));
        q.Parameters.AddWithValue("$newMilli", QuantityStorage.ToMilli(newQuantity));
        q.Parameters.AddWithValue("$expectedMilli", QuantityStorage.ToMilli(expectedQuantity));
        q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        q.Parameters.AddWithValue("$id", productId);
        var changed = await q.ExecuteNonQueryAsync(ct);
        if (changed == 0)
            return false;

        await WriteInventoryAuditAsync(c,tx,productId,newQuantity,actor,ct);
        tx.Commit();
        return true;
    });
}private static async Task WriteInventoryAuditAsync(SqliteConnection c,SqliteTransaction tx,long id,decimal quantity,string actor,CancellationToken ct) {
 using var q=c.CreateCommand();q.Transaction=tx;
 q.CommandText="INSERT INTO audit_log(created_at,actor,event_type,entity_type,entity_id,details) VALUES($at,$actor,'INVENTORY_COUNT_SET','PRODUCT',$id,$detail)";
 q.Parameters.AddWithValue("$at",DateTimeOffset.Now.ToString("O"));q.Parameters.AddWithValue("$actor",actor);q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$detail",quantity.ToString(CultureInfo.InvariantCulture));await q.ExecuteNonQueryAsync(ct);
}
public async Task<IReadOnlyList<InventoryArticleRow>> GetLowStockAsync(int limit = 50, CancellationToken ct = default)
{
    var rows = await GetInventoryAsync(ct);
    return rows.Where(x => x.IsLowStock)
        .OrderBy(x => x.StockQuantity - x.MinStockQuantity)
        .ThenBy(x => x.Name)
        .Take(Math.Clamp(limit, 1, 500))
        .ToArray();
}
public async Task<string> ExportArticlesCsvAsync(string targetPath, CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? Environment.CurrentDirectory);
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT COALESCE(g.name,'Standard'),c.name,p.name,p.sku,p.barcode,
                   p.base_price_cents,COALESCE(m.vat_rate,p.vat_rate),p.pfand_cents,
                   p.unit,p.is_active,
                   COALESCE(p.stock_milli,CAST(ROUND(COALESCE(p.stock_quantity,0)*1000.0) AS INTEGER)),
                   COALESCE(p.min_stock_milli,CAST(ROUND(COALESCE(p.min_stock_quantity,0)*1000.0) AS INTEGER)),
                   COALESCE(p.purchase_price_cents,0),
                   COALESCE(m.im_haus_applicable,1)
            FROM products p
            JOIN categories c ON c.id=p.category_id
            LEFT JOIN category_master_data m ON m.category_id=c.id
            LEFT JOIN product_groups g ON g.id=m.group_id
            WHERE (UPPER(COALESCE(p.edition_scope,'ALL'))='ALL'
                   OR UPPER(p.edition_scope)=UPPER(COALESCE((SELECT value FROM app_settings WHERE key='business.mode'),'IMBISS')))
              AND (UPPER(COALESCE(c.edition_scope,'ALL'))='ALL'
                   OR UPPER(c.edition_scope)=UPPER(COALESCE((SELECT value FROM app_settings WHERE key='business.mode'),'IMBISS')))
            ORDER BY g.name,c.name,p.name;
            """;
        await using var writer = new StreamWriter(targetPath, false, new UTF8Encoding(false));
        // R98 follow-up: IM_HAUS is the 14th column so an export/import
        // round-trip (backup/restore, moving a catalog to a fresh database)
        // carries each Warengruppe's R97 switch too, not just prices/VAT.
        await writer.WriteLineAsync("GRUPPE;WARENGRUPPE;ARTIKEL;ARTIKELNUMMER;EAN;PREIS_CENT;UST;PFAND_CENT;EINHEIT;AKTIV;BESTAND;MINDESTBESTAND;EINKAUFSPREIS_CENT;IM_HAUS");
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            await writer.WriteLineAsync(string.Join(";", new[] { Csv(r.GetString(0)), Csv(r.GetString(1)), Csv(r.GetString(2)), Csv(r.GetString(3)), Csv(r.GetString(4)), r.GetInt64(5).ToString(CultureInfo.InvariantCulture), Convert.ToDecimal(r.GetDouble(6)).ToString("0.##", CultureInfo.InvariantCulture), r.GetInt64(7).ToString(CultureInfo.InvariantCulture), Csv(r.GetString(8)), r.GetInt32(9).ToString(CultureInfo.InvariantCulture), QuantityStorage.FromMilli(r.GetInt64(10)).ToString("0.###", CultureInfo.InvariantCulture), QuantityStorage.FromMilli(r.GetInt64(11)).ToString("0.###", CultureInfo.InvariantCulture), r.GetInt64(12).ToString(CultureInfo.InvariantCulture), r.GetInt64(13) != 0 ? "1" : "0" }));
        }

        return targetPath;
    });
}public async Task<int> ImportArticlesCsvAsync(string sourcePath, string actor, CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Importdatei wurde nicht gefunden.", sourcePath);
        var lines = await File.ReadAllLinesAsync(sourcePath, ct);
        if (lines.Length < 2)
            return 0;
        var count = 0;
        await using var c = _db.OpenConnection();
        await using var tx = await c.BeginTransactionAsync(ct);
        for (var i = 1; i < lines.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;
            var cells = ParseCsvLine(lines[i]);
            if (cells.Count < 7)
                continue;
            var group = cells.ElementAtOrDefault(0)?.Trim() ?? "Standard";
            var category = cells.ElementAtOrDefault(1)?.Trim() ?? "Import";
            var name = cells.ElementAtOrDefault(2)?.Trim() ?? "";
            var sku = cells.ElementAtOrDefault(3)?.Trim() ?? "";
            var barcode = cells.ElementAtOrDefault(4)?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(name))
                continue;
            long.TryParse(cells.ElementAtOrDefault(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var price);
            decimal.TryParse(cells.ElementAtOrDefault(6), NumberStyles.Number, CultureInfo.InvariantCulture, out var vat);
            long.TryParse(cells.ElementAtOrDefault(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pfand);
            var unit = string.IsNullOrWhiteSpace(cells.ElementAtOrDefault(8)) ? "Stück" : cells[8].Trim();
            var active = !string.Equals(cells.ElementAtOrDefault(9)?.Trim(), "0", StringComparison.OrdinalIgnoreCase);
            decimal.TryParse(cells.ElementAtOrDefault(10), NumberStyles.Number, CultureInfo.InvariantCulture, out var stock);
            decimal.TryParse(cells.ElementAtOrDefault(11), NumberStyles.Number, CultureInfo.InvariantCulture, out var minStock);
            long.TryParse(cells.ElementAtOrDefault(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var purchasePrice);
            if (vat is not (7m or 19m))
                vat = 19m;
            // Optional 14th column (R98 follow-up). Older 13-column export
            // files simply don't have it - null means "don't touch an
            // already-existing Warengruppe's own Im-Haus choice".
            var imHausCell = cells.ElementAtOrDefault(13)?.Trim();
            bool? imHausFromCsv = string.IsNullOrEmpty(imHausCell) ? null : imHausCell != "0";
            var groupId = await EnsureGroupAsync(c, (SqliteTransaction)tx, group, ct);
            var (categoryId, categoryImHaus) = await EnsureCategoryAsync(c, (SqliteTransaction)tx, groupId, category, vat, ct, imHausFromCsv);
            await UpsertProductAsync(c, (SqliteTransaction)tx, categoryId, name, sku, barcode, Math.Max(0, price), vat, Math.Max(0, pfand), unit, active, Math.Max(0, stock), Math.Max(0, minStock), Math.Max(0, purchasePrice), ct, categoryImHaus);
            count++;
        }

        await tx.CommitAsync(ct);
        await _audit.WriteAsync(actor, "ARTICLE_IMPORT_CSV", "ARTICLE_MASTER_DATA", Path.GetFileName(sourcePath), $"rows={count}", ct);
        return count;
    });
}public async Task<int> ImportArticlesFromDatabaseAsync(string sourceDatabasePath, string actor, CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        if (!File.Exists(sourceDatabasePath))
            throw new FileNotFoundException("Quelldatenbank wurde nicht gefunden.", sourceDatabasePath);
        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sourceDatabasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();
        var rows = new List<(string Group, string Category, string Name, string Sku, string Barcode, long Price, decimal Vat, long Pfand, string Unit, bool Active, decimal Stock, decimal MinStock, long PurchasePrice, bool? ImHausApplicable)>();
        await using (var source = new SqliteConnection(sourceConnectionString))
        {
            await source.OpenAsync(ct);
            await using var q = source.CreateCommand();
            // R98 follow-up: im_haus_applicable (category_master_data) is a
            // brand-new R97 column, so it won't exist in ANY source database
            // that predates this release - i.e. virtually every real
            // customer database today. Guarded with the same
            // pragma_table_info EXISTS pattern already used for the R47
            // stock columns below, so importing from an older TOR database
            // never throws and falls into the legacy R42-R46 fallback tier
            // (which would silently drop real stock/purchase-price data
            // that source actually has). Returns NULL (not a default of 1)
            // when the source can't tell us its real value, so the target's
            // own already-existing Warengruppe setting is never overwritten
            // by a value we effectively made up.
            q.CommandText = """
                SELECT COALESCE(g.name,'Standard'),c.name,p.name,p.sku,p.barcode,
                       p.base_price_cents,COALESCE(m.vat_rate,p.vat_rate),p.pfand_cents,
                       p.unit,p.is_active,
                       CASE WHEN EXISTS(
                           SELECT 1 FROM pragma_table_info('products') WHERE name='stock_quantity'
                       ) THEN COALESCE(p.stock_quantity,0) ELSE 0 END,
                       CASE WHEN EXISTS(
                           SELECT 1 FROM pragma_table_info('products') WHERE name='min_stock_quantity'
                       ) THEN COALESCE(p.min_stock_quantity,0) ELSE 0 END,
                       CASE WHEN EXISTS(
                           SELECT 1 FROM pragma_table_info('products') WHERE name='purchase_price_cents'
                       ) THEN COALESCE(p.purchase_price_cents,0) ELSE 0 END,
                       CASE WHEN EXISTS(
                           SELECT 1 FROM pragma_table_info('category_master_data') WHERE name='im_haus_applicable'
                       ) THEN m.im_haus_applicable ELSE NULL END
                FROM products p
                JOIN categories c ON c.id=p.category_id
                LEFT JOIN category_master_data m ON m.category_id=c.id
                LEFT JOIN product_groups g ON g.id=m.group_id
                ORDER BY p.id;
                """;
            try
            {
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), Convert.ToDecimal(r.GetDouble(6)), r.GetInt64(7), r.GetString(8), r.GetInt32(9) != 0, Convert.ToDecimal(r.GetDouble(10)), Convert.ToDecimal(r.GetDouble(11)), r.GetInt64(12), r.IsDBNull(13) ? null : r.GetInt64(13) != 0));
                }
            }
            catch (SqliteException)
            {
                // R42-R46 databases have stock_quantity but not the new R47 columns. Preserve that stock.
                try
                {
                    q.CommandText = """
                        SELECT 'Standard',c.name,p.name,p.sku,p.barcode,
                               p.base_price_cents,p.vat_rate,p.pfand_cents,
                               p.unit,p.is_active,COALESCE(p.stock_quantity,0),0,0
                        FROM products p
                        JOIN categories c ON c.id=p.category_id
                        ORDER BY p.id;
                        """;
                    await using var r = await q.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), Convert.ToDecimal(r.GetDouble(6)), r.GetInt64(7), r.GetString(8), r.GetInt32(9) != 0, Convert.ToDecimal(r.GetDouble(10)), 0m, 0L, null));
                    }
                }
                catch (SqliteException)
                {
                    q.CommandText = """
                        SELECT 'Standard',c.name,p.name,p.sku,p.barcode,
                               p.base_price_cents,p.vat_rate,p.pfand_cents,
                               p.unit,p.is_active,0,0,0
                        FROM products p
                        JOIN categories c ON c.id=p.category_id
                        ORDER BY p.id;
                        """;
                    await using var r = await q.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), Convert.ToDecimal(r.GetDouble(6)), r.GetInt64(7), r.GetString(8), r.GetInt32(9) != 0, 0m, 0m, 0L, null));
                    }
                }
            }
        }

        await using var c = _db.OpenConnection();
        await using var tx = await c.BeginTransactionAsync(ct);
        foreach (var row in rows)
        {
            var groupId = await EnsureGroupAsync(c, (SqliteTransaction)tx, row.Group, ct);
            var (categoryId, categoryImHaus) = await EnsureCategoryAsync(c, (SqliteTransaction)tx, groupId, row.Category, row.Vat, ct, row.ImHausApplicable);
            await UpsertProductAsync(c, (SqliteTransaction)tx, categoryId, row.Name, row.Sku, row.Barcode, row.Price, row.Vat, row.Pfand, row.Unit, row.Active, row.Stock, row.MinStock, row.PurchasePrice, ct, categoryImHaus);
        }

        await tx.CommitAsync(ct);
        await _audit.WriteAsync(actor, "ARTICLE_IMPORT_DATABASE", "ARTICLE_MASTER_DATA", Path.GetFileName(sourceDatabasePath), $"rows={rows.Count}", ct);
        return rows.Count;
    });
}public async Task<ReportDocument> BuildXReportAsync(CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var period = await GetOpenPeriodAsync(ct);
        return BuildTurnoverDocument(
            "X-BERICHT",
            period,
            extra: "Zwischenbericht - kein Tagesabschluss, Z-Zähler bleibt unverändert.");
    });
}public async Task<ZArchiveRow> CreateZArchiveAsync(string actor, string fiscalStatus, CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var period = await GetOpenPeriodAsync(ct);
        var now = DateTimeOffset.Now;
        await using var c = _db.OpenConnection();
        await using var tx = await c.BeginTransactionAsync(ct);
        long zNumber;
        await using (var seq = c.CreateCommand())
        {
            seq.Transaction = (SqliteTransaction)tx;
            seq.CommandText = """
                UPDATE app_sequence SET value=value+1 WHERE key='z_report';
                SELECT value FROM app_sequence WHERE key='z_report';
                """;
            zNumber = Convert.ToInt64(await seq.ExecuteScalarAsync(ct));
        }

        var doc = BuildTurnoverDocument(
            $"Z-BERICHT {zNumber:000000}",
            period with { To = now },
            extra: $"Bediener: {actor} | Fiskalstatus: {fiscalStatus}");
        var snapshot = string.Join("\n", doc.Lines);

        // R132: the Stammdaten this closing was recorded under (DSFinV-K 3.2).
        var masterData = DsfinvkMasterDataRules.Serialize(
            await DsfinvkMasterDataStore.CurrentAsync(c, ct, (SqliteTransaction)tx));
        long id;
        await using (var q = c.CreateCommand())
        {
            q.Transaction = (SqliteTransaction)tx;
            q.CommandText = """
                INSERT INTO z_report_archive(
                  z_number,created_at,period_from,period_to,operator_name,
                  receipt_count,gross_cents,cash_cents,card_cents,
                  vat7_gross_cents,vat19_gross_cents,fiscal_status,snapshot_text,
                  list_gross_cents,promotion_discount_cents,manual_discount_cents,
                  storno_cents,return_cents,
                  vat7_net_cents,vat7_tax_cents,
                  vat19_net_cents,vat19_tax_cents,
                  master_data)
                VALUES(
                  $z,$created,$from,$to,$operator,
                  $count,$gross,$cash,$card,
                  $v7,$v19,$fiscal,$snapshot,
                  $list,$promotion,$manual,
                  $storno,$return,
                  $v7net,$v7tax,
                  $v19net,$v19tax,
                  $master);
                SELECT last_insert_rowid();
                """;
            q.Parameters.AddWithValue("$z", zNumber);
            q.Parameters.AddWithValue("$created", now.ToString("O"));
            q.Parameters.AddWithValue("$from", period.From.ToString("O"));
            q.Parameters.AddWithValue("$to", now.ToString("O"));
            q.Parameters.AddWithValue("$operator", actor ?? "");
            q.Parameters.AddWithValue("$count", period.ReceiptCount);
            q.Parameters.AddWithValue("$gross", period.GrossCents);
            q.Parameters.AddWithValue("$cash", period.CashCents);
            q.Parameters.AddWithValue("$card", period.CardCents);
            var vat7 = period.Taxes.FirstOrDefault(x => Math.Abs(x.Rate - 7m) < 0.01m);
            var vat19 = period.Taxes.FirstOrDefault(x => Math.Abs(x.Rate - 19m) < 0.01m);

            q.Parameters.AddWithValue("$v7", vat7?.GrossCents ?? 0);
            q.Parameters.AddWithValue("$v19", vat19?.GrossCents ?? 0);
            q.Parameters.AddWithValue("$list", period.ListGrossCents);
            q.Parameters.AddWithValue("$promotion", period.PromotionDiscountCents);
            q.Parameters.AddWithValue("$manual", period.ManualDiscountCents);
            q.Parameters.AddWithValue("$storno", period.StornoCents);
            q.Parameters.AddWithValue("$return", period.ReturnCents);
            q.Parameters.AddWithValue("$v7net", vat7?.NetCents ?? 0);
            q.Parameters.AddWithValue("$v7tax", vat7?.TaxCents ?? 0);
            q.Parameters.AddWithValue("$v19net", vat19?.NetCents ?? 0);
            q.Parameters.AddWithValue("$v19tax", vat19?.TaxCents ?? 0);
            q.Parameters.AddWithValue("$fiscal", fiscalStatus ?? "");
            q.Parameters.AddWithValue("$snapshot", snapshot);
            q.Parameters.AddWithValue("$master", masterData);
            id = Convert.ToInt64(await q.ExecuteScalarAsync(ct));
        }

        // R132: from here on the new period is recorded under the running
        // software version.
        await using (var version = c.CreateCommand())
        {
            version.Transaction = (SqliteTransaction)tx;
            version.CommandText = """
                INSERT INTO app_settings(key,value) VALUES($key,$value)
                ON CONFLICT(key) DO UPDATE SET value=excluded.value;
                """;
            version.Parameters.AddWithValue("$key", DsfinvkMasterDataRules.SoftwareVersionKey);
            version.Parameters.AddWithValue("$value", DsfinvkMasterDataStore.RunningSoftwareVersion);
            await version.ExecuteNonQueryAsync(ct);
        }

        await using (var closing = c.CreateCommand())
        {
            closing.Transaction = (SqliteTransaction)tx;
            closing.CommandText = """
                INSERT INTO daily_closings(closed_at,operator_name,close_type)
                VALUES($closed,$operator,'Z_REPORT');
                """;
            closing.Parameters.AddWithValue("$closed", now.ToString("O"));
            closing.Parameters.AddWithValue("$operator", actor ?? "");
            await closing.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        var safeActor = actor ?? "";
        var safeFiscalStatus = fiscalStatus ?? "";
        await _audit.WriteAsync(
            safeActor,
            "Z_REPORT_ARCHIVED",
            "Z_REPORT",
            zNumber.ToString(),
            $"receipts={period.ReceiptCount}; list_gross_cents={period.ListGrossCents}; " +
            $"promotion_discount_cents={period.PromotionDiscountCents}; manual_discount_cents={period.ManualDiscountCents}; " +
            $"gross_cents={period.GrossCents}; storno_cents={period.StornoCents}; return_cents={period.ReturnCents}; fiscal={safeFiscalStatus}",
            ct);
        return new ZArchiveRow(id, zNumber, now, period.From, now, safeActor, period.ReceiptCount, period.GrossCents, period.CashCents, period.CardCents, safeFiscalStatus, snapshot);
    });
}public async Task<IReadOnlyList<ZArchiveRow>> GetZArchiveAsync(CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var result = new List<ZArchiveRow>();
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT id,z_number,created_at,period_from,period_to,operator_name,
                   receipt_count,gross_cents,cash_cents,card_cents,fiscal_status,snapshot_text
            FROM z_report_archive
            ORDER BY z_number DESC;
            """;
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            result.Add(new ZArchiveRow(r.GetInt64(0), r.GetInt64(1), DateTimeOffset.Parse(r.GetString(2)), DateTimeOffset.Parse(r.GetString(3)), DateTimeOffset.Parse(r.GetString(4)), r.GetString(5), r.GetInt32(6), r.GetInt64(7), r.GetInt64(8), r.GetInt64(9), r.GetString(10), r.GetString(11)));
        }

        return result;
    });
}
    public ReportDocument ZArchiveToDocument(ZArchiveRow row) =>
        new($"Z-BERICHT {row.ZNumber:000000}",
            row.SnapshotText.Split('\n', StringSplitOptions.None),
            row.CreatedAt);
public async Task<ReportDocument> BuildCashJournalAsync(CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        // Range bounds on the generated created_at_utc column instead of
        // substr(created_at,1,10)=$date: identical matching outside the rare
        // DST transition day, correct instead of ambiguous during it, and
        // usable with an index instead of forcing a full table scan.
        var today = DateTime.Today;
        var dayStart = ToUtcColumnText(new DateTimeOffset(today, TimeZoneInfo.Local.GetUtcOffset(today)));
        var dayEnd = ToUtcColumnText(new DateTimeOffset(today.AddDays(1), TimeZoneInfo.Local.GetUtcOffset(today.AddDays(1))));
        var lines = new List<string>
        {
            $"Datum: {DateTime.Now:dd.MM.yyyy}",
            "",
            "BONS"
        };
        await using var c = _db.OpenConnection();
        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT s.receipt_number,s.created_at,s.payment_method,s.total_cents,
                       COALESCE(o.operator_name,'')
                FROM sales s
                LEFT JOIN sale_operators o ON o.sale_id=s.id
                WHERE s.created_at_utc >= $from AND s.created_at_utc < $to
                ORDER BY s.receipt_number;
                """;
            q.Parameters.AddWithValue("$from", dayStart);
            q.Parameters.AddWithValue("$to", dayEnd);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                lines.Add($"Bon {r.GetInt64(0):000000} | {DateTimeOffset.Parse(r.GetString(1)):HH:mm} | " + $"{r.GetString(2)} | {Money(r.GetInt64(3))} | {r.GetString(4)}");
            }
        }

        lines.Add("");
        lines.Add("KASSENBEWEGUNGEN");
        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT created_at,movement_type,amount_cents,reason,actor
                FROM cash_movements
                WHERE created_at_utc >= $from AND created_at_utc < $to
                ORDER BY id;
                """;
            q.Parameters.AddWithValue("$from", dayStart);
            q.Parameters.AddWithValue("$to", dayEnd);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                lines.Add($"{DateTimeOffset.Parse(r.GetString(0)):HH:mm} | {r.GetString(1)} | " + $"{Money(r.GetInt64(2))} | {r.GetString(3)} | {r.GetString(4)}");
            }
        }

        return new ReportDocument("KASSENJOURNAL", lines, DateTimeOffset.Now);
    });
}public async Task<ReportDocument> BuildInventoryReportAsync(CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var rows = await GetInventoryAsync(ct);
        var purchaseValue = rows.Sum(x => x.PurchaseStockValueCents);
        var salesValue = rows.Sum(x => x.SalesStockValueCents);
        var lowCount = rows.Count(x => x.IsLowStock);
        var lines = new List<string>
        {
            $"Stand: {DateTime.Now:dd.MM.yyyy HH:mm}",
            $"Aktive Artikel: {rows.Count} · Mindestbestand-Warnungen: {lowCount}",
            $"Warenwert Einkauf: {Money(purchaseValue)} · Warenwert Verkauf: {Money(salesValue)}",
            "",
            "Artikel | EAN | Bestand | Min. | VK | EK | Warenwert EK | Gruppe / Warengruppe"
        };
        lines.AddRange(rows.Select(x =>
            GermanFormat.Line($"{(x.IsLowStock ? "! " : "")}{x.Name} | {x.Barcode} | {x.StockQuantity:0.###} {x.Unit} | {x.MinStockQuantity:0.###} | {Money(x.PriceCents)} | {Money(x.PurchasePriceCents)} | {Money(x.PurchaseStockValueCents)} | {x.GroupName} / {x.CategoryName}")));
        return new ReportDocument("WARENBESTAND", lines, DateTimeOffset.Now);
    });
}// R91: same bug family as R88/R90, found by auditing sibling report
// queries - summed every sales row for the period regardless of
// transaction_type, so a BON STORNO/Teilretoure's total_cents was added
// on top of the period's Umsatz/Bar/Karte instead of excluded.
public async Task<ReportDocument> BuildTurnoverSummaryAsync(CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var today = DateTime.Today;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var lines = new List<string>();
        await using var c = _db.OpenConnection();
        foreach (var p in new[]
        {
            ("HEUTE", today),
            ("DIESE WOCHE", weekStart),
            ("DIESER MONAT", monthStart)
        }

        )
        {
            await using var q = c.CreateCommand();
            // R101: same cash/card split + historical-row fallback as
            // GetPeriodSummaryAsync above.
            q.CommandText = """
                SELECT COUNT(*),COALESCE(SUM(total_cents),0),
                       COALESCE(SUM(CASE WHEN cash_portion_cents<>0 OR card_portion_cents<>0 THEN cash_portion_cents
                                         WHEN payment_method='CASH' THEN total_cents ELSE 0 END),0),
                       COALESCE(SUM(CASE WHEN cash_portion_cents<>0 OR card_portion_cents<>0 THEN card_portion_cents
                                         WHEN payment_method='CARD' THEN total_cents ELSE 0 END),0)
                FROM sales
                WHERE COALESCE(transaction_type,'SALE')='SALE'
                  AND created_at >= $from;
                """;
            q.Parameters.AddWithValue("$from", new DateTimeOffset(p.Item2, TimeZoneInfo.Local.GetUtcOffset(p.Item2)).ToString("O"));
            await using var r = await q.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            lines.Add($"{p.Item1}: {r.GetInt64(0)} Bons | Umsatz {Money(r.GetInt64(1))} | Bar {Money(r.GetInt64(2))} | Karte {Money(r.GetInt64(3))}");
        }

        return new ReportDocument("UMSATZBERICHTE", lines, DateTimeOffset.Now);
    });
}public async Task<ReportDocument> BuildMonthlyTurnoverAsync(CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var lines = new List<string>
        {
            "Monat | Bons | Umsatz"
        };
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        // The report only ever shows the newest 24 months, so bounding the
        // scan to a 25-month-back cutoff (indexed range on created_at) cannot
        // change the output, but skips scanning older history entirely.
        var cutoffMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-25);
        q.CommandText = """
            SELECT substr(created_at,1,7) AS ym,COUNT(*),COALESCE(SUM(total_cents),0)
            FROM sales
            WHERE COALESCE(transaction_type,'SALE')='SALE'
              AND created_at_utc >= $cutoff
            GROUP BY ym
            ORDER BY ym DESC
            LIMIT 24;
            """;
        q.Parameters.AddWithValue(
            "$cutoff",
            ToUtcColumnText(new DateTimeOffset(cutoffMonth, TimeZoneInfo.Local.GetUtcOffset(cutoffMonth))));
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            lines.Add($"{r.GetString(0)} | {r.GetInt64(1)} | {Money(r.GetInt64(2))}");
        return new ReportDocument("MONATSUMSATZ", lines, DateTimeOffset.Now);
    });
}
// R91: sale_items rows exist for a BON STORNO/Teilretoure too (they mirror
// the reversed lines) - without the join+filter below, a returned
// product's quantity/revenue was counted a second time on top of its
// original sale instead of excluded.
public async Task<ReportDocument> BuildSalesStatisticsAsync(CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var lines = new List<string>
        {
            "Artikel | Menge | Umsatz"
        };
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT i.product_name,
                   COALESCE(SUM(CASE WHEN COALESCE(i.quantity_milli,0)<>0 THEN i.quantity_milli ELSE CAST(ROUND(i.quantity*1000.0) AS INTEGER) END),0),
                   COALESCE(SUM(i.line_total_cents),0)
            FROM sale_items i
            JOIN sales s ON s.id=i.sale_id
            WHERE COALESCE(s.transaction_type,'SALE')='SALE'
            GROUP BY i.product_name
            ORDER BY SUM(i.line_total_cents) DESC
            LIMIT 100;
            """;
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            lines.Add(GermanFormat.Line($"{r.GetString(0)} | {QuantityStorage.FromMilli(r.GetInt64(1)):0.###} | {Money(r.GetInt64(2))}"));
        return new ReportDocument("VERKAUFSSTATISTIK", lines, DateTimeOffset.Now);
    });
}// R90: previously summed EVERY sales row regardless of transaction_type,
// so a BON STORNO/Teilretoure's total_cents was silently ADDED to the
// operator's Umsatz/Bar/Karte instead of being excluded or netted - the
// exact opposite of what a cash-reconciliation report needs. The real
// X-/Z-Report (GetPeriodSummaryAsync) already establishes the correct
// convention: Bar/Karte/Bons come only from transaction_type='SALE' rows.
// Now mirrors that, and adds a second section for STORNO/RETURN, which is
// only attributable to whoever actually processed it since R90 also made
// RecordStornoAsync/RecordReturnAsync write a sale_operators row for the
// reversal itself (previously only audit_log's free-text actor knew who).
public async Task<ReportDocument> BuildOperatorSettlementAsync(CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var today = DateTime.Today;
        var dayStart = ToUtcColumnText(new DateTimeOffset(today, TimeZoneInfo.Local.GetUtcOffset(today)));
        var dayEnd = ToUtcColumnText(new DateTimeOffset(today.AddDays(1), TimeZoneInfo.Local.GetUtcOffset(today.AddDays(1))));
        var lines = new List<string>
        {
            $"Datum: {DateTime.Now:dd.MM.yyyy}",
            "",
            "Bediener | Bons | Umsatz | Bar | Karte"
        };
        await using var c = _db.OpenConnection();
        var saleRowCount = 0;
        await using (var q = c.CreateCommand())
        {
            // R101: same cash/card split + historical-row fallback as
            // GetPeriodSummaryAsync above, per operator.
            q.CommandText = """
                SELECT COALESCE(o.operator_name,'UNBEKANNT'),COUNT(*),COALESCE(SUM(s.total_cents),0),
                       COALESCE(SUM(CASE WHEN s.cash_portion_cents<>0 OR s.card_portion_cents<>0 THEN s.cash_portion_cents
                                         WHEN s.payment_method='CASH' THEN s.total_cents ELSE 0 END),0),
                       COALESCE(SUM(CASE WHEN s.cash_portion_cents<>0 OR s.card_portion_cents<>0 THEN s.card_portion_cents
                                         WHEN s.payment_method='CARD' THEN s.total_cents ELSE 0 END),0)
                FROM sales s
                LEFT JOIN sale_operators o ON o.sale_id=s.id
                WHERE COALESCE(s.transaction_type,'SALE')='SALE'
                  AND s.created_at_utc >= $from AND s.created_at_utc < $to
                GROUP BY COALESCE(o.operator_name,'UNBEKANNT')
                ORDER BY 3 DESC;
                """;
            q.Parameters.AddWithValue("$from", dayStart);
            q.Parameters.AddWithValue("$to", dayEnd);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                saleRowCount++;
                lines.Add($"{r.GetString(0)} | {r.GetInt64(1)} | {Money(r.GetInt64(2))} | {Money(r.GetInt64(3))} | {Money(r.GetInt64(4))}");
            }
        }
        if (saleRowCount == 0) lines.Add("(keine)");

        lines.Add("");
        lines.Add("STORNO/RETOURE NACH BEARBEITER (nicht im Umsatz oben enthalten)");
        lines.Add("Bearbeiter | Anzahl | Betrag");
        var reversalRowCount = 0;
        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT COALESCE(o.operator_name,'UNBEKANNT'),COUNT(*),COALESCE(SUM(s.total_cents),0)
                FROM sales s
                LEFT JOIN sale_operators o ON o.sale_id=s.id
                WHERE s.transaction_type IN ('STORNO','RETURN')
                  AND s.created_at_utc >= $from AND s.created_at_utc < $to
                GROUP BY COALESCE(o.operator_name,'UNBEKANNT')
                ORDER BY 3 DESC;
                """;
            q.Parameters.AddWithValue("$from", dayStart);
            q.Parameters.AddWithValue("$to", dayEnd);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                reversalRowCount++;
                lines.Add($"{r.GetString(0)} | {r.GetInt64(1)} | {Money(r.GetInt64(2))}");
            }
        }
        if (reversalRowCount == 0) lines.Add("(keine)");

        return new ReportDocument("BEDIENERABRECHNUNG", lines, DateTimeOffset.Now);
    });
}public async Task<ReportDocument> BuildPersonnelMonitoringAsync(CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var lines = new List<string>
        {
            "Auswertung aus Audit-Log - nur betriebliche POS-Aktionen, keine Tastatur-/Bildschirmüberwachung.",
            "",
            "Benutzer | Aktionen | Letzte Aktion"
        };
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT actor,COUNT(*),MAX(created_at)
            FROM audit_log
            WHERE actor<>''
            GROUP BY actor
            ORDER BY COUNT(*) DESC;
            """;
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            lines.Add($"{r.GetString(0)} | {r.GetInt64(1)} | {r.GetString(2)}");
        return new ReportDocument("PERSONALÜBERWACHUNG", lines, DateTimeOffset.Now);
    });
}// R88: rebuilt from scratch - the original version only queried
// audit_log WHERE event_type LIKE '%STORNO%', which silently missed BOTH
// real reversal kinds it should have covered: SOFORT STORNO is logged in
// a completely different table (pos_action_log.action_type='SOFORT_STORNO',
// not audit_log at all), and R82's Teilretoure logs audit_log event_type
// 'SALE_RETURN', which the LIKE pattern never matched. So this report has
// been silently incomplete since R82 shipped. Now pulls each kind from its
// actual source of truth instead of pattern-matching a free-text log.
public async Task<ReportDocument> BuildStornoReportAsync(CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var lines = new List<string>
        {
            "Storno- und Retourenjournal.",
            "SOFORT STORNO: Position aus einem noch offenen Verkauf entfernt, bevor abkassiert wurde.",
            "BON STORNO: volle Gegenbuchung eines bereits abgeschlossenen Bons (BAR, R79).",
            "TEILRETOURE: Rückgabe einzelner Positionen eines bereits abgeschlossenen Bons (BAR, R82).",
            "Hinweis: BON STORNO/TEILRETOURE sind bis zur echten TSE-Freigabe fiskalisch gesperrt; " +
                "diese Zeilen erscheinen erst nach Produktivfreigabe.",
            ""
        };

        await using var c = _db.OpenConnection();

        lines.Add("SOFORT STORNO");
        lines.Add("Zeit | Benutzer | Details | Betrag");
        var sofortCount = 0;
        long sofortCents = 0;
        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT created_at,actor,details,amount_cents
                FROM pos_action_log
                WHERE action_type='SOFORT_STORNO' AND phase='APPLIED'
                ORDER BY id DESC
                LIMIT 500;
                """;
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                sofortCount++;
                sofortCents += r.GetInt64(3);
                lines.Add($"{r.GetString(0)} | {r.GetString(1)} | {r.GetString(2)} | {Money(r.GetInt64(3))}");
            }
        }
        if (sofortCount == 0) lines.Add("(keine)");
        lines.Add("");
        lines.Add($"Summe SOFORT STORNO: {sofortCount} · {Money(sofortCents)}");
        lines.Add("");

        lines.Add("BON STORNO / TEILRETOURE");
        lines.Add("Zeit | Bon-Nr. | Referenz-Bon | Art | Bediener | Betrag");
        var reversalCount = 0;
        long reversalCents = 0;
        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT s.created_at,s.receipt_number,COALESCE(orig.receipt_number,0),
                       s.transaction_type,COALESCE(o.operator_name,''),s.total_cents
                FROM sales s
                LEFT JOIN sale_operators o ON o.sale_id=s.id
                LEFT JOIN sales orig ON orig.id=s.original_sale_id
                WHERE s.transaction_type IN ('STORNO','RETURN')
                ORDER BY s.id DESC
                LIMIT 500;
                """;
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                reversalCount++;
                reversalCents += r.GetInt64(5);
                var kind = r.GetString(3) == "STORNO" ? "BON STORNO" : "TEILRETOURE";
                lines.Add(
                    $"{r.GetString(0)} | {r.GetInt64(1):000000} | {r.GetInt64(2):000000} | " +
                    $"{kind} | {r.GetString(4)} | {Money(r.GetInt64(5))}");
            }
        }
        if (reversalCount == 0) lines.Add("(keine)");
        lines.Add("");
        lines.Add($"Summe BON STORNO/TEILRETOURE: {reversalCount} · {Money(reversalCents)}");

        return new ReportDocument("STORNO- UND RETOURENJOURNAL", lines, DateTimeOffset.Now);
    });
}/// <summary>
/// R144: the data of the notification under § 146a Abs. 4 AO, laid out as AEAO
/// zu § 146a Nr. 1.16.2 asks for them, to be entered in Mein ELSTER. TOR does not
/// transmit anything itself.
/// </summary>
public async Task<ReportDocument> BuildKassenmeldungAsync(CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var s = await _settings.LoadAllAsync(ct);
        string easSerial = "", manufacturer = "", model = "";
        IReadOnlyCollection<string> tseSerials;
        await using (var c = _db.OpenConnection())
        {
            await using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT eas_serial,manufacturer,model FROM system_identity WHERE id=1;";
                await using var r = await q.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    easSerial = r.GetString(0);
                    manufacturer = r.GetString(1);
                    model = r.GetString(2);
                }
            }

            tseSerials = (await TseMasterDataRepository.LoadAllAsync(c, ct)).Keys;
        }

        string Or(string value, string missing) => string.IsNullOrWhiteSpace(value) ? missing : value.Trim();
        var serial = KassenSeriennummer.From(easSerial);
        var tseSerial = tseSerials.Count > 0 ? string.Join(", ", tseSerials) : Get(s, "tse.serial");
        var clientId = Get(s, "tse.client_id");
        var lines = new List<string>
        {
            "MITTEILUNG NACH § 146a ABS. 4 AO",
            "Daten zur Übernahme in Mein ELSTER (AEAO zu § 146a Nr. 1.16.2).",
            "TOR übermittelt nichts selbst.",
            "",
            "STEUERPFLICHTIGER / BETRIEBSSTÄTTE (Nr. 1.16.1.4, 1.16.2.1)",
            $"Steuernummer: {Or(Get(s, "company.tax_no"), "FEHLT - bitte unter Firma eintragen")}",
            $"Firma: {Get(s, "company.name")}",
            $"Betriebsstätte: {Get(s, "company.street")}, {Get(s, "company.zip")} {Get(s, "company.city")}",
            "",
            "ELEKTRONISCHES AUFZEICHNUNGSSYSTEM (Nr. 1.16.2.3 - 1.16.2.7)",
            "Art: Elektronisches oder computergestütztes Kassensystem",
            "Anzahl in dieser Betriebsstätte: jede Kasse einzeln melden (dieses Protokoll je Kasse erstellen)",
            $"Seriennummer: {serial}",
            $"Hersteller / Software: {manufacturer} {model} · {TorRelease.DisplayName} ({TorRelease.Version})",
            $"Datum der Anschaffung: {Or(Get(s, "legal.kassenmeldung.anschaffung"), "FEHLT - bitte unter Recht & Freigabe eintragen (bei Leasing/Leihe: Beginn)")}",
            $"Datum der Außerbetriebnahme: {Or(Get(s, "legal.kassenmeldung.ausserbetriebnahme"), "- (in Betrieb)")}",
            "",
            "TECHNISCHE SICHERHEITSEINRICHTUNG (Nr. 1.16.2.2)",
            $"Zertifizierungs-ID (BSI-K-TR-nnnn-yyyy): {Or(Get(s, "tse.bsi_id"), "FEHLT - bei TSE-Aktivierung übernehmen")}",
            $"Seriennummer der TSE: {Or(tseSerial, "FEHLT - TSE-Export (TAR) erstellen")}",
            "",
            "PRÜFUNG",
            KassenSeriennummer.ClientIdMatches(clientId, easSerial)
                ? $"TSE-Client-ID {clientId} = Seriennummer der Kasse - Bon, TSE, DSFinV-K und Mitteilung stimmen überein."
                : $"ACHTUNG: TSE-Client-ID ist \"{clientId}\", die Seriennummer der Kasse \"{serial}\". Beide müssen gleich sein.",
            "",
            "Frist: innerhalb eines Monats nach Anschaffung bzw. Außerbetriebnahme (§ 146a Abs. 4 AO).",
            "Jede Meldung enthält alle Aufzeichnungssysteme der Betriebsstätte (Nr. 1.16.1.4)."
        };
        return new ReportDocument("KASSENMELDUNG § 146a ABS. 4 AO", lines, DateTimeOffset.Now);
    });
}

public async Task<ReportDocument> BuildProgrammingProtocolAsync(ICommercialLicenseService commercialLicense, ITseProvider tseProvider, CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var s = await _settings.LoadAllAsync(ct);
        var edition = InstallationEditionValue(s);
        var license = commercialLicense.Check(edition);
        var tse = await tseProvider.ProbeAsync(ct);
        string easSerial = "";
        string manufacturer = "";
        string model = "";
        await using (var c = _db.OpenConnection())
        await using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT eas_serial,manufacturer,model FROM system_identity WHERE id=1;";
            await using var r = await q.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                easSerial = r.GetString(0);
                manufacturer = r.GetString(1);
                model = r.GetString(2);
            }
        }

        var companyName = Get(s, "company.name");
        var companyOwner = Get(s, "company.owner");
        var companyStreet = Get(s, "company.street");
        var companyZip = Get(s, "company.zip");
        var companyCity = Get(s, "company.city");
        var companyTaxNo = Get(s, "company.tax_no");
        var companyVatId = Get(s, "company.vat_id");
        var appVersion = Get(s, "app.version");
        var registerNumber = Get(s, "cash.register.number");
        var registerName = Get(s, "cash.register.name");
        var tseSerial = tse.Device?.SerialNumber ?? Get(s, "tse.serial");
        var tseBsi = tse.Device?.BsiCertificationId ?? Get(s, "tse.bsi_id");
        var tseExpiry = tse.Device?.CertificateValidUntil?.ToString("dd.MM.yyyy") ?? Get(s, "tse.expiry_date");
        var tseClient = Get(s, "tse.client_id");
        var taxStandard = Get(s, "tax.standard");
        var taxReduced = Get(s, "tax.reduced");
        var dsfinvkVersion = Get(s, "legal.dsfinvk.version");
        var receiptPrinterModel = Get(s, "device.receipt_printer.model");
        var receiptPrinterName = Get(s, "device.receipt_printer.name");
        var terminalVendor = Get(s, "payment.terminal.vendor");
        var terminalModel = Get(s, "payment.terminal.model");
        var licenseExpiry = license.ValidUntilUtc?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "";
        var lines = new List<string>
        {
            "KASSEN-PROGRAMMIERUNGSPROTOKOLL",
            "",
            $"Erstellt am: {DateTime.Now:dd.MM.yyyy HH:mm:ss}",
            "",
            "BETRIEB",
            $"Firma: {companyName}",
            $"Inhaber / Betreiber: {companyOwner}",
            $"Anschrift: {companyStreet}, {companyZip} {companyCity}",
            $"Steuernummer: {companyTaxNo}",
            $"USt-IdNr.: {companyVatId}",
            "",
            "KASSENSYSTEM / eAS",
            $"Hersteller: {manufacturer}",
            $"Modell: {model}",
            $"Software: TOR POS Pro {appVersion}",
            $"Edition: {edition}",
            $"Kassennummer: {registerNumber}",
            $"Kassenname: {registerName}",
            $"Kassen-Seriennummer: {KassenSeriennummer.From(easSerial)}",
            $"Interne Kassen-ID (DSFinV-K Z_KASSE_ID): {easSerial}",
            "",
            "TSE",
            $"Status: {tse.State} - {tse.Message}",
            $"Hersteller: {tse.Device?.Manufacturer ?? ""}",
            $"Produkt: {tse.Device?.ProductFamily ?? ""}",
            $"TSE-Seriennummer: {tseSerial}",
            $"BSI-Zertifizierungs-ID: {tseBsi}",
            $"Zertifikatsablauf: {tseExpiry}",
            $"Client-ID: {tseClient}",
            "",
            "LIZENZ",
            $"Status: {license.State}",
            $"Kunden-Nr.: {license.CustomerNumber}",
            $"Kunde: {license.CustomerName}",
            $"License-ID: {license.LicenseId}",
            $"Installations-ID: {commercialLicense.InstallationId}",
            $"PC-Gerätecode: {commercialLicense.DeviceCode}",
            $"Gültig bis: {licenseExpiry}",
            "",
            "PROGRAMMIERUNG / STEUERN",
            $"Regelsteuersatz: {taxStandard} %",
            $"Ermäßigter Steuersatz: {taxReduced} %",
            $"DSFinV-K Version: {dsfinvkVersion}",
            $"Bondrucker: {receiptPrinterModel} / {receiptPrinterName}",
            $"Kartenterminal: {terminalVendor} {terminalModel}",
            "",
            "DOKUMENTATIONSHINWEIS",
            "Dieses automatisch erzeugte Programmierungsprotokoll dokumentiert die in TOR POS gespeicherten Kassen-, TSE- und Programmeinstellungen zum Erstellungszeitpunkt.",
            "Es ersetzt keine amtliche Bescheinigung und keine vorgeschriebene Verfahrensdokumentation. Produktive Fiskalfreigabe setzt die vollständige TSE- und DSFinV-K-Prüfung voraus."
        };
        return new ReportDocument("PROGRAMMIERUNGSPROTOKOLL", lines, DateTimeOffset.Now);
    });
}public async Task<string> ExportBookingDataAsync(string targetPath, CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? Environment.CurrentDirectory);
        await using var writer = new StreamWriter(targetPath, false, new UTF8Encoding(false));
        // R101: BAR_ANTEIL_CENT/KARTE_ANTEIL_CENT let an auditor recover the
        // cash/card split of a Bon whose ZAHLART reads 'MIXED', which the
        // plain payment-method text alone cannot express.
        await writer.WriteLineAsync("BON;DATUM;ZAHLART;BAR_ANTEIL_CENT;KARTE_ANTEIL_CENT;BEDIENER;ARTIKEL;EAN;MENGE;LISTENPREIS_CENT;VERKAUFSPREIS_CENT;ANGEBOT_ID;ANGEBOT_NAME;ANGEBOT_PROZENT;ANGEBOT_RABATT_CENT;ANGEBOT_VON;ANGEBOT_BIS;UST;PFAND_CENT;ZEILENSUMME_CENT;MANUELLER_RABATT_BON_CENT;BON_GESAMT_CENT");
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT s.receipt_number,s.created_at,s.payment_method,
                   CASE WHEN s.cash_portion_cents<>0 OR s.card_portion_cents<>0 THEN s.cash_portion_cents
                        WHEN s.payment_method='CASH' THEN s.total_cents ELSE 0 END,
                   CASE WHEN s.cash_portion_cents<>0 OR s.card_portion_cents<>0 THEN s.card_portion_cents
                        WHEN s.payment_method='CARD' THEN s.total_cents ELSE 0 END,
                   COALESCE(o.operator_name,''),
                   i.product_name,i.barcode,i.quantity,
                   CASE WHEN i.list_unit_price_cents>0 THEN i.list_unit_price_cents ELSE i.unit_price_cents+i.promotion_discount_unit_cents END,
                   i.unit_price_cents,i.promotion_id,i.promotion_name,i.promotion_percent,
                   i.promotion_discount_cents,i.promotion_start_date,i.promotion_end_date,
                   i.vat_rate,i.pfand_cents,i.line_total_cents,s.discount_cents,s.total_cents
            FROM sales s
            JOIN sale_items i ON i.sale_id=s.id
            LEFT JOIN sale_operators o ON o.sale_id=s.id
            ORDER BY s.receipt_number,i.id;
            """;
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            await writer.WriteLineAsync(string.Join(";", new[]
            {
                r.GetInt64(0).ToString(CultureInfo.InvariantCulture),
                Csv(r.GetString(1)), Csv(r.GetString(2)),
                r.GetInt64(3).ToString(CultureInfo.InvariantCulture),
                r.GetInt64(4).ToString(CultureInfo.InvariantCulture),
                Csv(r.GetString(5)),
                Csv(r.GetString(6)), Csv(r.GetString(7)),
                r.GetDouble(8).ToString("0.###", CultureInfo.InvariantCulture),
                r.GetInt64(9).ToString(CultureInfo.InvariantCulture),
                r.GetInt64(10).ToString(CultureInfo.InvariantCulture),
                r.GetInt64(11).ToString(CultureInfo.InvariantCulture),
                Csv(r.GetString(12)),
                r.GetInt32(13).ToString(CultureInfo.InvariantCulture),
                r.GetInt64(14).ToString(CultureInfo.InvariantCulture),
                Csv(r.GetString(15)), Csv(r.GetString(16)),
                r.GetDouble(17).ToString("0.##", CultureInfo.InvariantCulture),
                r.GetInt64(18).ToString(CultureInfo.InvariantCulture),
                r.GetInt64(19).ToString(CultureInfo.InvariantCulture),
                r.GetInt64(20).ToString(CultureInfo.InvariantCulture),
                r.GetInt64(21).ToString(CultureInfo.InvariantCulture)
            }));
        }

        return targetPath;
    });
}public async Task<string> ExportGdpduAuditPackageAsync(string targetDirectory, CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        Directory.CreateDirectory(targetDirectory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var audit = Path.Combine(targetDirectory, $"TOR-Audit-{stamp}.csv");
        var bookings = Path.Combine(targetDirectory, $"TOR-Buchungsdaten-{stamp}.csv");
        var articles = Path.Combine(targetDirectory, $"TOR-Artikel-{stamp}.csv");
        await _audit.ExportCsvAsync(audit, ct: ct);
        await ExportBookingDataAsync(bookings, ct);
        await ExportArticlesCsvAsync(articles, ct);
        var note = Path.Combine(targetDirectory, $"TOR-GDPdU-GoBD-Hinweis-{stamp}.txt");
        await File.WriteAllTextAsync(note, "GDPdU ist eine historische Bezeichnung. Für aktuelle Kassennachschauen und Außenprüfungen sind insbesondere GoBD, KassenSichV, AO §146a und die DSFinV-K relevant.\r\n" + "Dieses Paket enthält interne TOR-CSV-Auswertungen und ist KEIN Ersatz für einen vollständigen, offiziell validierten DSFinV-K-Export.\r\n", new UTF8Encoding(false), ct);
        return targetDirectory;
    });
}
    public string CreatePdf(ReportDocument document, string? preferredDirectory = null)
    {
        var directory = string.IsNullOrWhiteSpace(preferredDirectory)
            ? Path.Combine(AppPaths.DataDirectory, "Reports")
            : preferredDirectory.Trim();
        Directory.CreateDirectory(directory);
        var safe = SafeFileName(document.Title);
        var path = Path.Combine(directory, $"{safe}-{DateTime.Now:yyyyMMdd-HHmmss}.pdf");
        SimplePdfWriter.WriteTextReport(path, document.Title, document.Lines);
        return path;
    }
public async Task<string> CreateArticleLabelsPdfAsync(CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var rows = await GetInventoryAsync(ct);
        var directory = Path.Combine(AppPaths.DataDirectory, "Reports");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"Artikel-Etiketten-{DateTime.Now:yyyyMMdd-HHmmss}.pdf");
        SimplePdfWriter.WriteArticleLabels(path, rows);
        return path;
    });
}
    public static void OpenFile(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private sealed record TaxTurnoverRow(
        decimal Rate,
        long GrossCents,
        long NetCents,
        long TaxCents);

    private sealed record PromotionTurnoverRow(
        long PromotionId,
        string Name,
        int Percent,
        string StartDate,
        string EndDate,
        long ReceiptCount,
        long ListGrossCents,
        long DiscountCents,
        long SalesGrossCents);

    private sealed record OpenPeriodSummary(
        DateTimeOffset From,
        DateTimeOffset To,
        int ReceiptCount,
        long ListGrossCents,
        long PromotionDiscountCents,
        long ManualDiscountCents,
        long SalesGrossAfterDiscountCents,
        long StornoCents,
        long ReturnCents,
        long GrossCents,
        long CashCents,
        long CardCents,
        long ImmediateStornoCount,
        long ImmediateStornoCents,
        IReadOnlyList<TaxTurnoverRow> Taxes,
        IReadOnlyList<PromotionTurnoverRow> Promotions,
        IReadOnlyList<CashMovementTotalRow> CashMovements);

    /// <summary>R141: Einlagen/Entnahmen of the period by business case, signed.</summary>
    private sealed record CashMovementTotalRow(string Label, long SignedCents);

    private async Task<OpenPeriodSummary> GetOpenPeriodAsync(
        CancellationToken ct)
    {
        // R117: the open period runs from the last Tagesabschluss to now, with
        // NO midnight floor. It used to start at max(today's midnight, last
        // closing), which silently dropped every sale made between the last
        // closing and midnight - exactly the evening trade of a business that
        // stays open past midnight, which is the normal IMBISS case. Those
        // sales then appeared in no Z-report at all: the Z on the following
        // night started at 00:00 and the previous one had already been closed
        // hours earlier. Every sale must belong to exactly one Z period.
        //
        // With no closing recorded yet, the first Z legitimately covers
        // everything up to that point rather than only the current calendar
        // day, which would orphan earlier sales permanently.
        var to = DateTimeOffset.Now;
        var from = DateTimeOffset.MinValue;

        await using var c = _db.OpenConnection();

        await using (var last = c.CreateCommand())
        {
            last.CommandText =
                "SELECT closed_at FROM daily_closings ORDER BY id DESC LIMIT 1;";

            var value = await last.ExecuteScalarAsync(ct);

            if (value is string s &&
                DateTimeOffset.TryParse(s, out var parsed))
            {
                from = parsed;
            }
        }

        return await GetPeriodSummaryAsync(
            from,
            to,
            ct);
    }

    private async Task<OpenPeriodSummary> GetPeriodSummaryAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        // UTC text bounds so a period spanning a DST transition (this feeds
        // X-/Z-report turnover and VAT totals) compares as a true instant
        // instead of raw local-offset text.
        var fromUtcText = ToUtcColumnText(from);
        var toUtcText = ToUtcColumnText(to);
        await using var c = _db.OpenConnection();

        int receiptCount;
        long listGross;
        long promotionDiscount;
        long manualDiscount;
        long salesGross;
        long storno;
        long returns;
        long cash;
        long card;

        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT
                    COALESCE(SUM(CASE WHEN COALESCE(transaction_type,'SALE')='SALE' THEN 1 ELSE 0 END),0),
                    COALESCE(SUM(CASE WHEN COALESCE(transaction_type,'SALE')='SALE'
                        THEN CASE
                            WHEN COALESCE(list_subtotal_cents,0)>0
                                THEN list_subtotal_cents
                            ELSE subtotal_cents + COALESCE(promotion_discount_cents,0)
                        END
                        ELSE 0 END),0),
                    COALESCE(SUM(CASE WHEN COALESCE(transaction_type,'SALE')='SALE'
                        THEN COALESCE(promotion_discount_cents,0) ELSE 0 END),0),
                    COALESCE(SUM(CASE WHEN COALESCE(transaction_type,'SALE')='SALE'
                        THEN discount_cents ELSE 0 END),0),
                    COALESCE(SUM(CASE WHEN COALESCE(transaction_type,'SALE')='SALE'
                        THEN total_cents ELSE 0 END),0),
                    COALESCE(SUM(CASE WHEN transaction_type='STORNO'
                        THEN total_cents ELSE 0 END),0),
                    COALESCE(SUM(CASE WHEN transaction_type='RETURN'
                        THEN total_cents ELSE 0 END),0),
                    -- R101: a Mixed sale's split is correctly distributed
                    -- across both Bar and Karte via cash_portion_cents/
                    -- card_portion_cents; falls back to total_cents by
                    -- payment_method for a historical pre-R101 row (sales is
                    -- append-only, so such a row can never be backfilled).
                    -- R141: a Storno/Retoure pays back in the way it was paid
                    -- (R102) and is taken off its payment type, so Bar + Karte
                    -- equal "Umsatz nach Storno/Retouren", as in the DSFinV-K
                    -- Kassenabschluss (Z_Zahlart). Until R141 both counted sales only.
                    COALESCE(SUM(
                        CASE WHEN COALESCE(transaction_type,'SALE')='SALE' THEN 1
                             WHEN transaction_type IN ('STORNO','RETURN') THEN -1 ELSE 0 END *
                        CASE WHEN cash_portion_cents<>0 OR card_portion_cents<>0 THEN cash_portion_cents
                             WHEN payment_method='CASH' THEN total_cents ELSE 0 END),0),
                    COALESCE(SUM(
                        CASE WHEN COALESCE(transaction_type,'SALE')='SALE' THEN 1
                             WHEN transaction_type IN ('STORNO','RETURN') THEN -1 ELSE 0 END *
                        CASE WHEN cash_portion_cents<>0 OR card_portion_cents<>0 THEN card_portion_cents
                             WHEN payment_method='CARD' THEN total_cents ELSE 0 END),0)
                FROM sales
                WHERE created_at_utc >= $from
                  AND created_at_utc <= $to;
                """;

            q.Parameters.AddWithValue(
                "$from",
                fromUtcText);
            q.Parameters.AddWithValue(
                "$to",
                toUtcText);

            await using var r =
                await q.ExecuteReaderAsync(ct);

            await r.ReadAsync(ct);

            receiptCount = Convert.ToInt32(r.GetInt64(0));
            listGross = r.GetInt64(1);
            promotionDiscount = r.GetInt64(2);
            manualDiscount = r.GetInt64(3);
            salesGross = r.GetInt64(4);
            storno = r.GetInt64(5);
            returns = r.GetInt64(6);
            cash = r.GetInt64(7);
            card = r.GetInt64(8);
        }

        var taxGroups = new Dictionary<decimal,long>();
        // R79 FIX: a STORNO/RETURN row's own VAT must be subtracted from the same
        // per-rate totals a SALE contributes to - otherwise afterReversals (gross)
        // already nets Storno/Retouren out, but the MwSt.-Zusammenfassung above it
        // would keep overstating VAT collected by exactly the reversed amount.
        var saleTaxRows = new List<(long SaleId,long Discount,decimal Rate,long Gross,string TransactionType)>();

        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT
                    s.id,
                    s.discount_cents,
                    i.quantity,
                    i.unit_price_cents,
                    i.vat_rate,
                    COALESCE(i.vat_allocations_json,''),
                    COALESCE(s.transaction_type,'SALE')
                FROM sales s
                JOIN sale_items i ON i.sale_id=s.id
                WHERE s.created_at_utc >= $from
                  AND s.created_at_utc <= $to
                  AND COALESCE(s.transaction_type,'SALE') IN ('SALE','STORNO','RETURN')
                ORDER BY s.id,i.id;
                """;

            q.Parameters.AddWithValue("$from", fromUtcText);
            q.Parameters.AddWithValue("$to", toUtcText);

            await using var r =
                await q.ExecuteReaderAsync(ct);

            while (await r.ReadAsync(ct))
            {
                var saleId = r.GetInt64(0);
                var discount = r.GetInt64(1);
                var line = new CartLine
                {
                    Quantity = Convert.ToDecimal(r.GetDouble(2)),
                    UnitPriceCents = r.GetInt64(3),
                    VatRate = Convert.ToDecimal(r.GetDouble(4)),
                    VatAllocations = VatAllocationStorage.Deserialize(r.GetString(5))
                };
                var transactionType = r.GetString(6);

                foreach (var allocation in MenuVatPolicy.LineAllocations(line))
                    saleTaxRows.Add((
                        saleId,
                        discount,
                        allocation.VatRate,
                        allocation.GrossCents,
                        transactionType));
            }
        }

        foreach (var sale in saleTaxRows.GroupBy(x => x.SaleId))
        {
            var groups = sale.ToArray();
            var sign = groups[0].TransactionType == "SALE" ? 1 : -1;
            var subtotal = groups.Sum(x => x.Gross);
            var discount = Math.Clamp(
                groups[0].Discount,
                0L,
                Math.Max(0L, subtotal));
            var targetGross = subtotal - discount;
            var assigned = 0L;

            for (var i = 0; i < groups.Length; i++)
            {
                var group = groups[i];

                var gross = i == groups.Length - 1
                    ? targetGross - assigned
                    : subtotal <= 0
                        ? 0
                        : (long)Math.Round(
                            group.Gross *
                            (targetGross / (decimal)subtotal),
                            MidpointRounding.AwayFromZero);

                assigned += gross;
                taxGroups[group.Rate] =
                    taxGroups.GetValueOrDefault(group.Rate) + sign * gross;
            }
        }

        var taxes = taxGroups
            .OrderBy(x => x.Key)
            .Select(x =>
            {
                var divisor = 1m + x.Key / 100m;
                var net = divisor <= 0m
                    ? x.Value
                    : (long)Math.Round(
                        x.Value / divisor,
                        MidpointRounding.AwayFromZero);

                return new TaxTurnoverRow(
                    x.Key,
                    x.Value,
                    net,
                    x.Value - net);
            })
            .ToArray();

        var promotions = new List<PromotionTurnoverRow>();

        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT
                    i.promotion_id,
                    i.promotion_name,
                    i.promotion_percent,
                    i.promotion_start_date,
                    i.promotion_end_date,
                    COUNT(DISTINCT i.sale_id),
                    COALESCE(SUM(
                        CASE WHEN i.list_line_total_cents>0
                             THEN i.list_line_total_cents
                             ELSE i.line_total_cents + i.promotion_discount_cents
                        END),0),
                    COALESCE(SUM(i.promotion_discount_cents),0),
                    COALESCE(SUM(i.line_total_cents),0)
                FROM sale_items i
                JOIN sales s ON s.id=i.sale_id
                WHERE s.created_at_utc >= $from
                  AND s.created_at_utc <= $to
                  AND COALESCE(s.transaction_type,'SALE')='SALE'
                  AND i.promotion_id>0
                GROUP BY
                    i.promotion_id,
                    i.promotion_name,
                    i.promotion_percent,
                    i.promotion_start_date,
                    i.promotion_end_date
                ORDER BY SUM(i.promotion_discount_cents) DESC,
                         i.promotion_id;
                """;

            q.Parameters.AddWithValue("$from", fromUtcText);
            q.Parameters.AddWithValue("$to", toUtcText);

            await using var r =
                await q.ExecuteReaderAsync(ct);

            while (await r.ReadAsync(ct))
            {
                promotions.Add(
                    new PromotionTurnoverRow(
                        r.GetInt64(0),
                        r.GetString(1),
                        r.GetInt32(2),
                        r.GetString(3),
                        r.GetString(4),
                        r.GetInt64(5),
                        r.GetInt64(6),
                        r.GetInt64(7),
                        r.GetInt64(8)));
            }
        }

        long immediateStornoCount = 0;
        long immediateStornoCents = 0;

        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT
                    COUNT(*),
                    COALESCE(SUM(amount_cents),0)
                FROM pos_action_log
                WHERE created_at_utc >= $from
                  AND created_at_utc <= $to
                  AND action_type='SOFORT_STORNO'
                  AND phase='APPLIED';
                """;

            q.Parameters.AddWithValue("$from", fromUtcText);
            q.Parameters.AddWithValue("$to", toUtcText);

            await using var r =
                await q.ExecuteReaderAsync(ct);

            await r.ReadAsync(ct);
            immediateStornoCount = r.GetInt64(0);
            immediateStornoCents = r.GetInt64(1);
        }

        // R131: this used to be Math.Max(0, ...). A period in which more was
        // cancelled or returned than sold - the Storno of a large receipt from
        // the day before, for instance - printed "Umsatz nach Storno/Retouren:
        // 0,00" and archived gross_cents 0, while the VAT lines of the same
        // Z-Bericht correctly showed the negative amounts. The DSFinV-K
        // Kassenabschluss of that period is negative, so is this figure.
        var afterReversals =
            salesGross - storno - returns;

        // R141: the cash flows of the period that are not sales - Einlagen,
        // Entnahmen, Kassendifferenzen (R134, R139) - so the Z-Bericht shows the
        // cash of the period like the DSFinV-K Kassenabschluss does. Test
        // entries never belong to a closing.
        var cashMovements = new List<CashMovementTotalRow>();
        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT movement_type, COALESCE(business_case,''), COALESCE(SUM(amount_cents),0)
                FROM cash_movements
                WHERE created_at_utc >= $from
                  AND created_at_utc <= $to
                  AND movement_type IN ('EINLAGE','ENTNAHME')
                  AND fiscal_mode <> 'TEST_ONLY'
                GROUP BY movement_type, COALESCE(business_case,'')
                ORDER BY movement_type, COALESCE(business_case,'');
                """;
            q.Parameters.AddWithValue("$from", fromUtcText);
            q.Parameters.AddWithValue("$to", toUtcText);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var inflow = r.GetString(0) == "EINLAGE";
                var kind = inflow ? CashMovementKind.Einlage : CashMovementKind.Entnahme;
                var label = Enum.TryParse<CashBusinessCase>(r.GetString(1), out var businessCase)
                    ? CashBusinessCases.Label(businessCase, kind)
                    : inflow ? "Einlage" : "Entnahme";
                cashMovements.Add(new CashMovementTotalRow(label, inflow ? r.GetInt64(2) : -r.GetInt64(2)));
            }
        }

        return new OpenPeriodSummary(
            from,
            to,
            receiptCount,
            listGross,
            promotionDiscount,
            manualDiscount,
            salesGross,
            storno,
            returns,
            afterReversals,
            cash,
            card,
            immediateStornoCount,
            immediateStornoCents,
            taxes,
            promotions,
            cashMovements);
    }

    private static ReportDocument BuildTurnoverDocument(
        string title,
        OpenPeriodSummary period,
        string extra)
    {
        static string PromotionDate(string value)
        {
            return DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var day)
                    ? day.ToString("dd.MM.yyyy")
                    : value;
        }

        var lines = new List<string>
        {
            $"Zeitraum: {period.From:dd.MM.yyyy HH:mm:ss} - {period.To:dd.MM.yyyy HH:mm:ss}",
            $"Bons: {period.ReceiptCount}",
            "",
            "UMSATZ / RABATTE",
            $"Listenwert vor Angebot/Rabatt: {Money(period.ListGrossCents)}",
            $"- Angebote / Aktionen: -{Money(period.PromotionDiscountCents)}",
            $"- Manuelle Rabatte: -{Money(period.ManualDiscountCents)}",
            $"= Umsatz nach Rabatt (brutto): {Money(period.SalesGrossAfterDiscountCents)}",
            "",
            "STORNO / RETOUREN",
            $"Belegstorno / Gegenbuchung: -{Money(period.StornoCents)}",
            $"Retouren: -{Money(period.ReturnCents)}",
            $"= Umsatz nach Storno/Retouren: {Money(period.GrossCents)}",
            $"Sofort-Storno vor Zahlung (Info): {period.ImmediateStornoCount} Vorgänge · {Money(period.ImmediateStornoCents)}",
            "",
            "ZAHLARTEN (NACH STORNO/RETOUREN)",
            $"Bar: {Money(period.CashCents)}",
            $"Karte: {Money(period.CardCents)}",
            "",
            "KASSENBEWEGUNGEN (BAR)"
        };

        // R141: Einlagen, Entnahmen and Kassendifferenzen of the period, and the
        // cash that moved in the drawer in total.
        if (period.CashMovements.Count == 0)
            lines.Add("Keine Einlagen / Entnahmen im Zeitraum.");
        foreach (var movement in period.CashMovements)
            lines.Add($"{movement.Label}: {Money(movement.SignedCents)}");
        lines.Add($"= Bar-Saldo des Zeitraums (Bar-Umsatz + Kassenbewegungen): {Money(period.CashCents + period.CashMovements.Sum(m => m.SignedCents))}");
        lines.Add("");
        lines.Add("UMSATZSTEUER NACH RABATT");

        if (period.Taxes.Count == 0)
        {
            lines.Add("Keine steuerpflichtigen Verkaufszeilen im Zeitraum.");
        }
        else
        {
            foreach (var tax in period.Taxes)
            {
                lines.Add(
                    GermanFormat.Number(tax.Rate, "0.##") + $" % · Brutto {Money(tax.GrossCents)} · " +
                    $"Netto {Money(tax.NetCents)} · Steuer {Money(tax.TaxCents)}");
            }
        }

        if (period.Promotions.Count > 0)
        {
            lines.Add("");
            lines.Add("ANGEBOTE / AKTIONEN IM ZEITRAUM");

            foreach (var promotion in period.Promotions)
            {
                lines.Add(
                    $"{promotion.Name} · -{promotion.Percent}% · " +
                    $"{PromotionDate(promotion.StartDate)}–{PromotionDate(promotion.EndDate)}");
                lines.Add(
                    $"  Bons {promotion.ReceiptCount} · Listenwert {Money(promotion.ListGrossCents)} · " +
                    $"Angebotsrabatt -{Money(promotion.DiscountCents)} · " +
                    $"Artikelumsatz {Money(promotion.SalesGrossCents)}");
            }
        }

        lines.Add("");
        lines.Add(extra);

        return new ReportDocument(
            title,
            lines,
            DateTimeOffset.Now);
    }

    private static async Task<long> EnsureGroupAsync(
        SqliteConnection c,
        SqliteTransaction tx,
        string name,
        CancellationToken ct)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Standard" : name.Trim();
        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "INSERT OR IGNORE INTO product_groups(name,sort_order,is_active) VALUES($n,0,1);";
            q.Parameters.AddWithValue("$n", name);
            await q.ExecuteNonQueryAsync(ct);
        }
        await using var id = c.CreateCommand();
        id.Transaction = tx;
        id.CommandText = "SELECT id FROM product_groups WHERE name=$n COLLATE NOCASE LIMIT 1;";
        id.Parameters.AddWithValue("$n", name);
        return Convert.ToInt64(await id.ExecuteScalarAsync(ct));
    }

    // R97 follow-up: import must inherit the Warengruppe's im_haus_applicable
    // choice the same way the normal UI editor does (SaveWithStockAsync) -
    // otherwise a product inserted/updated through import would silently
    // get the raw SQLite column default instead of the category's actual,
    // possibly-admin-switched-off setting. The UPSERT below deliberately
    // does NOT touch im_haus_applicable on an existing category (only
    // group_id/vat_rate are in the UPDATE SET) - importing article prices
    // must never silently reset an admin's per-Warengruppe Im-Haus choice.
    private static async Task<(long CategoryId, bool ImHausApplicable)> EnsureCategoryAsync(
        SqliteConnection c,
        SqliteTransaction tx,
        long groupId,
        string name,
        decimal vat,
        CancellationToken ct,
        bool? imHausOverride = null)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Import" : name.Trim();
        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "INSERT OR IGNORE INTO categories(name,sort_order,is_active,edition_scope) VALUES($n,0,1,$scope);";
            q.Parameters.AddWithValue("$n", name);
            q.Parameters.AddWithValue("$scope", CurrentEditionScope(c, tx));
            await q.ExecuteNonQueryAsync(ct);
        }
        long categoryId;
        await using (var id = c.CreateCommand())
        {
            id.Transaction = tx;
            id.CommandText = "SELECT id FROM categories WHERE name=$n COLLATE NOCASE LIMIT 1;";
            id.Parameters.AddWithValue("$n", name);
            categoryId = Convert.ToInt64(await id.ExecuteScalarAsync(ct));
        }
        await using (var master = c.CreateCommand())
        {
            master.Transaction = tx;
            master.CommandText = """
                INSERT INTO category_master_data(category_id,group_id,vat_rate)
                VALUES($c,$g,$v)
                ON CONFLICT(category_id) DO UPDATE SET group_id=excluded.group_id,vat_rate=excluded.vat_rate;
                """;
            master.Parameters.AddWithValue("$c", categoryId);
            master.Parameters.AddWithValue("$g", groupId);
            master.Parameters.AddWithValue("$v", Convert.ToDouble(vat));
            await master.ExecuteNonQueryAsync(ct);
        }
        // Only overwritten when the caller explicitly provides a value (an
        // IM_HAUS CSV column, or a source database's own concrete setting) -
        // exactly like vat_rate above, EXCEPT a missing/blank source must
        // leave an already-existing Warengruppe's own choice untouched
        // instead of silently resetting it back to the column default.
        if (imHausOverride.HasValue)
        {
            await using var overrideCmd = c.CreateCommand();
            overrideCmd.Transaction = tx;
            overrideCmd.CommandText = "UPDATE category_master_data SET im_haus_applicable=$v WHERE category_id=$c;";
            overrideCmd.Parameters.AddWithValue("$v", imHausOverride.Value ? 1 : 0);
            overrideCmd.Parameters.AddWithValue("$c", categoryId);
            await overrideCmd.ExecuteNonQueryAsync(ct);
        }
        bool imHausApplicable;
        await using (var read = c.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT COALESCE(im_haus_applicable,1) FROM category_master_data WHERE category_id=$c;";
            read.Parameters.AddWithValue("$c", categoryId);
            imHausApplicable = Convert.ToInt64(await read.ExecuteScalarAsync(ct)) != 0;
        }
        return (categoryId, imHausApplicable);
    }

    private static async Task UpsertProductAsync(
        SqliteConnection c,
        SqliteTransaction tx,
        long categoryId,
        string name,
        string sku,
        string barcode,
        long price,
        decimal vat,
        long pfand,
        string unit,
        bool active,
        decimal stock,
        decimal minStock,
        long purchasePrice,
        CancellationToken ct,
        bool imHausApplicable = true)
    {
        long? existing = null;
        if (!string.IsNullOrWhiteSpace(barcode))
        {
            await using var byBarcode = c.CreateCommand();
            byBarcode.Transaction = tx;
            // ux_products_barcode is a GLOBAL unique index (not scoped by
            // edition_scope) - every fresh installation also seeds the same
            // demo barcode (Cola 0,33 l, 5449000000996) under KIOSK scope
            // unconditionally, so any cross-installation CSV/DB import can
            // legitimately hit an existing row in a DIFFERENT scope than the
            // one currently active. The lookup must match the real
            // constraint (global), not the app's read-side scope filtering,
            // or it misses that row and falls into an INSERT that collides
            // with the unique index instead of updating the existing row.
            byBarcode.CommandText = "SELECT id FROM products WHERE barcode=$v LIMIT 1;";
            byBarcode.Parameters.AddWithValue("$v", barcode);
            var value = await byBarcode.ExecuteScalarAsync(ct);
            if (value is not null) existing = Convert.ToInt64(value);
        }
        if (existing is null && !string.IsNullOrWhiteSpace(sku))
        {
            await using var bySku = c.CreateCommand();
            bySku.Transaction = tx;
            bySku.CommandText = "SELECT id FROM products WHERE sku=$v COLLATE NOCASE AND (UPPER(COALESCE(edition_scope,'ALL'))='ALL' OR UPPER(edition_scope)=$scope) LIMIT 1;";
            bySku.Parameters.AddWithValue("$scope", CurrentEditionScope(c, tx));
            bySku.Parameters.AddWithValue("$v", sku);
            var value = await bySku.ExecuteScalarAsync(ct);
            if (value is not null) existing = Convert.ToInt64(value);
        }

        var effectiveSku = (sku ?? "").Trim();
        if (existing is null && effectiveSku.Length == 0)
        {
            await using var seq = c.CreateCommand();
            seq.Transaction = tx;
            seq.CommandText = "UPDATE app_sequence SET value=MAX(value,COALESCE((SELECT MAX(CAST(sku AS INTEGER)) FROM products WHERE TRIM(sku)<>'' AND sku NOT GLOB '*[^0-9]*'),99999))+1 WHERE key='article_number'; SELECT value FROM app_sequence WHERE key='article_number';";
            effectiveSku = Convert.ToInt64(await seq.ExecuteScalarAsync(ct)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        await using var q = c.CreateCommand();
        q.Transaction = tx;
        if (existing is long id)
        {
            q.CommandText = """
                UPDATE products SET category_id=$c,name=$n,sku=CASE WHEN TRIM($sku)='' THEN sku ELSE $sku END,barcode=$ean,
                  base_price_cents=$price,vat_rate=$vat,pfand_cents=$pfand,unit=$unit,
                  is_active=$active,stock_quantity=$stock,stock_milli=$stockMilli,min_stock_quantity=$minstock,min_stock_milli=$minStockMilli,purchase_price_cents=$purchase,
                  im_haus_applicable=$imHaus
                WHERE id=$id;
                """;
            q.Parameters.AddWithValue("$id", id);
        }
        else
        {
            q.CommandText = """
                INSERT INTO products(category_id,name,sku,barcode,base_price_cents,vat_rate,pfand_cents,unit,is_active,stock_quantity,stock_milli,min_stock_quantity,min_stock_milli,purchase_price_cents,edition_scope,im_haus_applicable)
                VALUES($c,$n,$sku,$ean,$price,$vat,$pfand,$unit,$active,$stock,$stockMilli,$minstock,$minStockMilli,$purchase,$scope,$imHaus);
                """;
        }
        q.Parameters.AddWithValue("$imHaus", imHausApplicable ? 1 : 0);
        q.Parameters.AddWithValue("$c", categoryId);
        q.Parameters.AddWithValue("$n", name);
        q.Parameters.AddWithValue("$sku", effectiveSku);
        q.Parameters.AddWithValue("$ean", barcode ?? "");
        q.Parameters.AddWithValue("$price", price);
        q.Parameters.AddWithValue("$vat", Convert.ToDouble(vat));
        q.Parameters.AddWithValue("$pfand", pfand);
        q.Parameters.AddWithValue("$unit", unit ?? "Stück");
        q.Parameters.AddWithValue("$active", active ? 1 : 0);
        q.Parameters.AddWithValue("$stock", Convert.ToDouble(stock));
        q.Parameters.AddWithValue("$stockMilli", QuantityStorage.ToMilli(stock));
        q.Parameters.AddWithValue("$minstock", Convert.ToDouble(Math.Max(0m, minStock)));
        q.Parameters.AddWithValue("$minStockMilli", QuantityStorage.ToMilli(Math.Max(0m, minStock)));
        q.Parameters.AddWithValue("$purchase", Math.Max(0, purchasePrice));
        q.Parameters.AddWithValue("$scope", CurrentEditionScope(c, tx));
        await q.ExecuteNonQueryAsync(ct);
    }

    private static string CurrentEditionScope(SqliteConnection c, SqliteTransaction? tx = null)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT UPPER(COALESCE((SELECT value FROM app_settings WHERE key='business.mode'),'IMBISS'));";
        var scope = (q.ExecuteScalar() as string ?? "IMBISS").Trim().ToUpperInvariant();
        return scope is "KIOSK" or "IMBISS" ? scope : "ALL";
    }

    private static string Csv(string? value) =>
        "\"" + (value ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"'); i++;
                }
                else quoted = !quoted;
            }
            else if (ch == ';' && !quoted)
            {
                result.Add(sb.ToString()); sb.Clear();
            }
            else sb.Append(ch);
        }
        result.Add(sb.ToString());
        return result;
    }

    // German report amounts must not depend on the Windows account culture.
    // R123: the rule itself now lives in TorPos.Core.GermanFormat, shared with
    // the printed and digital receipts that had the same defect.
    private static string Money(long cents) => GermanFormat.Eur(cents);

    // Matches the format produced by the generated created_at_utc column
    // (strftime('%Y-%m-%dT%H:%M:%fZ', ...)) exactly, so a plain text WHERE
    // comparison against it is a true, DST-safe instant comparison.
    private static string ToUtcColumnText(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private static string SafeFileName(string value)
    {
        foreach (var ch in Path.GetInvalidFileNameChars())
            value = value.Replace(ch, '_');
        return value.Replace(' ', '-');
    }

    private static string Get(IReadOnlyDictionary<string,string> settings, string key) =>
        settings.TryGetValue(key, out var value) ? value : "";

    private static string InstallationEditionValue(IReadOnlyDictionary<string,string> settings) =>
        Get(settings, "business.mode").Trim().ToUpperInvariant() is "KIOSK" ? "KIOSK" : "IMBISS";
}

internal static class SimplePdfWriter
{
    public static void WriteTextReport(
        string path,
        string title,
        IReadOnlyList<string> inputLines)
    {
        var lines = new List<string> { title, "" };
        foreach (var raw in inputLines)
            lines.AddRange(Wrap(raw ?? "", 96));

        var pages = lines.Chunk(48).ToArray();
        WritePdf(path, pages.Select((page, index) => BuildTextPage(page, index + 1, pages.Length)).ToArray());
    }

    public static void WriteArticleLabels(
        string path,
        IReadOnlyList<InventoryArticleRow> rows)
    {
        const int perPage = 24;
        var pages = new List<string>();

        foreach (var chunk in rows.Chunk(perPage))
        {
            var sb = new StringBuilder();
            const double left = 30;
            const double top = 805;
            const double labelW = 178;
            const double labelH = 92;
            const double gapX = 8;
            const double gapY = 7;

            for (var i = 0; i < chunk.Length; i++)
            {
                var row = chunk[i];
                var col = i % 3;
                var r = i / 3;
                var x = left + col * (labelW + gapX);
                var y = top - (r + 1) * labelH - r * gapY;

                sb.AppendLine($"0.75 w {F(x)} {F(y)} {F(labelW)} {F(labelH)} re S");
                AddText(sb, x + 7, y + labelH - 16, 10, Truncate(row.Name, 28));
                AddText(sb, x + 7, y + labelH - 32, 15, GermanFormat.Eur(row.PriceCents));

                if (TryBuildEan13(row.Barcode, out var modules))
                {
                    var moduleW = (labelW - 14) / 95.0;
                    var barBottom = y + 22;
                    var barHeight = 28;
                    for (var m = 0; m < modules.Length; m++)
                    {
                        if (modules[m] != '1') continue;
                        sb.AppendLine($"{F(x + 7 + m * moduleW)} {F(barBottom)} {F(moduleW + 0.1)} {F(barHeight)} re f");
                    }
                    AddText(sb, x + 34, y + 8, 8, row.Barcode);
                }
                else
                {
                    AddText(sb, x + 7, y + 18, 9, string.IsNullOrWhiteSpace(row.Barcode) ? row.Sku : row.Barcode);
                }
            }
            pages.Add(sb.ToString());
        }

        if (pages.Count == 0)
        {
            pages.Add(BuildTextPage(new[] { "Keine aktiven Artikel vorhanden." }, 1, 1));
        }

        WritePdf(path, pages.ToArray());
    }

    private static string BuildTextPage(IReadOnlyList<string> lines, int page, int total)
    {
        var sb = new StringBuilder();
        var y = 800.0;
        for (var i = 0; i < lines.Count; i++)
        {
            var size = i == 0 && page == 1 ? 16 : 10;
            AddText(sb, 42, y, size, lines[i]);
            y -= i == 0 && page == 1 ? 24 : 15;
        }
        AddText(sb, 500, 22, 8, $"Seite {page}/{total}");
        return sb.ToString();
    }

    private static void AddText(StringBuilder sb, double x, double y, int size, string value)
    {
        sb.AppendLine($"BT /F1 {size} Tf {F(x)} {F(y)} Td ({EscapePdf(value)}) Tj ET");
    }

    private static void WritePdf(string path, IReadOnlyList<string> pageStreams)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
        var objects = new List<byte[]> { Array.Empty<byte>() };

        int Add(string value)
        {
            objects.Add(Encoding.Latin1.GetBytes(value));
            return objects.Count - 1;
        }

        var catalogId = Add("");
        var pagesId = Add("");
        var fontId = Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        var pageIds = new List<int>();

        foreach (var stream in pageStreams)
        {
            var bytes = Encoding.Latin1.GetBytes(stream);
            var contentId = Add($"<< /Length {bytes.Length} >>\nstream\n{stream}\nendstream");
            var pageId = Add($"<< /Type /Page /Parent {pagesId} 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 {fontId} 0 R >> >> /Contents {contentId} 0 R >>");
            pageIds.Add(pageId);
        }

        objects[catalogId] = Encoding.Latin1.GetBytes($"<< /Type /Catalog /Pages {pagesId} 0 R >>");
        objects[pagesId] = Encoding.Latin1.GetBytes($"<< /Type /Pages /Count {pageIds.Count} /Kids [{string.Join(" ", pageIds.Select(id => $"{id} 0 R"))}] >>");

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        void W(string s)
        {
            var b = Encoding.ASCII.GetBytes(s);
            fs.Write(b, 0, b.Length);
        }

        W("%PDF-1.4\n%TORPOS\n");
        var offsets = new long[objects.Count];
        for (var i = 1; i < objects.Count; i++)
        {
            offsets[i] = fs.Position;
            W($"{i} 0 obj\n");
            fs.Write(objects[i], 0, objects[i].Length);
            W("\nendobj\n");
        }

        var xref = fs.Position;
        W($"xref\n0 {objects.Count}\n");
        W("0000000000 65535 f \n");
        for (var i = 1; i < objects.Count; i++)
            W($"{offsets[i]:0000000000} 00000 n \n");
        W($"trailer\n<< /Size {objects.Count} /Root {catalogId} 0 R >>\nstartxref\n{xref}\n%%EOF");
    }

    private static IEnumerable<string> Wrap(string value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            yield return "";
            yield break;
        }
        var rest = value.Replace("\t", " ").Replace("€", "EUR");
        while (rest.Length > max)
        {
            var cut = rest.LastIndexOf(' ', max);
            if (cut < max / 2) cut = max;
            yield return rest[..cut].TrimEnd();
            rest = rest[cut..].TrimStart();
        }
        yield return rest;
    }

    private static string EscapePdf(string value) =>
        value.Replace("€", "EUR")
             .Replace("\\", "\\\\")
             .Replace("(", "\\(")
             .Replace(")", "\\)");

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool TryBuildEan13(string? raw, out string modules)
    {
        modules = "";
        var digits = new string((raw ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length == 12)
            digits += CalculateEan13Check(digits);
        if (digits.Length != 13 || digits.Any(ch => ch < '0' || ch > '9'))
            return false;
        if (CalculateEan13Check(digits[..12]) != digits[12])
            return false;

        string[] l = ["0001101","0011001","0010011","0111101","0100011","0110001","0101111","0111011","0110111","0001011"];
        string[] g = ["0100111","0110011","0011011","0100001","0011101","0111001","0000101","0010001","0001001","0010111"];
        string[] r = ["1110010","1100110","1101100","1000010","1011100","1001110","1010000","1000100","1001000","1110100"];
        string[] parity = ["LLLLLL","LLGLGG","LLGGLG","LLGGGL","LGLLGG","LGGLLG","LGGGLL","LGLGLG","LGLGGL","LGGLGL"];

        var sb = new StringBuilder("101");
        var first = digits[0] - '0';
        for (var i = 1; i <= 6; i++)
        {
            var d = digits[i] - '0';
            sb.Append(parity[first][i - 1] == 'L' ? l[d] : g[d]);
        }
        sb.Append("01010");
        for (var i = 7; i <= 12; i++)
            sb.Append(r[digits[i] - '0']);
        sb.Append("101");
        modules = sb.ToString();
        return modules.Length == 95;
    }

    private static char CalculateEan13Check(string first12)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++)
        {
            var d = first12[i] - '0';
            sum += i % 2 == 0 ? d : d * 3;
        }
        return (char)('0' + ((10 - (sum % 10)) % 10));
    }
}
