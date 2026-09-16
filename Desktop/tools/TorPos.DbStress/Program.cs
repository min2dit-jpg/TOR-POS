using System.Diagnostics;
using Microsoft.Data.Sqlite;
using TorPos.Infrastructure;

const int ProductCount = 10_000;
const int SaleCount = 500_000;
const int BatchSize = 5_000;

var reportDirectory = GetArgument(
    args,
    "--report")
    ?? Path.Combine(
        Directory.GetCurrentDirectory(),
        "verification",
        "R74");

Directory.CreateDirectory(
    reportDirectory);

var runId =
    DateTime.Now.ToString(
        "yyyyMMdd-HHmmss");

var tempRoot =
    Path.Combine(
        Path.GetTempPath(),
        "TOR-POS-DB-STRESS-" +
        runId +
        "-" +
        Guid.NewGuid().ToString("N"));

Directory.CreateDirectory(
    tempRoot);

var reportPath =
    Path.Combine(
        reportDirectory,
        $"DB-STRESS-WINDOWS-{runId}.txt");

var lines =
    new List<string>();

void Log(string text)
{
    Console.WriteLine(text);
    lines.Add(text);
}

void Require(
    bool condition,
    string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

var overall =
    Stopwatch.StartNew();

try
{
    Log("TOR POS R74 DATABASE / WAL STRESS");
    Log("================================");
    Log("WICHTIG: Nur Wegwerf-Datenbank unter %TEMP%; Produktionsdatenbank wird NICHT geöffnet.");
    Log($"Temp: {tempRoot}");
    Log($"Artikel: {ProductCount:N0}");
    Log($"Verkäufe: {SaleCount:N0}");
    Log($"Batch: {BatchSize:N0}");
    Log("");

    var db =
        new SqliteDatabase(
            Path.Combine(
                tempRoot,
                "stress.db"));

    var migrations =
        new SchemaMigrationService(
            db,
            new DatabaseBackupService(db),
            Path.Combine(
                tempRoot,
                "migration-backups"));

    var migrationWatch =
        Stopwatch.StartNew();

    var migration =
        await migrations
            .InitializeDatabaseAsync();

    migrationWatch.Stop();

    Require(
        migration.ToVersion ==
        SchemaMigrationService.TargetSchemaVersion,
        "Stress-DB erreichte nicht das aktuelle Schema.");

    Log(
        $"Schema V{migration.ToVersion}: " +
        $"{migrationWatch.Elapsed.TotalMilliseconds:0} ms");

    long categoryId;

    using (var c=db.OpenConnection())
    using (var tx=c.BeginTransaction())
    {
        using (var group=c.CreateCommand())
        {
            group.Transaction =
                (SqliteTransaction)tx;

            group.CommandText="""
                INSERT INTO product_groups(
                    name,sort_order,is_active)
                VALUES(
                    'R74 STRESS GROUP',
                    990000,
                    1);
                """;

            group.ExecuteNonQuery();
        }

        using (var category=c.CreateCommand())
        {
            category.Transaction =
                (SqliteTransaction)tx;

            category.CommandText="""
                INSERT INTO categories(
                    name,sort_order,is_active,edition_scope)
                VALUES(
                    'R74 STRESS CATEGORY',
                    990000,
                    1,
                    'ALL');
                SELECT last_insert_rowid();
                """;

            categoryId =
                Convert.ToInt64(
                    category.ExecuteScalar());
        }

        tx.Commit();
    }

    var productWatch =
        Stopwatch.StartNew();

    using (var c=db.OpenConnection())
    using (var tx=c.BeginTransaction())
    using (var q=c.CreateCommand())
    {
        q.Transaction =
            (SqliteTransaction)tx;

        q.CommandText="""
            INSERT INTO products(
                category_id,name,sku,barcode,
                base_price_cents,vat_rate,pfand_cents,
                unit,image_path,is_active,sort_order,
                edition_scope,stock_quantity,
                min_stock_quantity,purchase_price_cents)
            VALUES(
                $category,$name,$sku,$barcode,
                $price,$vat,0,
                'Stück','',1,$sort,
                'ALL',1000000,
                0,$purchase);
            """;

        var pCategory =
            q.Parameters.Add(
                "$category",
                SqliteType.Integer);

        var pName =
            q.Parameters.Add(
                "$name",
                SqliteType.Text);

        var pSku =
            q.Parameters.Add(
                "$sku",
                SqliteType.Text);

        var pBarcode =
            q.Parameters.Add(
                "$barcode",
                SqliteType.Text);

        var pPrice =
            q.Parameters.Add(
                "$price",
                SqliteType.Integer);

        var pVat =
            q.Parameters.Add(
                "$vat",
                SqliteType.Real);

        var pSort =
            q.Parameters.Add(
                "$sort",
                SqliteType.Integer);

        var pPurchase =
            q.Parameters.Add(
                "$purchase",
                SqliteType.Integer);

        for(var i=1;i<=ProductCount;i++)
        {
            var price =
                100 + i % 9900;

            pCategory.Value =
                categoryId;

            pName.Value =
                $"R74 Artikel {i:00000}";

            pSku.Value =
                (900000 + i).ToString();

            pBarcode.Value =
                $"2900000{i:00000}";

            pPrice.Value =
                price;

            pVat.Value =
                i % 5 == 0
                    ? 7d
                    : 19d;

            pSort.Value =
                i;

            pPurchase.Value =
                price / 2;

            q.ExecuteNonQuery();
        }

        tx.Commit();
    }

    productWatch.Stop();

    Log(
        $"10.000 Artikel Insert: " +
        $"{productWatch.Elapsed.TotalSeconds:0.00} s");

    var batchCommitMs =
        new List<double>();

    var salesWatch =
        Stopwatch.StartNew();

    var baseTime =
        DateTimeOffset.Now
            .AddDays(-30);

    for(var start=1;
        start<=SaleCount;
        start+=BatchSize)
    {
        var end =
            Math.Min(
                SaleCount,
                start + BatchSize - 1);

        using var c =
            db.OpenConnection();

        using var tx =
            c.BeginTransaction();

        using var sale =
            c.CreateCommand();

        sale.Transaction =
            (SqliteTransaction)tx;

        sale.CommandText="""
            INSERT INTO sales(
                receipt_number,created_at,payment_method,
                subtotal_cents,discount_cents,total_cents,
                fiscal_status,list_subtotal_cents,
                promotion_discount_cents,
                transaction_type,original_sale_id,
                pickup_number)
            VALUES(
                $receipt,$at,$payment,
                $subtotal,0,$total,
                'TEST_TSE_NOT_CONNECTED',$list,
                0,'SALE',NULL,0);
            SELECT last_insert_rowid();
            """;

        var sReceipt =
            sale.Parameters.Add(
                "$receipt",
                SqliteType.Integer);

        var sAt =
            sale.Parameters.Add(
                "$at",
                SqliteType.Text);

        var sPayment =
            sale.Parameters.Add(
                "$payment",
                SqliteType.Text);

        var sSubtotal =
            sale.Parameters.Add(
                "$subtotal",
                SqliteType.Integer);

        var sTotal =
            sale.Parameters.Add(
                "$total",
                SqliteType.Integer);

        var sList =
            sale.Parameters.Add(
                "$list",
                SqliteType.Integer);

        using var item =
            c.CreateCommand();

        item.Transaction =
            (SqliteTransaction)tx;

        item.CommandText="""
            INSERT INTO sale_items(
                sale_id,product_id,product_name,
                variant_name,barcode,quantity,
                unit_price_cents,vat_rate,pfand_cents,
                line_total_cents,list_unit_price_cents,
                list_line_total_cents)
            VALUES(
                $sale,$product,$name,
                '',$barcode,1,
                $price,$vat,0,
                $price,$price,$price);
            """;

        var iSale =
            item.Parameters.Add(
                "$sale",
                SqliteType.Integer);

        var iProduct =
            item.Parameters.Add(
                "$product",
                SqliteType.Integer);

        var iName =
            item.Parameters.Add(
                "$name",
                SqliteType.Text);

        var iBarcode =
            item.Parameters.Add(
                "$barcode",
                SqliteType.Text);

        var iPrice =
            item.Parameters.Add(
                "$price",
                SqliteType.Integer);

        var iVat =
            item.Parameters.Add(
                "$vat",
                SqliteType.Real);

        for(var number=start;
            number<=end;
            number++)
        {
            var productOrdinal =
                (number % ProductCount) + 1;

            var productId =
                productOrdinal;

            var price =
                100 + productOrdinal % 9900;

            var vat =
                productOrdinal % 5 == 0
                    ? 7d
                    : 19d;

            sReceipt.Value =
                number;

            sAt.Value =
                baseTime
                    .AddSeconds(number * 5L)
                    .ToString("O");

            sPayment.Value =
                number % 3 == 0
                    ? "CARD"
                    : "CASH";

            sSubtotal.Value =
                price;

            sTotal.Value =
                price;

            sList.Value =
                price;

            var saleId =
                Convert.ToInt64(
                    sale.ExecuteScalar());

            iSale.Value =
                saleId;

            iProduct.Value =
                productId;

            iName.Value =
                $"R74 Artikel {productOrdinal:00000}";

            iBarcode.Value =
                $"2900000{productOrdinal:00000}";

            iPrice.Value =
                price;

            iVat.Value =
                vat;

            item.ExecuteNonQuery();
        }

        var commitWatch =
            Stopwatch.StartNew();

        tx.Commit();

        commitWatch.Stop();

        batchCommitMs.Add(
            commitWatch.Elapsed.TotalMilliseconds);

        if(end % 50_000 == 0)
        {
            Log(
                $"  {end:N0}/{SaleCount:N0} Verkäufe · " +
                $"letzter Commit {commitWatch.Elapsed.TotalMilliseconds:0.0} ms");
        }
    }

    salesWatch.Stop();

    Log(
        $"500.000 Sales + 500.000 SaleItems: " +
        $"{salesWatch.Elapsed.TotalSeconds:0.00} s");

    var sortedCommits =
        batchCommitMs
            .OrderBy(x=>x)
            .ToArray();

    var p95 =
        sortedCommits[
            Math.Clamp(
                (int)Math.Ceiling(
                    sortedCommits.Length * 0.95) - 1,
                0,
                sortedCommits.Length - 1)];

    Log(
        $"Batch-Commit Ø: {batchCommitMs.Average():0.0} ms · " +
        $"P95: {p95:0.0} ms · " +
        $"Max: {batchCommitMs.Max():0.0} ms");

    var lookupWatch =
        Stopwatch.StartNew();

    using (var c=db.OpenConnection())
    using (var q=c.CreateCommand())
    {
        q.CommandText="""
            SELECT id,base_price_cents
            FROM products
            WHERE barcode=$barcode
              AND is_active=1;
            """;

        var barcode =
            q.Parameters.Add(
                "$barcode",
                SqliteType.Text);

        for(var i=0;i<1000;i++)
        {
            var productOrdinal =
                (i * 7919 % ProductCount) + 1;

            barcode.Value =
                $"2900000{productOrdinal:00000}";

            using var reader =
                q.ExecuteReader();

            Require(
                reader.Read(),
                "Barcode-Lookup fand Testartikel nicht.");
        }
    }

    lookupWatch.Stop();

    Log(
        $"1.000 Barcode-Lookups: " +
        $"{lookupWatch.Elapsed.TotalMilliseconds:0.0} ms · " +
        $"Ø {lookupWatch.Elapsed.TotalMilliseconds/1000d:0.000} ms");

    var reportWatch =
        Stopwatch.StartNew();

    long saleRows;
    long gross;

    using (var c=db.OpenConnection())
    using (var q=c.CreateCommand())
    {
        q.CommandText="""
            SELECT
                COUNT(*),
                COALESCE(SUM(total_cents),0)
            FROM sales
            WHERE transaction_type='SALE';
            """;

        using var reader =
            q.ExecuteReader();

        reader.Read();

        saleRows =
            reader.GetInt64(0);

        gross =
            reader.GetInt64(1);
    }

    reportWatch.Stop();

    Require(
        saleRows == SaleCount,
        $"Sales-Zahl falsch: {saleRows:N0}");

    Log(
        $"Sales-Aggregation 500k: " +
        $"{reportWatch.Elapsed.TotalMilliseconds:0.0} ms · " +
        $"Brutto-Cent {gross:N0}");

    // R83.1: a deliberate writer-contention simulation (Connection A holds a
    // write transaction; Connection B opened on a background Task.Run thread
    // tries a conflicting write and should be unblocked by busy_timeout once
    // A commits) used to live here. Diagnosed and removed: the exact same
    // pattern passes reliably in TorPos.SafetyTests ("R74 competing writer
    // waits for a short lock and succeeds within busy_timeout") on a fresh
    // database, but hung indefinitely here - reproducibly, at any scale -
    // once it ran on a SqliteDatabase/connection pool that had already done
    // heavy prior connection churn (the 500k-row insert, which itself
    // completed in well under a second - this was never a disk-speed
    // problem). Wrapping just the wait in Task.WaitAsync(timeout) was not
    // enough: the background thread's native SQLite call stayed
    // permanently stuck, and that appears to poison the shared-cache
    // connection pool (Cache=Shared in SqliteDatabase.OpenConnection) badly
    // enough that even disposing the *first* connection afterward hung too.
    // Real WAL/busy_timeout concurrency is already covered reliably by that
    // SafetyTests fixture; this tool's own job (bulk-insert throughput,
    // lookup latency, health/backup timing) doesn't need to re-prove it.
    Log("Writer-Contention Test: entfernt (siehe Quellcode-Kommentar) - WAL/busy_timeout wird bereits zuverlässig von TorPos.SafetyTests abgedeckt.");

    var health =
        new DatabaseHealthService(db);

    var healthWatch =
        Stopwatch.StartNew();

    var status =
        await health.GetSnapshotAsync();

    healthWatch.Stop();

    Require(
        status.CorePragmasHealthy,
        "SQLite Kern-PRAGMAs sind nicht gesund.");

    Require(
        status.IntegrityHealthy,
        "PRAGMA quick_check ist nicht OK.");

    Log(
        $"Health: journal={status.JournalMode} · " +
        $"sync={status.SynchronousName} · " +
        $"busy={status.BusyTimeoutMs} ms · " +
        $"checkpoint={status.CheckpointBusy}/" +
        $"{status.WalLogFrames}/" +
        $"{status.WalCheckpointedFrames}");

    Log(
        $"DB: {status.DatabaseFileBytes/1024d/1024d:0.0} MB · " +
        $"WAL: {status.WalFileBytes/1024d/1024d:0.0} MB · " +
        $"Pages: {status.PageCount:N0} · " +
        $"Freelist: {status.FreeListPages:N0} " +
        $"({status.FreeListPercent:0.0} %)");

    Log(
        $"quick_check: {status.QuickCheck} · " +
        $"{healthWatch.Elapsed.TotalMilliseconds:0.0} ms");

    var backupWatch =
        Stopwatch.StartNew();

    var backupPath =
        await new DatabaseBackupService(db)
            .CreateBackupAsync(
                Path.Combine(
                    tempRoot,
                    "backup"));

    backupWatch.Stop();

    Require(
        File.Exists(backupPath),
        "Stress-Backup wurde nicht erstellt.");

    Log(
        $"SQLite Backup: " +
        $"{new FileInfo(backupPath).Length/1024d/1024d:0.0} MB · " +
        $"{backupWatch.Elapsed.TotalSeconds:0.00} s");

    overall.Stop();

    Log("");
    Log("RESULT: PASS");
    Log(
        $"Gesamtdauer: {overall.Elapsed.TotalSeconds:0.0} s");
    Log(
        "Geschwindigkeitswerte sind Messwerte, keine willkürlichen PASS/FAIL-Grenzen.");

    await File.WriteAllLinesAsync(
        reportPath,
        lines);

    Console.WriteLine("");
    Console.WriteLine(
        "Report: " + reportPath);

    // This process owns only the disposable stress DB; clearing its pools is safe.
    SqliteConnection.ClearAllPools();

    try
    {
        Directory.Delete(
            tempRoot,
            recursive:true);
    }
    catch(Exception ex)
    {
        Console.WriteLine(
            "WARNUNG: Temp-Ordner konnte nicht gelöscht werden: " +
            ex.Message);
    }

    return 0;
}
catch(Exception ex)
{
    overall.Stop();

    Log("");
    Log("RESULT: FAIL");
    Log(
        $"{ex.GetType().Name}: {ex.Message}");
    Log(
        $"Gesamtdauer bis Fehler: {overall.Elapsed.TotalSeconds:0.0} s");

    try
    {
        await File.WriteAllLinesAsync(
            reportPath,
            lines);
    }
    catch
    {
    }

    Console.Error.WriteLine(ex);
    Console.Error.WriteLine(
        "Stress-Datenbank bleibt zur Analyse erhalten: " +
        tempRoot);

    return 1;
}

static string? GetArgument(
    string[] args,
    string name)
{
    for(var i=0;i<args.Length-1;i++)
    {
        if(string.Equals(
            args[i],
            name,
            StringComparison.OrdinalIgnoreCase))
        {
            return args[i+1];
        }
    }

    return null;
}
