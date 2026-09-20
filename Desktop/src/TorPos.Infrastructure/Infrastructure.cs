using System.Collections.Frozen;
using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

internal static class VatAllocationStorage
{
    public static string Serialize(CartLine line) =>
        line.VatAllocations.Length == 0
            ? ""
            : System.Text.Json.JsonSerializer.Serialize(line.VatAllocations);

    public static MenuVatAllocation[] Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<MenuVatAllocation>();

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<MenuVatAllocation[]>(json)
                ?? Array.Empty<MenuVatAllocation>();
        }
        catch
        {
            // Old/corrupt optional allocation data must never invent VAT.
            // Downstream validation will fall back to the stored scalar rate
            // only when no allocation snapshot exists.
            return Array.Empty<MenuVatAllocation>();
        }
    }
}

internal static class MenuComponentStorage
{
    public static string Serialize(CartLine line) =>
        line.MenuComponents.Length == 0
            ? ""
            : System.Text.Json.JsonSerializer.Serialize(line.MenuComponents);

    public static MenuComponentSnapshot[] Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<MenuComponentSnapshot>();

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<MenuComponentSnapshot[]>(json)
                ?? Array.Empty<MenuComponentSnapshot>();
        }
        catch
        {
            return Array.Empty<MenuComponentSnapshot>();
        }
    }
}

public static class AppPaths
{
    /// <summary>
    /// R126: lets tools/TorPos.UiSnapshot render real windows against a
    /// throwaway folder. On Windows %APPDATA% cannot be redirected through the
    /// environment (GetFolderPath asks the shell, not the variable), so without
    /// this a screenshot run on a developer PC that also runs TOR POS would
    /// read and write that PC's real till data. Internal and only visible to
    /// that tool - nothing in the shipped application sets it.
    /// </summary>
    internal static string? DataDirectoryOverride { get; set; }

    public static string DataDirectory
    {
        get
        {
            var basePath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var path = DataDirectoryOverride ?? Path.Combine(basePath, "TOR-POS-Pro");
            Directory.CreateDirectory(path);
            Directory.CreateDirectory(Path.Combine(path, "ProductImages"));
            Directory.CreateDirectory(Path.Combine(path, "ReceiptAssets"));
            Directory.CreateDirectory(Path.Combine(path, "Backups"));
            Directory.CreateDirectory(Path.Combine(path, "Logs"));
            Directory.CreateDirectory(Path.Combine(path, "Updates"));
            return path;
        }
    }

    public static string DatabasePath => Path.Combine(DataDirectory, "torpos.db");
    public static string ProductImagesPath => Path.Combine(DataDirectory, "ProductImages");
    public static string ReceiptAssetsPath => Path.Combine(DataDirectory, "ReceiptAssets");
    public static string BackupsPath => Path.Combine(DataDirectory, "Backups");
    public static string UpdatesPath => Path.Combine(DataDirectory, "Updates");
    /// <summary>R133: TSE TAR exports TOR creates itself (after activation), kept with the till's data.</summary>
    public static string TseExportsPath => Path.Combine(DataDirectory, "TseExports");
    public static string EditionLockPath => Path.Combine(DataDirectory, "edition.lock");
    public static string FirstRunAdminPath => Path.Combine(DataDirectory, "first-run-admin.cfg");
    public static string SecurityInitializedPath => Path.Combine(DataDirectory, "security.initialized");
    public static string CommercialLicensePath => Path.Combine(DataDirectory, "commercial-license.json");
    public static string LicenseDeactivationJournalPath => Path.Combine(DataDirectory, "commercial-license-deactivations.jsonl");
    public static string LicenseInstallationIdPath => Path.Combine(DataDirectory, "license-installation.id");
    public static string TrialStatePath => Path.Combine(DataDirectory, "trial-state.json");
    public static string TrialIdentityDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "TOR-POS-Pro");
    public static string TrialIdentityPath =>
        Path.Combine(TrialIdentityDirectory, "trial-installation.id");
}

public sealed class SqliteDatabase
{
    private readonly string _connectionString;

    public string DatabasePath { get; }

    public SqliteDatabase(string? path = null)
    {
        path ??= AppPaths.DatabasePath;
        DatabasePath = Path.GetFullPath(path);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public SqliteConnection OpenConnection()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();

        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA foreign_keys=ON;
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA temp_store=MEMORY;
            PRAGMA busy_timeout=3000;
            """;
        cmd.ExecuteNonQuery();
        return c;
    }
public async Task InitializeAsync(CancellationToken ct = default)
{
    await IoQueue.RunAsync(async () =>
    {
        await using var c = OpenConnection();
        using(var cloudSchema=c.CreateCommand()){cloudSchema.CommandText=TorCloudOutbox.Schema;cloudSchema.ExecuteNonQuery();}
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS product_groups(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              name TEXT NOT NULL UNIQUE,
              sort_order INTEGER NOT NULL DEFAULT 0,
              is_active INTEGER NOT NULL DEFAULT 1);

            CREATE TABLE IF NOT EXISTS categories(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              name TEXT NOT NULL UNIQUE,
              sort_order INTEGER NOT NULL DEFAULT 0,
              is_active INTEGER NOT NULL DEFAULT 1,
              edition_scope TEXT NOT NULL DEFAULT 'ALL');

            CREATE TABLE IF NOT EXISTS category_master_data(
              category_id INTEGER PRIMARY KEY,
              group_id INTEGER NOT NULL,
              vat_rate REAL NOT NULL DEFAULT 19,
              FOREIGN KEY(category_id) REFERENCES categories(id) ON DELETE CASCADE,
              FOREIGN KEY(group_id) REFERENCES product_groups(id));

            CREATE TABLE IF NOT EXISTS category_visual_data(
              category_id INTEGER PRIMARY KEY,
              tile_color TEXT NOT NULL DEFAULT '#17466A',
              FOREIGN KEY(category_id) REFERENCES categories(id) ON DELETE CASCADE);

            CREATE TABLE IF NOT EXISTS category_kitchen_data(
              category_id INTEGER PRIMARY KEY,
              station TEXT NOT NULL DEFAULT '',
              FOREIGN KEY(category_id) REFERENCES categories(id) ON DELETE CASCADE);

            CREATE TABLE IF NOT EXISTS products(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              category_id INTEGER NOT NULL,
              name TEXT NOT NULL,
              sku TEXT NOT NULL DEFAULT '',
              barcode TEXT NOT NULL DEFAULT '',
              base_price_cents INTEGER NOT NULL,
              vat_rate REAL NOT NULL,
              pfand_cents INTEGER NOT NULL DEFAULT 0,
              unit TEXT NOT NULL DEFAULT 'Stück',
              image_path TEXT NOT NULL DEFAULT '',
              is_active INTEGER NOT NULL DEFAULT 1,
              sort_order INTEGER NOT NULL DEFAULT 0,
              edition_scope TEXT NOT NULL DEFAULT 'ALL',
              FOREIGN KEY(category_id) REFERENCES categories(id));

            CREATE TABLE IF NOT EXISTS product_variants(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              product_id INTEGER NOT NULL,
              name TEXT NOT NULL,
              price_cents INTEGER NOT NULL,
              sort_order INTEGER NOT NULL DEFAULT 0,
              is_active INTEGER NOT NULL DEFAULT 1,
              FOREIGN KEY(product_id) REFERENCES products(id) ON DELETE CASCADE);

            CREATE TABLE IF NOT EXISTS product_combo_items(
              product_id INTEGER NOT NULL,
              component_product_id INTEGER NOT NULL,
              quantity REAL NOT NULL DEFAULT 1,
              sort_order INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY(product_id,component_product_id),
              FOREIGN KEY(product_id) REFERENCES products(id) ON DELETE CASCADE,
              FOREIGN KEY(component_product_id) REFERENCES products(id));

            CREATE TABLE IF NOT EXISTS extras(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              category_id INTEGER NOT NULL,
              name TEXT NOT NULL UNIQUE COLLATE NOCASE,
              price_cents INTEGER NOT NULL,
              sort_order INTEGER NOT NULL DEFAULT 0,
              is_active INTEGER NOT NULL DEFAULT 1,
              FOREIGN KEY(category_id) REFERENCES categories(id));

            CREATE TABLE IF NOT EXISTS app_sequence(
              key TEXT PRIMARY KEY,
              value INTEGER NOT NULL);

            CREATE TABLE IF NOT EXISTS app_settings(
              key TEXT PRIMARY KEY,
              value TEXT NOT NULL);

            CREATE TABLE IF NOT EXISTS users(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              username TEXT NOT NULL UNIQUE COLLATE NOCASE,
              password_hash TEXT NOT NULL,
              password_salt TEXT NOT NULL,
              pin_hash TEXT NOT NULL,
              pin_salt TEXT NOT NULL,
              role TEXT NOT NULL DEFAULT 'ADMIN',
              is_admin INTEGER NOT NULL DEFAULT 0,
              is_active INTEGER NOT NULL DEFAULT 1,
              must_change_password INTEGER NOT NULL DEFAULT 0,
              failed_attempts INTEGER NOT NULL DEFAULT 0,
              last_login_at TEXT NOT NULL DEFAULT '',
              created_at TEXT NOT NULL);

            CREATE TABLE IF NOT EXISTS user_permissions(
              user_id INTEGER PRIMARY KEY,
              permissions INTEGER NOT NULL DEFAULT 0,
              credentials_configured INTEGER NOT NULL DEFAULT 0,
              FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE);


            CREATE TABLE IF NOT EXISTS system_identity(
              id INTEGER PRIMARY KEY CHECK(id=1),
              eas_serial TEXT NOT NULL UNIQUE,
              manufacturer TEXT NOT NULL,
              model TEXT NOT NULL,
              created_at TEXT NOT NULL);

            CREATE TRIGGER IF NOT EXISTS trg_system_identity_serial_immutable
            BEFORE UPDATE OF eas_serial ON system_identity
            BEGIN
              SELECT RAISE(ABORT,'eAS serial is immutable');
            END;

            CREATE TRIGGER IF NOT EXISTS trg_system_identity_no_delete
            BEFORE DELETE ON system_identity
            BEGIN
              SELECT RAISE(ABORT,'system identity cannot be deleted');
            END;

            CREATE TABLE IF NOT EXISTS audit_log(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              created_at TEXT NOT NULL,
              actor TEXT NOT NULL,
              event_type TEXT NOT NULL,
              entity_type TEXT NOT NULL,
              entity_id TEXT NOT NULL,
              details TEXT NOT NULL DEFAULT '');

            CREATE TRIGGER IF NOT EXISTS trg_audit_no_update
            BEFORE UPDATE ON audit_log
            BEGIN
              SELECT RAISE(ABORT,'audit log is append-only');
            END;

            CREATE TRIGGER IF NOT EXISTS trg_audit_no_delete
            BEFORE DELETE ON audit_log
            BEGIN
              SELECT RAISE(ABORT,'audit log is append-only');
            END;

            CREATE TABLE IF NOT EXISTS cash_movements(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              created_at TEXT NOT NULL,
              movement_type TEXT NOT NULL,
              amount_cents INTEGER NOT NULL,
              reason TEXT NOT NULL,
              actor TEXT NOT NULL,
              fiscal_mode TEXT NOT NULL DEFAULT 'TEST_ONLY');

            CREATE TRIGGER IF NOT EXISTS trg_cash_movements_no_update
            BEFORE UPDATE ON cash_movements
            BEGIN
              SELECT RAISE(ABORT,'cash movements are append-only');
            END;

            CREATE TRIGGER IF NOT EXISTS trg_cash_movements_no_delete
            BEFORE DELETE ON cash_movements
            BEGIN
              SELECT RAISE(ABORT,'cash movements are append-only');
            END;

            CREATE TABLE IF NOT EXISTS tse_outage_log(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              started_at TEXT NOT NULL,
              ended_at TEXT NOT NULL DEFAULT '',
              reason TEXT NOT NULL,
              actor TEXT NOT NULL DEFAULT 'SYSTEM',
              state TEXT NOT NULL DEFAULT 'OPEN');

            CREATE TRIGGER IF NOT EXISTS trg_tse_outage_no_delete
            BEFORE DELETE ON tse_outage_log
            BEGIN
              SELECT RAISE(ABORT,'TSE outage history cannot be deleted');
            END;

            CREATE INDEX IF NOT EXISTS ix_users_active
              ON users(is_active,username);

            CREATE TABLE IF NOT EXISTS sales(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              receipt_number INTEGER NOT NULL UNIQUE,
              created_at TEXT NOT NULL,
              payment_method TEXT NOT NULL,
              subtotal_cents INTEGER NOT NULL,
              discount_cents INTEGER NOT NULL DEFAULT 0,
              total_cents INTEGER NOT NULL,
              fiscal_status TEXT NOT NULL DEFAULT 'TEST_TSE_NOT_CONNECTED');

            CREATE TABLE IF NOT EXISTS checkout_operations(
              id TEXT PRIMARY KEY,
              state TEXT NOT NULL CHECK(state IN ('PREPARED','SENT','UNKNOWN','APPROVED','CASH_READY','COMMITTED','NOT_CHARGED')),
              snapshot TEXT NOT NULL,
              evidence TEXT NOT NULL DEFAULT '',
              updated_at TEXT NOT NULL,
              sale_id INTEGER UNIQUE REFERENCES sales(id));

            CREATE UNIQUE INDEX IF NOT EXISTS ux_checkout_one_open
              ON checkout_operations((1)) WHERE state NOT IN ('COMMITTED','NOT_CHARGED');

            CREATE TABLE IF NOT EXISTS sale_items(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              sale_id INTEGER NOT NULL,
              product_id INTEGER NOT NULL,
              product_name TEXT NOT NULL,
              variant_name TEXT NOT NULL DEFAULT '',
              barcode TEXT NOT NULL DEFAULT '',
              quantity REAL NOT NULL,
              unit_price_cents INTEGER NOT NULL,
              vat_rate REAL NOT NULL,
              pfand_cents INTEGER NOT NULL DEFAULT 0,
              line_total_cents INTEGER NOT NULL,
              FOREIGN KEY(sale_id) REFERENCES sales(id) ON DELETE CASCADE);

            CREATE TABLE IF NOT EXISTS sale_operators(
              sale_id INTEGER PRIMARY KEY,
              operator_name TEXT NOT NULL,
              FOREIGN KEY(sale_id) REFERENCES sales(id));

            CREATE TABLE IF NOT EXISTS daily_closings(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              closed_at TEXT NOT NULL,
              operator_name TEXT NOT NULL,
              close_type TEXT NOT NULL DEFAULT 'Z_REPORT');

            CREATE TABLE IF NOT EXISTS z_report_archive(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              z_number INTEGER NOT NULL UNIQUE,
              created_at TEXT NOT NULL,
              period_from TEXT NOT NULL,
              period_to TEXT NOT NULL,
              operator_name TEXT NOT NULL,
              receipt_count INTEGER NOT NULL,
              gross_cents INTEGER NOT NULL,
              cash_cents INTEGER NOT NULL,
              card_cents INTEGER NOT NULL,
              vat7_gross_cents INTEGER NOT NULL DEFAULT 0,
              vat19_gross_cents INTEGER NOT NULL DEFAULT 0,
              fiscal_status TEXT NOT NULL,
              snapshot_text TEXT NOT NULL);

            CREATE TRIGGER IF NOT EXISTS trg_z_report_archive_no_update
            BEFORE UPDATE ON z_report_archive
            BEGIN
              SELECT RAISE(ABORT,'Z report archive is immutable');
            END;

            CREATE TRIGGER IF NOT EXISTS trg_z_report_archive_no_delete
            BEFORE DELETE ON z_report_archive
            BEGIN
              SELECT RAISE(ABORT,'Z report archive cannot be deleted');
            END;

            CREATE TRIGGER IF NOT EXISTS trg_sales_no_update
            BEFORE UPDATE ON sales
            BEGIN
              SELECT RAISE(ABORT,'completed sales are immutable');
            END;

            CREATE TRIGGER IF NOT EXISTS trg_sales_no_delete
            BEFORE DELETE ON sales
            BEGIN
              SELECT RAISE(ABORT,'completed sales cannot be deleted');
            END;

            CREATE TRIGGER IF NOT EXISTS trg_sale_items_no_update
            BEFORE UPDATE ON sale_items
            BEGIN
              SELECT RAISE(ABORT,'completed sale items are immutable');
            END;

            CREATE TRIGGER IF NOT EXISTS trg_sale_items_no_delete
            BEFORE DELETE ON sale_items
            BEGIN
              SELECT RAISE(ABORT,'completed sale items cannot be deleted');
            END;

            CREATE TRIGGER IF NOT EXISTS trg_sale_operators_no_update
            BEFORE UPDATE ON sale_operators
            BEGIN
              SELECT RAISE(ABORT,'sale operator is immutable');
            END;

            CREATE TRIGGER IF NOT EXISTS trg_sale_operators_no_delete
            BEFORE DELETE ON sale_operators
            BEGIN
              SELECT RAISE(ABORT,'sale operator cannot be deleted');
            END;

            CREATE TRIGGER IF NOT EXISTS trg_daily_closings_no_update
            BEFORE UPDATE ON daily_closings
            BEGIN
              SELECT RAISE(ABORT,'daily closing is immutable');
            END;

            CREATE TRIGGER IF NOT EXISTS trg_daily_closings_no_delete
            BEFORE DELETE ON daily_closings
            BEGIN
              SELECT RAISE(ABORT,'daily closing cannot be deleted');
            END;


            CREATE TABLE IF NOT EXISTS parked_receipts(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              park_number INTEGER NOT NULL UNIQUE,
              pickup_number INTEGER NOT NULL DEFAULT 0,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              created_by TEXT NOT NULL DEFAULT '',
              subtotal_cents INTEGER NOT NULL,
              discount_cents INTEGER NOT NULL DEFAULT 0,
              total_cents INTEGER NOT NULL,
              status TEXT NOT NULL DEFAULT 'OPEN',
              is_training INTEGER NOT NULL DEFAULT 0,
              cashed_sale_id INTEGER NULL,
              cashed_at TEXT NOT NULL DEFAULT '',
              FOREIGN KEY(cashed_sale_id) REFERENCES sales(id));

            CREATE TABLE IF NOT EXISTS parked_receipt_items(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              parked_receipt_id INTEGER NOT NULL,
              product_id INTEGER NOT NULL,
              product_name TEXT NOT NULL,
              variant_name TEXT NOT NULL DEFAULT '',
              barcode TEXT NOT NULL DEFAULT '',
              quantity REAL NOT NULL,
              unit_price_cents INTEGER NOT NULL,
              vat_rate REAL NOT NULL,
              pfand_cents INTEGER NOT NULL DEFAULT 0,
              line_total_cents INTEGER NOT NULL,
              FOREIGN KEY(parked_receipt_id) REFERENCES parked_receipts(id) ON DELETE CASCADE);

            CREATE INDEX IF NOT EXISTS ix_parked_receipts_status
              ON parked_receipts(status,created_at);

            CREATE INDEX IF NOT EXISTS ix_parked_receipt_items_parent
              ON parked_receipt_items(parked_receipt_id);

            CREATE UNIQUE INDEX IF NOT EXISTS ux_products_barcode
              ON products(barcode) WHERE barcode <> '';
            CREATE INDEX IF NOT EXISTS ix_groups_active
              ON product_groups(is_active,sort_order,name);
            CREATE INDEX IF NOT EXISTS ix_category_master_group
              ON category_master_data(group_id,category_id);
            CREATE INDEX IF NOT EXISTS ix_products_category_active
              ON products(category_id,is_active,sort_order);
            CREATE INDEX IF NOT EXISTS ix_variants_product
              ON product_variants(product_id,is_active,sort_order);
            CREATE INDEX IF NOT EXISTS ix_combo_component
              ON product_combo_items(component_product_id);
            CREATE INDEX IF NOT EXISTS ix_extras_active
              ON extras(is_active,sort_order,name);
            CREATE INDEX IF NOT EXISTS ix_sales_created
              ON sales(created_at);
            CREATE INDEX IF NOT EXISTS ix_z_report_archive_created
              ON z_report_archive(created_at,z_number);

            INSERT OR IGNORE INTO app_sequence(key,value) VALUES('receipt',0);
            INSERT OR IGNORE INTO app_sequence(key,value) VALUES('parked_receipt',0);
            INSERT OR IGNORE INTO app_sequence(key,value) VALUES('z_report',0);
            INSERT OR IGNORE INTO app_sequence(key,value) VALUES('article_number',99999);

            INSERT OR IGNORE INTO system_identity(
              id,eas_serial,manufacturer,model,created_at)
            VALUES(
              1,
              'TORPOS-' || UPPER(hex(randomblob(16))),
              'TOR Kassensysteme',
              'TOR POS Pro',
              datetime('now'));


            INSERT OR IGNORE INTO app_settings(key,value) VALUES
              ('app.version','0.7.33'),
              ('cash.register.number','1'),
              ('cash.register.name','Kasse 1'),
              ('business.mode','IMBISS'),
              ('startup.view','KASSE'),
              ('ui.scale','AUTO'),
              ('ui.theme','DUNKEL'),
              ('imbiss.order.number_enabled','true'),
              ('ui.product.columns','4'),
              ('ui.product.rows','10'),
              ('ui.category.columns','4'),
              ('ui.category.rows','6'),
              ('ui.touch.font_size','18'),
              ('ui.product.show_images','true'),
              ('ui.keyboard.auto','true'),
              ('currency.name','Euro'),
              ('currency.symbol','EUR'),
              ('cash.start.cents','10000'),
              ('function.single_operator','false'),
              ('function.login_required','true'),
              ('function.free_price','true'),
              ('imbiss.pickup_number.enabled','true'),
              ('imbiss.pickup_number.mode','SALE'),
              ('imbiss.pickup_slip.auto_print','true'),
              ('function.pfand_buttons','true'),
              ('pfand.direct.8.cent','8'),
              ('pfand.direct.15.cent','15'),
              ('pfand.direct.25.cent','25'),
              ('pfand.crate.empty.cent','150'),
              ('pfand.crate.full.cent','330'),
              ('function.low_stock','true'),
              ('function.low_stock_threshold','5'),
              ('function.z_auto_print','false'),
              ('function.operator_on_receipt','true'),
              ('function.options_on_receipt','true'),
              ('function.drawer_on_receipt','true'),
              ('function.payment_query','false'),
              ('function.storno_reasons','Fehlbuchung|Doppelte Ware|Umtausch|Nicht abgeholt|Bruch'),
              ('function.bon_storno_reasons','Kunde reklamiert|Fehlbon|Doppelte Erfassung|Preisirrtum|Sonstiger Grund'),
              ('company.name','TOR POS Testbetrieb'),
              ('company.owner',''),
              ('company.street',''),
              ('company.zip',''),
              ('company.city',''),
              ('company.phone',''),
              ('company.email',''),
              ('company.tax_no',''),
              ('company.vat_id',''),
              ('company.show_on_receipt','true'),
              ('company.show_on_reports','true'),
              ('pay.cash.enabled','true'),
              ('pay.card.enabled','true'),
              ('pay.invoice.enabled','false'),
              ('pay.cash.label','Bar'),
              ('pay.card.label','Karte'),
              ('pay.invoice.label','Auf Rechnung'),
              ('pay.quick.default','AUS'),
              ('pay.quick.cash_exact','false'),
              ('payment.terminal.enabled','false'),
              ('payment.terminal.protocol','ZVT_TCP'),
              ('payment.terminal.vendor','AUTO_ZVT'),
              ('payment.terminal.model',''),
              ('payment.terminal.ip',''),
              ('payment.terminal.port','20007'),
              ('payment.terminal.connect_timeout_seconds','5'),
              ('payment.terminal.command_timeout_seconds','120'),
              ('payment.terminal.register_before_payment','true'),
              ('payment.terminal.require_success','true'),
              ('payment.terminal.last_test',''),
              ('payment.terminal.last_status','NICHT_KONFIGURIERT'),
              ('payment.terminal.last_error',''),
              ('tax.standard','19'),
              ('tax.reduced','7'),
              ('tax.default','19'),
              ('tax.datev.standard.account',''),
              ('tax.datev.standard.counter',''),
              ('tax.datev.standard.code',''),
              ('tax.datev.reduced.account',''),
              ('tax.datev.reduced.counter',''),
              ('tax.datev.reduced.code',''),
              ('receipt.header',''),
              ('receipt.logo_path',''),
              ('receipt.footer','Vielen Dank für Ihren Einkauf!'),
              ('receipt.alignment','ZENTRIERT'),
              ('receipt.font_width','32'),
              ('receipt.auto_print','true'),
              ('receipt.last_receipt_enabled','true'),
              ('receipt.tse_qr_code.enabled','false'),
              ('device.receipt_printer.enabled','false'),
              ('device.receipt_printer.name',''),
              ('device.receipt_printer.model','Star mC-Print3 MCP31CBI'),
              ('device.receipt_printer.driver_mode','STAR_WINDOWS_DRIVER'),
              ('device.receipt_printer.paper_width_mm','80'),
              ('device.receipt_printer.auto_detect','true'),
              ('device.receipt_printer.autocut_driver','true'),
              ('device.receipt_printer.last_test',''),
              ('device.receipt_printer.last_error',''),
              ('device.kitchen_printer.enabled','false'),
              ('device.kitchen_printer.name',''),
              ('device.kitchen_printer.auto_print','true'),
              ('device.kitchen_printer.station.grill.enabled','false'),
              ('device.kitchen_printer.station.grill.name',''),
              ('device.kitchen_printer.station.fritteuse.enabled','false'),
              ('device.kitchen_printer.station.fritteuse.name',''),
              ('device.kitchen_printer.station.getraenke.enabled','false'),
              ('device.kitchen_printer.station.getraenke.name',''),
              ('device.drawer.enabled','false'),
              ('device.drawer.via_printer','true'),
              ('device.customer_display.enabled','false'),
              ('device.customer_display.port',''),
              ('device.customer_display.screen_index','0'),
              ('order_display.enabled','false'),
              ('order_display.screen_index','0'),
              ('order_display.refresh_seconds','2'),
              ('device.a4_printer.enabled','false'),
              ('device.a4_printer.name',''),
              ('scanner.mode','HID'),
              ('scanner.enter_suffix','true'),
              ('scanner.wait_ms','1000'),
              ('scanner.unknown_dialog','true'),
              ('scanner.unknown_beep','true'),
              ('backup.on_exit','true'),
              ('backup.directory',''),
              ('backup.keep_count','30'),
              ('backup.daily.enabled','true'),
              ('backup.daily.time','00:00'),
              ('backup.daily.last_success',''),
              ('backup.daily.last_error',''),
              ('backup.daily.last_path',''),
              ('reports.email.monthly.enabled','false'),
              ('reports.email.recipient',''),
              ('reports.email.sender',''),
              ('reports.email.smtp.host',''),
              ('reports.email.smtp.port','587'),
              ('reports.email.smtp.ssl','true'),
              ('reports.email.smtp.user',''),
              ('reports.email.smtp.password_protected',''),
              ('reports.email.monthly.day','1'),
              ('reports.email.monthly.time','00:15'),
              ('reports.email.monthly.last_period',''),
              ('reports.email.monthly.last_success',''),
              ('reports.email.monthly.last_error',''),
              ('reports.email.monthly.last_folder',''),
              ('permission.settings_admin','true'),
              ('permission.storno_admin','true'),
              ('permission.z_admin','true'),
              ('permission.discount_admin','false'),
              ('permission.max_discount_percent','20'),
              ('tse.status','NICHT_EINGERICHTET'),
              ('tse.provider','SWISSBIT_HARDWARE'),
              ('tse.type','USB'),
              ('tse.product_family','HARDWARE_TSE_2'),
              ('tse.compatibility_profile','SWISSBIT_UNIFIED_SDK'),
              ('tse.serial',''),
              ('tse.bsi_id',''),
              ('tse.activation_date',''),
              ('tse.expiry_date',''),
              ('tse.client_id',''),
              ('tse.device_path',''),
              ('tse.auto_connect','true'),
              ('tse.last_test',''),
              ('tse.last_error',''),
              ('legal.mode','TEST_ONLY'),
              ('legal.dsfinvk.version','2.4'),
              ('legal.kassenmeldung.status','OFFEN'),
              ('legal.kassenmeldung.date',''),
              ('legal.verfahrensdokumentation.status','VORLAGE_VORHANDEN'),
              ('legal.receipt.qr.optional','true'),
              ('legal.production.unlock','NO_USER_OVERRIDE');
            UPDATE app_settings SET value='0.7.33' WHERE key='app.version';
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        // R49 one-time migration: preserve the customer's previous Abholnummer on/off choice.
        await using (var pickupMigration = c.CreateCommand())
        {
            pickupMigration.CommandText = """
                UPDATE app_settings
                SET value=CASE WHEN LOWER(COALESCE((SELECT value FROM app_settings WHERE key='imbiss.pickup_number.enabled'),'true'))='false' THEN 'OFF' ELSE 'SALE' END
                WHERE key='imbiss.pickup_number.mode'
                  AND NOT EXISTS(SELECT 1 FROM app_settings WHERE key='migration.r49.pickup_mode');
                INSERT OR IGNORE INTO app_settings(key,value) VALUES('migration.r49.pickup_mode','done');
                """;
            await pickupMigration.ExecuteNonQueryAsync(ct);
        }
        // v0.7.33 additive inventory columns. Existing customer databases stay readable.
        await EnsureColumnAsync(c, "products", "stock_quantity", "REAL NOT NULL DEFAULT 0", ct);
        await EnsureColumnAsync(c, "products", "last_inventory_at", "TEXT NOT NULL DEFAULT ''", ct);
        // R47: KIOSK inventory warning and stock valuation. Additive only; old customer DBs remain compatible.
        await EnsureColumnAsync(c, "products", "min_stock_quantity", "REAL NOT NULL DEFAULT 0", ct);
        await EnsureColumnAsync(c, "products", "purchase_price_cents", "INTEGER NOT NULL DEFAULT 0", ct);
        // R50: catalogs can be scoped to KIOSK or IMBISS without deleting existing customer data.
        await EnsureColumnAsync(c, "categories", "edition_scope", "TEXT NOT NULL DEFAULT 'ALL'", ct);
        await EnsureColumnAsync(c, "products", "edition_scope", "TEXT NOT NULL DEFAULT 'ALL'", ct);
        // R48: every article gets a stable automatic article number; IMBISS sales can carry a daily pickup number.
        await EnsureColumnAsync(c, "sales", "pickup_number", "INTEGER NOT NULL DEFAULT 0", ct);
        // R49: pickup numbers may already belong to an accepted IMBISS order before final checkout.
        await EnsureColumnAsync(c, "parked_receipts", "pickup_number", "INTEGER NOT NULL DEFAULT 0", ct);
        await EnsureColumnAsync(c, "parked_receipts", "is_training", "INTEGER NOT NULL DEFAULT 0", ct);
        await using(var orderPrint=c.CreateCommand()) {
            orderPrint.CommandText="CREATE TABLE IF NOT EXISTS order_print_outbox(id TEXT PRIMARY KEY,order_id INTEGER NOT NULL,action TEXT NOT NULL,payload TEXT NOT NULL,state TEXT NOT NULL,created_at TEXT NOT NULL); CREATE INDEX IF NOT EXISTS ix_order_print_pending ON order_print_outbox(state);";
            await orderPrint.ExecuteNonQueryAsync(ct);
        }
        // R119: the order print queue is processed strictly in order and used
        // to stop at the first failure with no attempt counter and no way to
        // see it - a single job pointing at a printer that no longer exists
        // blocked every later kitchen ticket indefinitely. These two columns
        // let a job be retried a bounded number of times and then parked as
        // FAILED so the queue keeps moving, with the reason kept for the
        // Diagnose window.
        await EnsureColumnAsync(c,"order_print_outbox","attempts","INTEGER NOT NULL DEFAULT 0",ct);
        await EnsureColumnAsync(c,"order_print_outbox","last_error","TEXT NOT NULL DEFAULT ''",ct);
        await EnsureColumnAsync(c,"parked_receipts","preparation_state","TEXT NOT NULL DEFAULT 'ACCEPTED'",ct);
        await EnsureColumnAsync(c,"parked_receipts","order_note","TEXT NOT NULL DEFAULT ''",ct);
        await EnsureColumnAsync(c,"parked_receipts","workflow_version","INTEGER NOT NULL DEFAULT 0",ct);
        await EnsureColumnAsync(c,"parked_receipts","simulation_payment","TEXT NOT NULL DEFAULT ''",ct);
        // R95 follow-up: persists the Im-Haus/Außer-Haus choice made before
        // parking, so reopening the order later restores the same choice
        // instead of silently reverting to the Außer-Haus default.
        await EnsureColumnAsync(c,"parked_receipts","im_haus","INTEGER NOT NULL DEFAULT 0",ct);
        // R97: lets an admin opt a specific Warengruppe out of the
        // Im-Haus/Außer-Haus VAT rule entirely, instead of relying purely
        // on the implicit "only 7% rates change" behavior. "Warengruppe
        // is the VAT master" - category_master_data is authoritative,
        // products.im_haus_applicable is kept in sync the same way
        // vat_rate already is, and parked_receipt_items carries a
        // snapshot per line, same as vat_rate does there too.
        await EnsureColumnAsync(c,"category_master_data","im_haus_applicable","INTEGER NOT NULL DEFAULT 1",ct);
        await EnsureColumnAsync(c,"products","im_haus_applicable","INTEGER NOT NULL DEFAULT 1",ct);
        await EnsureColumnAsync(c,"parked_receipt_items","im_haus_applicable","INTEGER NOT NULL DEFAULT 1",ct);
        // R101: Mixed payment (split cash+card in one sale). Populated by
        // every INSERT going forward (Cash=(total,0), Card=(0,total),
        // Mixed=(X,total-X)). NOTE: deliberately NO backfill UPDATE for
        // pre-existing rows - trg_sales_no_update (below) makes `sales`
        // append-only and unconditionally aborts ANY UPDATE, including one
        // issued from migration code itself. A historical CASH/CARD sale
        // from before this column existed simply keeps cash_portion_cents=
        // card_portion_cents=0 forever; every report query below that reads
        // these two columns treats "both still 0" as "derive it from
        // payment_method+total_cents instead", so old rows keep reporting
        // exactly as they did before this column existed.
        await EnsureColumnAsync(c,"sales","cash_portion_cents","INTEGER NOT NULL DEFAULT 0",ct);
        await EnsureColumnAsync(c,"sales","card_portion_cents","INTEGER NOT NULL DEFAULT 0",ct);
        await using(var language=c.CreateCommand()) {
            language.CommandText="DELETE FROM app_settings WHERE key='ui.language';";
            await language.ExecuteNonQueryAsync(ct);
        }

        await EnsureColumnAsync(c, "users", "locked_until", "TEXT NOT NULL DEFAULT ''", ct);
        // R103: maps an unguessable digital-receipt token to a sale, for
        // the local QR-receipt web server. Token, not receipt_number, is
        // the lookup key so a customer's Bon can't be enumerated by
        // guessing/incrementing a URL.
        // R145: nothing reads this table any more - the digital receipt is
        // published to TOR Cloud. It stays so the schema of existing
        // databases does not change in this round.
        await using (var digitalReceipts = c.CreateCommand())
        {
            digitalReceipts.CommandText = """
                CREATE TABLE IF NOT EXISTS digital_receipts(
                  token TEXT PRIMARY KEY,
                  sale_id INTEGER NOT NULL,
                  created_at TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_digital_receipts_sale ON digital_receipts(sale_id);
                """;
            await digitalReceipts.ExecuteNonQueryAsync(ct);
        }
        // R106: durable lock against a duplicate card refund. When a BON
        // STORNO/Teilretoure's terminal RefundAsync call comes back
        // Unknown (ambiguous - it may have actually succeeded despite the
        // unclear response), a row here stays at state='UNKNOWN' and the
        // partial unique index below refuses any further refund attempt
        // against the SAME original sale until an admin explicitly
        // resolves it (after manually checking with the terminal/bank) -
        // exactly the same "durable, must-be-reconciled" property
        // checkout_operations already gives the forward payment direction,
        // now given to the reverse (refund) direction too.
        await using (var cardRefundLocks = c.CreateCommand())
        {
            cardRefundLocks.CommandText = """
                CREATE TABLE IF NOT EXISTS card_refund_attempts(
                  id TEXT PRIMARY KEY,
                  original_sale_id INTEGER NOT NULL,
                  kind TEXT NOT NULL,
                  amount_cents INTEGER NOT NULL,
                  state TEXT NOT NULL CHECK(state IN ('UNKNOWN','RESOLVED')),
                  created_at TEXT NOT NULL,
                  resolved_at TEXT NOT NULL DEFAULT '',
                  resolved_by TEXT NOT NULL DEFAULT '',
                  resolution_note TEXT NOT NULL DEFAULT '');
                CREATE UNIQUE INDEX IF NOT EXISTS ux_card_refund_one_unknown_per_sale
                  ON card_refund_attempts(original_sale_id) WHERE state='UNKNOWN';
                """;
            await cardRefundLocks.ExecuteNonQueryAsync(ct);
        }
        await SeedAsync(c, ct);
        await EnsureAutomaticArticleNumbersAsync(c, ct);
        await EnsureMasterDataHierarchyAsync(c, ct);
        await EnsureDefaultExtrasAsync(c, ct);
        await EnsureLegacyEditionScopeAsync(c, ct);
        // Sonstiges category migration v0.6.7:
        // Existing test products are preserved by moving them to Schnellwahl.
        await using (var migrate = c.CreateCommand())
        {
            migrate.CommandText = """
                UPDATE products
                SET category_id = (
                    SELECT id FROM categories
                    WHERE name='Schnellwahl'
                    ORDER BY id LIMIT 1
                )
                WHERE category_id IN (
                    SELECT id FROM categories
                    WHERE name='Sonstiges'
                )
                AND EXISTS (
                    SELECT 1 FROM categories
                    WHERE name='Schnellwahl'
                );

                UPDATE categories
                SET is_active=0
                WHERE name='Sonstiges';
                """;
            await migrate.ExecuteNonQueryAsync(ct);
        }
    });
}
    private static async Task EnsureColumnAsync(
        SqliteConnection c,
        string table,
        string column,
        string definition,
        CancellationToken ct)
    {
        await using var exists = c.CreateCommand();
        exists.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$name;";
        exists.Parameters.AddWithValue("$name", column);
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(ct)) > 0)
            return;

        await using var alter = c.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await alter.ExecuteNonQueryAsync(ct);
    }

    private static async Task EnsureAutomaticArticleNumbersAsync(
        SqliteConnection c,
        CancellationToken ct)
    {
        // Keep existing manually/imported article numbers. The automatic range starts at 100000.
        // Numeric existing SKUs advance the sequence so an automatically assigned number is never reused.
        await using (var seq = c.CreateCommand())
        {
            seq.CommandText = """
                INSERT OR IGNORE INTO app_sequence(key,value) VALUES('article_number',99999);
                UPDATE app_sequence
                SET value = MAX(
                    value,
                    COALESCE((
                        SELECT MAX(CAST(sku AS INTEGER))
                        FROM products
                        WHERE TRIM(sku) <> ''
                          AND sku NOT GLOB '*[^0-9]*'
                    ), 99999)
                )
                WHERE key='article_number';
                """;
            await seq.ExecuteNonQueryAsync(ct);
        }

        var blankIds = new List<long>();
        await using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT id FROM products WHERE TRIM(COALESCE(sku,''))='' ORDER BY id;";
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) blankIds.Add(r.GetInt64(0));
        }

        foreach (var id in blankIds)
        {
            long next;
            await using (var seq = c.CreateCommand())
            {
                seq.CommandText = "UPDATE app_sequence SET value=MAX(value,COALESCE((SELECT MAX(CAST(sku AS INTEGER)) FROM products WHERE TRIM(sku)<>'' AND sku NOT GLOB '*[^0-9]*'),99999))+1 WHERE key='article_number'; SELECT value FROM app_sequence WHERE key='article_number';";
                next = Convert.ToInt64(await seq.ExecuteScalarAsync(ct));
            }

            await using var update = c.CreateCommand();
            update.CommandText = "UPDATE products SET sku=$sku WHERE id=$id AND TRIM(COALESCE(sku,''))='';";
            update.Parameters.AddWithValue("$sku", next.ToString(System.Globalization.CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$id", id);
            await update.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task EnsureDefaultExtrasAsync(
        SqliteConnection c,
        CancellationToken ct)
    {
        await using var q = c.CreateCommand();
        q.CommandText = """
            INSERT OR IGNORE INTO extras(category_id,name,price_cents,sort_order,is_active)
            SELECT id,'Extra Käse',100,10,1 FROM categories
            WHERE name='Döner' COLLATE NOCASE LIMIT 1;

            INSERT OR IGNORE INTO extras(category_id,name,price_cents,sort_order,is_active)
            SELECT id,'Extra Ketchup',50,20,1 FROM categories
            WHERE name='Döner' COLLATE NOCASE LIMIT 1;

            INSERT OR IGNORE INTO extras(category_id,name,price_cents,sort_order,is_active)
            SELECT id,'Extra Fleisch',250,30,1 FROM categories
            WHERE name='Döner' COLLATE NOCASE LIMIT 1;
            """;
        await q.ExecuteNonQueryAsync(ct);
    }

    private static async Task EnsureMasterDataHierarchyAsync(
        SqliteConnection c,
        CancellationToken ct)
    {
        // IMPORTANT:
        // This migration is intentionally ADDITIVE. The existing categories table
        // is not altered. This keeps old customer databases fully readable.

        await using (var ensureGroup = c.CreateCommand())
        {
            ensureGroup.CommandText = """
                INSERT OR IGNORE INTO product_groups(
                  name,sort_order,is_active)
                VALUES('Standard',0,1);
                """;
            await ensureGroup.ExecuteNonQueryAsync(ct);
        }

        long standardGroupId;
        await using (var groupId = c.CreateCommand())
        {
            groupId.CommandText = """
                SELECT id
                FROM product_groups
                WHERE name='Standard'
                ORDER BY id
                LIMIT 1;
                """;

            var result = await groupId.ExecuteScalarAsync(ct);

            if (result is null)
                throw new InvalidOperationException(
                    "Standard-Gruppe konnte nicht angelegt werden.");

            standardGroupId = Convert.ToInt64(result);
        }

        // Create missing master-data rows for every existing Warengruppe.
        // VAT is inferred only if all active articles already use one single rate.
        await using (var assign = c.CreateCommand())
        {
            assign.CommandText = """
                INSERT OR IGNORE INTO category_master_data(
                  category_id,group_id,vat_rate)
                SELECT
                  c.id,
                  $group,
                  COALESCE(
                    (
                      SELECT MIN(p.vat_rate)
                      FROM products p
                      WHERE p.category_id=c.id
                        AND p.is_active=1
                      HAVING COUNT(DISTINCT p.vat_rate)=1
                    ),
                    19
                  )
                FROM categories c;
                """;

            assign.Parameters.AddWithValue(
                "$group",
                standardGroupId);

            await assign.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task EnsureLegacyEditionScopeAsync(
        SqliteConnection c,
        CancellationToken ct)
    {
        // R51: keep the two editions visually separate without deleting customer data.
        // Old KIOSK demo groups no longer appear on the IMBISS cashier screen.
        // Getränke stays shared because both editions use it; only the old demo Cola is KIOSK-only.
        await using var q = c.CreateCommand();
        q.CommandText = """
            UPDATE categories
            SET edition_scope='IMBISS'
            WHERE UPPER(name) IN ('DÖNER','BURGER','FINGERFOOD','PIZZA')
              AND UPPER(COALESCE(edition_scope,'ALL'))='ALL';

            UPDATE products
            SET edition_scope='IMBISS'
            WHERE category_id IN (
                SELECT id FROM categories
                WHERE UPPER(name) IN ('DÖNER','BURGER','FINGERFOOD','PIZZA')
            )
              AND UPPER(COALESCE(edition_scope,'ALL'))='ALL';

            UPDATE categories
            SET edition_scope='KIOSK'
            WHERE UPPER(name) IN ('SCHNELLWAHL','SNACKS')
              AND UPPER(COALESCE(edition_scope,'ALL'))='ALL';

            UPDATE products
            SET edition_scope='KIOSK'
            WHERE category_id IN (
                SELECT id FROM categories
                WHERE UPPER(name) IN ('SCHNELLWAHL','SNACKS')
            )
              AND UPPER(COALESCE(edition_scope,'ALL'))='ALL';

            UPDATE products
            SET edition_scope='KIOSK'
            WHERE name='Cola 0,33 l' COLLATE NOCASE
              AND barcode='5449000000996'
              AND UPPER(COALESCE(edition_scope,'ALL'))='ALL';
            """;
        await q.ExecuteNonQueryAsync(ct);
    }

    private static async Task SeedAsync(SqliteConnection c, CancellationToken ct)
    {
        await using var count = c.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM categories;";
        if (Convert.ToInt32(await count.ExecuteScalarAsync(ct)) > 0) return;

        await using var tx = await c.BeginTransactionAsync(ct);

        // Base seed: KIOSK-only Schnellwahl/Snacks plus the shared Getränke group.
        // IMBISS food groups are added only after IMBISS is explicitly selected.
        foreach (var item in new[] {
            (Name:"Schnellwahl",Sort:0,Scope:"KIOSK"),
            (Name:"Getränke",Sort:30,Scope:"ALL"),
            (Name:"Snacks",Sort:40,Scope:"KIOSK")
        })
        {
            await using var cmd = c.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = "INSERT INTO categories(name,sort_order,edition_scope) VALUES($n,$s,$scope);";
            cmd.Parameters.AddWithValue("$n", item.Name);
            cmd.Parameters.AddWithValue("$s", item.Sort);
            cmd.Parameters.AddWithValue("$scope", item.Scope);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        async Task<long> Cat(string name)
        {
            await using var q = c.CreateCommand();
            q.Transaction = (SqliteTransaction)tx;
            q.CommandText = "SELECT id FROM categories WHERE name=$n;";
            q.Parameters.AddWithValue("$n", name);
            return Convert.ToInt64(await q.ExecuteScalarAsync(ct));
        }

        var g = await Cat("Getränke");
        await using (var q = c.CreateCommand())
        {
            q.Transaction = (SqliteTransaction)tx;
            q.CommandText = """
                INSERT INTO products(
                  category_id,name,barcode,base_price_cents,vat_rate,pfand_cents,edition_scope)
                VALUES($c,'Cola 0,33 l','5449000000996',275,19,25,'KIOSK');
                """;
            q.Parameters.AddWithValue("$c", g);
            await q.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

}

public sealed class ProductRepository : IProductRepository
{
    private readonly SqliteDatabase _db;

    public ProductRepository(
        SqliteDatabase db) =>
        _db = db;
public async Task<IReadOnlyList<ProductGroup>> GetGroupsAsync(CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var result = new List<ProductGroup>();
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT id,name,sort_order
            FROM product_groups
            WHERE is_active=1
            ORDER BY sort_order,name;
            """;
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            result.Add(new ProductGroup(r.GetInt64(0), r.GetString(1), r.GetInt32(2)));
        }

        return result;
    });
}public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var result = new List<Category>();
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT
              c.id,
              COALESCE(m.group_id,0),
              c.name,
              COALESCE(m.vat_rate,19),
              c.sort_order,
              COALESCE(v.tile_color,'#17466A'),
              COALESCE(k.station,''),
              COALESCE(m.im_haus_applicable,1)
            FROM categories c
            LEFT JOIN category_master_data m
              ON m.category_id=c.id
            LEFT JOIN category_visual_data v
              ON v.category_id=c.id
            LEFT JOIN category_kitchen_data k
              ON k.category_id=c.id
            WHERE c.is_active=1
              AND (UPPER(COALESCE(c.edition_scope,'ALL'))='ALL'
                   OR UPPER(c.edition_scope)=UPPER(COALESCE((SELECT value FROM app_settings WHERE key='business.mode'),'IMBISS')))
            ORDER BY c.sort_order,c.name;
            """;
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            result.Add(new Category(r.GetInt64(0), r.GetInt64(1), r.GetString(2), Convert.ToDecimal(r.GetDouble(3)), r.GetInt32(4), r.GetString(5), r.GetString(6), r.GetInt64(7) != 0));
        }

        return result;
    });
}public async Task ReorderCategoriesAsync(
    IReadOnlyList<long> orderedCategoryIds,
    CancellationToken ct = default)
{
    await IoQueue.RunAsync(async () =>
    {
        if (orderedCategoryIds.Count == 0)
            return;

        var ids = orderedCategoryIds.ToArray();
        if (ids.Any(x => x <= 0) || ids.Distinct().Count() != ids.Length)
            throw new InvalidOperationException("Ungültige oder doppelte Warengruppen-ID in der Sortierung.");

        await using var c = _db.OpenConnection();
        await using var tx = await c.BeginTransactionAsync(ct);

        for (var i = 0; i < ids.Length; i++)
        {
            await using var q = c.CreateCommand();
            q.Transaction = (SqliteTransaction)tx;
            q.CommandText = """
                UPDATE categories
                SET sort_order=$sort
                WHERE id=$id AND is_active=1;
                """;
            q.Parameters.AddWithValue("$sort", i);
            q.Parameters.AddWithValue("$id", ids[i]);

            var changed = await q.ExecuteNonQueryAsync(ct);
            if (changed != 1)
                throw new InvalidOperationException(
                    $"Warengruppe {ids[i]} konnte nicht eindeutig sortiert werden.");
        }

        await tx.CommitAsync(ct);
    });
}

public async Task<long> SaveGroupAsync(ProductGroup group, CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var name = group.Name.Trim();
        if (name.Length == 0)
            throw new InvalidOperationException("Gruppenname darf nicht leer sein.");
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = group.Id == 0 ? """
                    INSERT INTO product_groups(
                      name,sort_order,is_active)
                    VALUES($name,$sort,1);
                    SELECT last_insert_rowid();
                    """ : """
                    UPDATE product_groups
                    SET name=$name,
                        sort_order=$sort,
                        is_active=1
                    WHERE id=$id;
                    SELECT $id;
                    """;
        if (group.Id != 0)
            q.Parameters.AddWithValue("$id", group.Id);
        q.Parameters.AddWithValue("$name", name);
        q.Parameters.AddWithValue("$sort", group.SortOrder);
        return Convert.ToInt64(await q.ExecuteScalarAsync(ct));
    });
}public async Task<long> SaveCategoryAsync(Category category, CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        if (category.GroupId <= 0)
            throw new InvalidOperationException("Bitte zuerst eine Gruppe auswählen.");
        if (category.VatRate != 7m && category.VatRate != 19m)
        {
            throw new InvalidOperationException("MwSt. muss 7 % oder 19 % sein.");
        }

        var name = category.Name.Trim();
        if (name.Length == 0)
            throw new InvalidOperationException("Warengruppenname darf nicht leer sein.");
        await using var c = _db.OpenConnection();
        await using var tx = await c.BeginTransactionAsync(ct);
        long id;
        await using (var q = c.CreateCommand())
        {
            q.Transaction = (SqliteTransaction)tx;
            q.CommandText = category.Id == 0 ? """
                        INSERT INTO categories(
                          name,sort_order,is_active,edition_scope)
                        VALUES($name,$sort,1,$scope);
                        SELECT last_insert_rowid();
                        """ : """
                        UPDATE categories
                        SET name=$name,
                            sort_order=$sort,
                            is_active=1
                        WHERE id=$id;
                        SELECT $id;
                        """;
            if (category.Id != 0)
                q.Parameters.AddWithValue("$id", category.Id);
            q.Parameters.AddWithValue("$name", name);
            q.Parameters.AddWithValue("$sort", category.SortOrder);
            q.Parameters.AddWithValue("$scope", CurrentEditionScope(c, (SqliteTransaction)tx));
            id = Convert.ToInt64(await q.ExecuteScalarAsync(ct));
        }

        await using (var meta = c.CreateCommand())
        {
            meta.Transaction = (SqliteTransaction)tx;
            meta.CommandText = """
                INSERT INTO category_master_data(
                  category_id,group_id,vat_rate,im_haus_applicable)
                VALUES($category,$group,$vat,$imHaus)
                ON CONFLICT(category_id)
                DO UPDATE SET
                  group_id=excluded.group_id,
                  vat_rate=excluded.vat_rate,
                  im_haus_applicable=excluded.im_haus_applicable;
                """;
            meta.Parameters.AddWithValue("$category", id);
            meta.Parameters.AddWithValue("$group", category.GroupId);
            meta.Parameters.AddWithValue("$vat", category.VatRate);
            meta.Parameters.AddWithValue("$imHaus", category.ImHausApplicable ? 1 : 0);
            await meta.ExecuteNonQueryAsync(ct);
        }

        await using (var visual = c.CreateCommand())
        {
            visual.Transaction = (SqliteTransaction)tx;
            visual.CommandText = """
                INSERT INTO category_visual_data(category_id,tile_color)
                VALUES($category,$color)
                ON CONFLICT(category_id)
                DO UPDATE SET tile_color=excluded.tile_color;
                """;
            visual.Parameters.AddWithValue("$category", id);
            visual.Parameters.AddWithValue("$color", NormalizeTileColor(category.TileColor));
            await visual.ExecuteNonQueryAsync(ct);
        }

        await using (var kitchen = c.CreateCommand())
        {
            kitchen.Transaction = (SqliteTransaction)tx;
            kitchen.CommandText = """
                INSERT INTO category_kitchen_data(category_id,station)
                VALUES($category,$station)
                ON CONFLICT(category_id)
                DO UPDATE SET station=excluded.station;
                """;
            kitchen.Parameters.AddWithValue("$category", id);
            kitchen.Parameters.AddWithValue("$station", KitchenStations.Normalize(category.KitchenStation));
            await kitchen.ExecuteNonQueryAsync(ct);
        }

        // Warengruppe is the VAT master.
        await using (var propagate = c.CreateCommand())
        {
            propagate.Transaction = (SqliteTransaction)tx;
            propagate.CommandText = """
                UPDATE products
                SET vat_rate=$vat,
                    im_haus_applicable=$imHaus
                WHERE category_id=$category;
                """;
            propagate.Parameters.AddWithValue("$vat", category.VatRate);
            propagate.Parameters.AddWithValue("$imHaus", category.ImHausApplicable ? 1 : 0);
            propagate.Parameters.AddWithValue("$category", id);
            await propagate.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return id;
    });
}
public async Task DeactivateGroupAsync(long groupId, string actor, CancellationToken ct = default)
{
    await IoQueue.RunAsync(async () =>
    {
        if (groupId <= 0) throw new InvalidOperationException("Bitte zuerst eine Gruppe auswählen.");
        await using var c = _db.OpenConnection();
        await using var tx = await c.BeginTransactionAsync(ct);
        var now = DateTimeOffset.Now.ToString("O");

        await using (var q = c.CreateCommand())
        {
            q.Transaction = (SqliteTransaction)tx;
            q.CommandText = """
                UPDATE products SET is_active=0
                WHERE category_id IN (SELECT category_id FROM category_master_data WHERE group_id=$id);
                UPDATE extras SET is_active=0
                WHERE category_id IN (SELECT category_id FROM category_master_data WHERE group_id=$id);
                UPDATE categories SET is_active=0
                WHERE id IN (SELECT category_id FROM category_master_data WHERE group_id=$id);
                UPDATE product_groups SET is_active=0 WHERE id=$id;
                DELETE FROM product_combo_items
                WHERE product_id IN (SELECT id FROM products WHERE category_id IN (SELECT category_id FROM category_master_data WHERE group_id=$id))
                   OR component_product_id IN (SELECT id FROM products WHERE category_id IN (SELECT category_id FROM category_master_data WHERE group_id=$id));
                INSERT INTO audit_log(created_at,actor,event_type,entity_type,entity_id,details)
                VALUES($at,$actor,'MASTERDATA_GROUP_DEACTIVATED','PRODUCT_GROUP',$id,'Gruppe inkl. Warengruppen/Artikel deaktiviert');
                """;
            q.Parameters.AddWithValue("$id", groupId);
            q.Parameters.AddWithValue("$at", now);
            q.Parameters.AddWithValue("$actor", string.IsNullOrWhiteSpace(actor) ? "unknown" : actor.Trim());
            await q.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    });
}
public async Task DeactivateCategoryAsync(long categoryId, string actor, CancellationToken ct = default)
{
    await IoQueue.RunAsync(async () =>
    {
        if (categoryId <= 0) throw new InvalidOperationException("Bitte zuerst eine Warengruppe auswählen.");
        await using var c = _db.OpenConnection();
        await using var tx = await c.BeginTransactionAsync(ct);
        var now = DateTimeOffset.Now.ToString("O");
        await using var q = c.CreateCommand();
        q.Transaction = (SqliteTransaction)tx;
        q.CommandText = """
            UPDATE products SET is_active=0 WHERE category_id=$id;
            UPDATE extras SET is_active=0 WHERE category_id=$id;
            UPDATE categories SET is_active=0 WHERE id=$id;
            DELETE FROM product_combo_items
            WHERE product_id IN (SELECT id FROM products WHERE category_id=$id)
               OR component_product_id IN (SELECT id FROM products WHERE category_id=$id);
            INSERT INTO audit_log(created_at,actor,event_type,entity_type,entity_id,details)
            VALUES($at,$actor,'MASTERDATA_CATEGORY_DEACTIVATED','CATEGORY',$id,'Warengruppe inkl. Artikel deaktiviert');
            """;
        q.Parameters.AddWithValue("$id", categoryId);
        q.Parameters.AddWithValue("$at", now);
        q.Parameters.AddWithValue("$actor", string.IsNullOrWhiteSpace(actor) ? "unknown" : actor.Trim());
        await q.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    });
}
public async Task DeactivateProductAsync(long productId, string actor, CancellationToken ct = default)
{
    await IoQueue.RunAsync(async () =>
    {
        if (productId <= 0) throw new InvalidOperationException("Bitte zuerst einen Artikel auswählen.");
        await using var c = _db.OpenConnection();
        await using var tx = await c.BeginTransactionAsync(ct);
        var now = DateTimeOffset.Now.ToString("O");
        await using var q = c.CreateCommand();
        q.Transaction = (SqliteTransaction)tx;
        q.CommandText = """
            UPDATE products SET is_active=0 WHERE id=$id;
            DELETE FROM product_combo_items WHERE product_id=$id OR component_product_id=$id;
            INSERT INTO audit_log(created_at,actor,event_type,entity_type,entity_id,details)
            VALUES($at,$actor,'MASTERDATA_PRODUCT_DEACTIVATED','PRODUCT',$id,'Artikel deaktiviert');
            """;
        q.Parameters.AddWithValue("$id", productId);
        q.Parameters.AddWithValue("$at", now);
        q.Parameters.AddWithValue("$actor", string.IsNullOrWhiteSpace(actor) ? "unknown" : actor.Trim());
        await q.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    });
}
    private static string CurrentEditionScope(SqliteConnection c, SqliteTransaction? tx = null)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT UPPER(COALESCE((SELECT value FROM app_settings WHERE key='business.mode'),'IMBISS'));";
        var scope = (q.ExecuteScalar() as string ?? "IMBISS").Trim().ToUpperInvariant();
        return scope is "KIOSK" or "IMBISS" ? scope : "ALL";
    }

    private static string NormalizeTileColor(string? value)
    {
        var color = (value ?? "").Trim().ToUpperInvariant();
        if (color.Length == 7 && color[0] == '#' &&
            color.Skip(1).All(Uri.IsHexDigit))
        {
            return color;
        }

        return "#17466A";
    }
public async Task<IReadOnlyList<Product>> GetActiveProductsAsync(CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var products = new List<Product>();
        await using var c = _db.OpenConnection();
        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
              SELECT id,category_id,name,sku,barcode,base_price_cents,vat_rate,pfand_cents,
                     unit,image_path,is_active,sort_order,
                     COALESCE(stock_milli,CAST(ROUND(COALESCE(stock_quantity,0)*1000.0) AS INTEGER)),
                     COALESCE(min_stock_milli,CAST(ROUND(COALESCE(min_stock_quantity,0)*1000.0) AS INTEGER)),
                     COALESCE(purchase_price_cents,0),
                     COALESCE(im_haus_applicable,1)
              FROM products
              WHERE is_active=1
                AND (UPPER(COALESCE(edition_scope,'ALL'))='ALL'
                     OR UPPER(edition_scope)=UPPER(COALESCE((SELECT value FROM app_settings WHERE key='business.mode'),'IMBISS')))
                AND EXISTS(
                    SELECT 1 FROM categories c
                    WHERE c.id=products.category_id
                      AND c.is_active=1
                      AND (UPPER(COALESCE(c.edition_scope,'ALL'))='ALL'
                           OR UPPER(c.edition_scope)=UPPER(COALESCE((SELECT value FROM app_settings WHERE key='business.mode'),'IMBISS')))
                )
              ORDER BY category_id,sort_order,name;
              """;
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                products.Add(ReadProduct(r));
        }

        var byProduct = new Dictionary<long, List<ProductVariant>>();
        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
              SELECT id,product_id,name,price_cents,sort_order,is_active
              FROM product_variants
              WHERE is_active=1
              ORDER BY product_id,sort_order,id;
              """;
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var v = new ProductVariant(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3), r.GetInt32(4), r.GetInt64(5) == 1);
                if (!byProduct.TryGetValue(v.ProductId, out var list))
                {
                    byProduct[v.ProductId] = list = new();
                }

                list.Add(v);
            }
        }

        foreach (var p in products)
        {
            p.Variants = byProduct.TryGetValue(p.Id, out var list) ? list : Array.Empty<ProductVariant>();
        }

        var comboByProduct = new Dictionary<long, List<ProductComboItem>>();
        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
              SELECT ci.product_id,ci.component_product_id,p.name,
                     CASE WHEN COALESCE(ci.quantity_milli,0)<>0
                          THEN ci.quantity_milli
                          ELSE CAST(ROUND(ci.quantity*1000.0) AS INTEGER) END,
                     ci.sort_order,
                     COALESCE(ci.choice_group,'')
              FROM product_combo_items ci
              JOIN products p ON p.id=ci.component_product_id
              WHERE p.is_active=1
              ORDER BY ci.product_id,ci.sort_order,ci.component_product_id;
              """;
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var item = new ProductComboItem(
                    r.GetInt64(0),
                    r.GetInt64(1),
                    r.GetString(2),
                    QuantityStorage.FromMilli(r.GetInt64(3)),
                    r.GetInt32(4),
                    r.GetString(5));
                if (!comboByProduct.TryGetValue(item.ProductId,out var items)) comboByProduct[item.ProductId]=items=new();
                items.Add(item);
            }
        }
        foreach (var p in products)
            p.ComboItems = comboByProduct.TryGetValue(p.Id,out var combo) ? combo : Array.Empty<ProductComboItem>();

        return products;
    });
}public async Task<Product?> GetByIdAsync(long id, CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var all = await GetActiveProductsAsync(ct);
        return all.FirstOrDefault(x => x.Id == id);
    });
}public Task<long> SaveAsync(Product p, CancellationToken ct = default) => SaveWithStockAsync(p,null,0,"SYSTEM",ct);
public async Task<long> SaveWithStockAsync(Product p, decimal? count, decimal expected, string actor, CancellationToken ct = default, IReadOnlyList<ProductVariant>? variants = null, IReadOnlyList<ProductComboItem>? comboItems = null)
{
    return await IoQueue.RunAsync(async () =>
    {
        if(count is <0 or >1000000000) throw new InvalidOperationException("Ungültiger Bestand.");
        await using var c = _db.OpenConnection();
        using var tx = c.BeginTransaction();
        if(count is not null && p.Id != 0) {
            using var check=c.CreateCommand();check.Transaction=tx;
            check.CommandText="SELECT COALESCE(stock_milli,CAST(ROUND(COALESCE(stock_quantity,0)*1000.0) AS INTEGER)) FROM products WHERE id=$id";check.Parameters.AddWithValue("$id",p.Id);
            var old=check.ExecuteScalar();
            if(old is null || Convert.ToInt64(old)!=QuantityStorage.ToMilli(expected)) throw new InvalidOperationException("Bestand wurde inzwischen geändert. Bitte Artikel neu laden.");
        }
        decimal inheritedVat;
        bool inheritedImHaus;
        await using (var vatQuery = c.CreateCommand())
        {
            vatQuery.Transaction=tx;
            vatQuery.CommandText = """
                SELECT vat_rate,COALESCE(im_haus_applicable,1)
                FROM category_master_data
                WHERE category_id=$category;
                """;
            vatQuery.Parameters.AddWithValue("$category", p.CategoryId);
            await using var vatReader = await vatQuery.ExecuteReaderAsync(ct);
            if (!await vatReader.ReadAsync(ct))
            {
                throw new InvalidOperationException("Warengruppe hat noch keine MwSt.-Stammdaten.");
            }

            inheritedVat = Convert.ToDecimal(vatReader.GetDouble(0));
            inheritedImHaus = vatReader.GetInt64(1) != 0;
        }

        var sku = (p.Sku ?? "").Trim();
        if (p.Id == 0 && sku.Length == 0)
        {
            await using var seq = c.CreateCommand();
            seq.Transaction=tx;
            seq.CommandText = "UPDATE app_sequence SET value=MAX(value,COALESCE((SELECT MAX(CAST(sku AS INTEGER)) FROM products WHERE TRIM(sku)<>'' AND sku NOT GLOB '*[^0-9]*'),99999))+1 WHERE key='article_number'; SELECT value FROM app_sequence WHERE key='article_number';";
            sku = Convert.ToInt64(await seq.ExecuteScalarAsync(ct)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        await using var q = c.CreateCommand();
        q.Transaction=tx;
        q.CommandText = p.Id == 0 ? """
                  INSERT INTO products(
                    category_id,name,sku,barcode,base_price_cents,
                    vat_rate,pfand_cents,unit,image_path,is_active,sort_order,
                    min_stock_quantity,min_stock_milli,purchase_price_cents,edition_scope,im_haus_applicable)
                  VALUES(
                    $cat,$name,$sku,$bar,$price,
                    $vat,$pfand,$unit,$img,$active,$sort,$minstock,$minstockmilli,$purchase,$scope,$imHaus);
                  SELECT last_insert_rowid();
                  """ : """
                  UPDATE products
                  SET category_id=$cat,
                      name=$name,
                      sku=$sku,
                      barcode=$bar,
                      base_price_cents=$price,
                      vat_rate=$vat,
                      pfand_cents=$pfand,
                      unit=$unit,
                      image_path=$img,
                      is_active=$active,
                      sort_order=$sort,
                      min_stock_quantity=$minstock,
                      min_stock_milli=$minstockmilli,
                      purchase_price_cents=$purchase,
                      im_haus_applicable=$imHaus
                  WHERE id=$id;
                  SELECT $id;
                  """;
        if (p.Id != 0)
            q.Parameters.AddWithValue("$id", p.Id);
        q.Parameters.AddWithValue("$cat", p.CategoryId);
        q.Parameters.AddWithValue("$name", p.Name.Trim());
        q.Parameters.AddWithValue("$sku", sku);
        q.Parameters.AddWithValue("$bar", p.Barcode.Trim());
        q.Parameters.AddWithValue("$price", p.BasePriceCents);
        q.Parameters.AddWithValue("$vat", inheritedVat);
        q.Parameters.AddWithValue("$imHaus", inheritedImHaus ? 1 : 0);
        q.Parameters.AddWithValue("$pfand", p.PfandCents);
        q.Parameters.AddWithValue("$unit", p.Unit);
        q.Parameters.AddWithValue("$img", p.ImagePath);
        q.Parameters.AddWithValue("$active", p.IsActive ? 1 : 0);
        q.Parameters.AddWithValue("$sort", p.SortOrder);
        q.Parameters.AddWithValue("$minstock", Convert.ToDouble(Math.Max(0m, p.MinStockQuantity)));
        q.Parameters.AddWithValue("$minstockmilli", QuantityStorage.ToMilli(Math.Max(0m, p.MinStockQuantity)));
        q.Parameters.AddWithValue("$purchase", Math.Max(0, p.PurchasePriceCents));
        q.Parameters.AddWithValue("$scope", CurrentEditionScope(c, tx));
        var id=Convert.ToInt64(await q.ExecuteScalarAsync(ct));
        if(count is not null) {
            using var stock=c.CreateCommand();stock.Transaction=tx;
            stock.CommandText="UPDATE products SET stock_quantity=$qty,stock_milli=$qtyMilli,last_inventory_at=$at WHERE id=$id; INSERT INTO audit_log(created_at,actor,event_type,entity_type,entity_id,details) VALUES($at,$actor,'INVENTORY_COUNT_SET','PRODUCT',$id,$details);";
            stock.Parameters.AddWithValue("$qty",Convert.ToDouble(count.Value));stock.Parameters.AddWithValue("$qtyMilli",QuantityStorage.ToMilli(count.Value));stock.Parameters.AddWithValue("$at",DateTimeOffset.Now.ToString("O"));stock.Parameters.AddWithValue("$id",id);stock.Parameters.AddWithValue("$actor",actor);stock.Parameters.AddWithValue("$details",$"{expected} -> {count}");await stock.ExecuteNonQueryAsync(ct);
        }
        if(variants is not null)await ReplaceVariantsInTransactionAsync(c,tx,id,variants,ct);
        if(comboItems is not null)await ReplaceComboItemsInTransactionAsync(c,tx,id,comboItems,ct);
        tx.Commit();return id;
    });
}public Task ReplaceVariantsAsync(long productId,IReadOnlyList<ProductVariant> variants,CancellationToken ct=default) => IoQueue.RunAsync(async()=>{
 using var c=_db.OpenConnection();using var tx=c.BeginTransaction();await ReplaceVariantsInTransactionAsync(c,tx,productId,variants,ct);tx.Commit();
});
private static async Task ReplaceVariantsInTransactionAsync(SqliteConnection c,SqliteTransaction tx,long productId,IReadOnlyList<ProductVariant> variants,CancellationToken ct){
 if(variants.Any(x=>string.IsNullOrWhiteSpace(x.Name)||x.PriceCents<0))throw new InvalidOperationException("Ungültige Variante.");
        await using (var del = c.CreateCommand())
        {
            del.Transaction = (SqliteTransaction)tx;
            del.CommandText = "DELETE FROM product_variants WHERE product_id=$id;";
            del.Parameters.AddWithValue("$id", productId);
            await del.ExecuteNonQueryAsync(ct);
        }

        for (var i = 0; i < variants.Count; i++)
        {
            await using var q = c.CreateCommand();
            q.Transaction = (SqliteTransaction)tx;
            q.CommandText = """
                INSERT INTO product_variants(
                  product_id,name,price_cents,sort_order)
                VALUES($p,$n,$c,$s);
                """;
            q.Parameters.AddWithValue("$p", productId);
            q.Parameters.AddWithValue("$n", variants[i].Name.Trim());
            q.Parameters.AddWithValue("$c", variants[i].PriceCents);
            q.Parameters.AddWithValue("$s", i);
            await q.ExecuteNonQueryAsync(ct);
        }

}
public Task ReplaceComboItemsAsync(long productId,IReadOnlyList<ProductComboItem> items,CancellationToken ct=default) => IoQueue.RunAsync(async()=>{
 using var c=_db.OpenConnection();using var tx=c.BeginTransaction();await ReplaceComboItemsInTransactionAsync(c,tx,productId,items,ct);tx.Commit();
});
private static async Task ReplaceComboItemsInTransactionAsync(SqliteConnection c,SqliteTransaction tx,long productId,IReadOnlyList<ProductComboItem> items,CancellationToken ct){
        if (items.Any(x => x.ComponentProductId <= 0 || x.ComponentProductId == productId || x.Quantity <= 0m))
            throw new InvalidOperationException("Ungültige Menü-/Combo-Zusammenstellung.");
        if (items.Select(x => x.ComponentProductId).Distinct().Count() != items.Count)
            throw new InvalidOperationException("Ein Artikel darf im Menü nur einmal vorkommen. Menge bitte am Eintrag ändern.");
        var normalized = items
            .Select((x,index) => x with
            {
                SortOrder = index,
                ChoiceGroup = (x.ChoiceGroup ?? "").Trim().ToUpperInvariant()
            })
            .ToArray();

        foreach (var group in normalized
                     .Where(x => x.IsChoice)
                     .GroupBy(x => x.ChoiceGroup, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() < 2)
                throw new InvalidOperationException(
                    $"Auswahlgruppe {group.Key} benötigt mindestens zwei Artikel.");
        }

        var previous = new List<(long Id,decimal Quantity,string Group)>();
        await using(var read=c.CreateCommand()) {
            read.Transaction=tx;
            read.CommandText="SELECT component_product_id,CASE WHEN COALESCE(quantity_milli,0)<>0 THEN quantity_milli ELSE CAST(ROUND(quantity*1000.0) AS INTEGER) END,COALESCE(choice_group,'') FROM product_combo_items WHERE product_id=$id ORDER BY component_product_id;";
            read.Parameters.AddWithValue("$id",productId);await using var r=await read.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
                previous.Add((r.GetInt64(0),QuantityStorage.FromMilli(r.GetInt64(1)),r.GetString(2)));
        }
        var recipeChanged=!previous.SequenceEqual(
            normalized.OrderBy(x=>x.ComponentProductId)
                .Select(x=>(x.ComponentProductId,x.Quantity,x.ChoiceGroup)));
        if(recipeChanged) {
            await using var pending=c.CreateCommand();pending.Transaction=tx;
            pending.CommandText="SELECT COUNT(*) FROM parked_receipt_items i JOIN parked_receipts p ON p.id=i.parked_receipt_id WHERE i.product_id=$id AND p.status='OPEN';";
            pending.Parameters.AddWithValue("$id",productId);
            if(Convert.ToInt64(await pending.ExecuteScalarAsync(ct))>0)
                throw new InvalidOperationException("Menüzusammenstellung ist durch offene Bestellungen gesperrt. Diese zuerst abschließen oder stornieren.");
        }
        if(items.Count>0) {
            await using var parent=c.CreateCommand();parent.Transaction=tx;
            parent.CommandText="SELECT COUNT(*) FROM product_combo_items WHERE component_product_id=$id;";
            parent.Parameters.AddWithValue("$id",productId);
            if(Convert.ToInt64(await parent.ExecuteScalarAsync(ct))>0)
                throw new InvalidOperationException("Dieser Artikel ist bereits Menübestandteil. Verschachtelte Menüs sind nicht unterstützt.");
            foreach(var item in normalized) {
                await using var component=c.CreateCommand();component.Transaction=tx;
                component.CommandText="SELECT COUNT(*) FROM products WHERE id=$id AND is_active=1 AND NOT EXISTS(SELECT 1 FROM product_combo_items WHERE product_id=$id);";
                component.Parameters.AddWithValue("$id",item.ComponentProductId);
                if(Convert.ToInt64(await component.ExecuteScalarAsync(ct))!=1)
                    throw new InvalidOperationException("Menübestandteile müssen aktive Einzelartikel sein. Verschachtelte Menüs sind nicht unterstützt.");
            }
        }
        await using (var del = c.CreateCommand())
        {
            del.Transaction=(SqliteTransaction)tx;
            del.CommandText="DELETE FROM product_combo_items WHERE product_id=$id;";
            del.Parameters.AddWithValue("$id",productId);
            await del.ExecuteNonQueryAsync(ct);
        }
        for (var i=0;i<normalized.Length;i++)
        {
            await using var q=c.CreateCommand();
            q.Transaction=(SqliteTransaction)tx;
            q.CommandText="INSERT INTO product_combo_items(product_id,component_product_id,quantity,quantity_milli,sort_order,choice_group) VALUES($p,$c,$q,$qm,$s,$g);";
            q.Parameters.AddWithValue("$p",productId);
            q.Parameters.AddWithValue("$c",normalized[i].ComponentProductId);
            q.Parameters.AddWithValue("$q",Convert.ToDouble(normalized[i].Quantity));
            q.Parameters.AddWithValue("$qm",QuantityStorage.ToMilli(normalized[i].Quantity));
            q.Parameters.AddWithValue("$s",i);
            q.Parameters.AddWithValue("$g",normalized[i].ChoiceGroup);
            await q.ExecuteNonQueryAsync(ct);
        }
}
public async Task<IReadOnlyList<ProductComboItem>> GetComboItemsAsync(long productId, CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var result=new List<ProductComboItem>();
        await using var c=_db.OpenConnection(); await using var q=c.CreateCommand();
        q.CommandText="SELECT ci.product_id,ci.component_product_id,p.name,CASE WHEN COALESCE(ci.quantity_milli,0)<>0 THEN ci.quantity_milli ELSE CAST(ROUND(ci.quantity*1000.0) AS INTEGER) END,ci.sort_order,COALESCE(ci.choice_group,'') FROM product_combo_items ci JOIN products p ON p.id=ci.component_product_id WHERE ci.product_id=$id ORDER BY ci.sort_order,ci.component_product_id;";
        q.Parameters.AddWithValue("$id",productId); await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct)) result.Add(new ProductComboItem(
            r.GetInt64(0),
            r.GetInt64(1),
            r.GetString(2),
            QuantityStorage.FromMilli(r.GetInt64(3)),
            r.GetInt32(4),
            r.GetString(5)));
        return (IReadOnlyList<ProductComboItem>)result;
    });
}
public async Task<IReadOnlyList<ExtraItem>> GetExtrasAsync(CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var result = new List<ExtraItem>();
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT e.id,e.category_id,e.name,e.price_cents,
                   COALESCE(m.vat_rate,19),e.sort_order,e.is_active
            FROM extras e
            JOIN categories c ON c.id=e.category_id
            LEFT JOIN category_master_data m ON m.category_id=e.category_id
            WHERE e.is_active=1
              AND (UPPER(COALESCE(c.edition_scope,'ALL'))='ALL'
                   OR UPPER(c.edition_scope)=UPPER(COALESCE((SELECT value FROM app_settings WHERE key='business.mode'),'IMBISS')))
            ORDER BY e.sort_order,e.name;
            """;
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            result.Add(new ExtraItem(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3), Convert.ToDecimal(r.GetDouble(4)), r.GetInt32(5), r.GetInt64(6) == 1));
        }

        return result;
    });
}public async Task<long> SaveExtraAsync(ExtraItem extra, CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var name = (extra.Name ?? "").Trim();
        if (name.Length == 0)
            throw new InvalidOperationException("Extra-Name darf nicht leer sein.");
        if (extra.CategoryId <= 0)
            throw new InvalidOperationException("Bitte eine Warengruppe auswählen.");
        if (extra.PriceCents < 0)
            throw new InvalidOperationException("Extra-Preis darf nicht negativ sein.");
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = extra.Id == 0 ? """
                INSERT INTO extras(category_id,name,price_cents,sort_order,is_active)
                VALUES($category,$name,$price,$sort,1);
                SELECT last_insert_rowid();
                """ : """
                UPDATE extras SET
                  category_id=$category,name=$name,price_cents=$price,
                  sort_order=$sort,is_active=1
                WHERE id=$id;
                SELECT $id;
                """;
        if (extra.Id != 0)
            q.Parameters.AddWithValue("$id", extra.Id);
        q.Parameters.AddWithValue("$category", extra.CategoryId);
        q.Parameters.AddWithValue("$name", name);
        q.Parameters.AddWithValue("$price", extra.PriceCents);
        q.Parameters.AddWithValue("$sort", extra.SortOrder);
        return Convert.ToInt64(await q.ExecuteScalarAsync(ct));
    });
}
    private static Product ReadProduct(
        SqliteDataReader r) =>
        new()
        {
            Id=r.GetInt64(0),
            CategoryId=r.GetInt64(1),
            Name=r.GetString(2),
            Sku=r.GetString(3),
            Barcode=r.GetString(4),
            BasePriceCents=r.GetInt64(5),
            VatRate=Convert.ToDecimal(r.GetDouble(6)),
            PfandCents=r.GetInt64(7),
            Unit=r.GetString(8),
            ImagePath=r.GetString(9),
            IsActive=r.GetInt64(10)==1,
            SortOrder=r.GetInt32(11),
            StockQuantity=QuantityStorage.FromMilli(r.GetInt64(12)),
            MinStockQuantity=QuantityStorage.FromMilli(r.GetInt64(13)),
            PurchasePriceCents=r.GetInt64(14),
            ImHausApplicable=r.GetInt64(15)!=0
        };
}

public sealed class CardRefundLockRepository : ICardRefundLockRepository
{
    private readonly SqliteDatabase _db;
    private readonly IAuditLog _audit;
    public CardRefundLockRepository(SqliteDatabase db, IAuditLog audit) { _db = db; _audit = audit; }

    public async Task<bool> HasUnresolvedAsync(long originalSaleId, CancellationToken ct = default)
    {
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM card_refund_attempts WHERE original_sale_id=$id AND state='UNKNOWN';";
        q.Parameters.AddWithValue("$id", originalSaleId);
        return Convert.ToInt64(await q.ExecuteScalarAsync(ct)) > 0;
    }

    public Task<string> BeginAsync(long originalSaleId, string kind, long amountCents, CancellationToken ct = default)
        => IoQueue.RunAsync(async () =>
        {
            var id = Guid.NewGuid().ToString("N");
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                INSERT INTO card_refund_attempts(id,original_sale_id,kind,amount_cents,state,created_at)
                VALUES($id,$sale,$kind,$amount,'UNKNOWN',$now);
                """;
            q.Parameters.AddWithValue("$id", id);
            q.Parameters.AddWithValue("$sale", originalSaleId);
            q.Parameters.AddWithValue("$kind", kind);
            q.Parameters.AddWithValue("$amount", amountCents);
            q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
            try
            {
                await q.ExecuteNonQueryAsync(ct);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19 &&
                ex.Message.Contains("ux_card_refund_one_unknown_per_sale"))
            {
                throw new InvalidOperationException("Für diesen Bon läuft bereits eine ungeklärte Kartenerstattung.");
            }
            return id;
        });

    public Task ClearAsync(string attemptId, CancellationToken ct = default)
        => IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = "DELETE FROM card_refund_attempts WHERE id=$id AND state='UNKNOWN';";
            q.Parameters.AddWithValue("$id", attemptId);
            await q.ExecuteNonQueryAsync(ct);
        });

    public async Task<IReadOnlyList<CardRefundAttempt>> GetUnresolvedAsync(CancellationToken ct = default)
    {
        var result = new List<CardRefundAttempt>();
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT a.id,a.original_sale_id,COALESCE(s.receipt_number,0),a.kind,a.amount_cents,a.created_at
            FROM card_refund_attempts a
            LEFT JOIN sales s ON s.id=a.original_sale_id
            WHERE a.state='UNKNOWN'
            ORDER BY a.created_at;
            """;
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            result.Add(new CardRefundAttempt(
                r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetInt64(4),
                DateTimeOffset.Parse(r.GetString(5))));
        }
        return result;
    }

    public Task ResolveAsync(string attemptId, string actor, string note, CancellationToken ct = default)
        => IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = await c.BeginTransactionAsync(ct);
            await using (var q = c.CreateCommand())
            {
                q.Transaction = (SqliteTransaction)tx;
                q.CommandText = """
                    UPDATE card_refund_attempts
                    SET state='RESOLVED', resolved_at=$at, resolved_by=$actor, resolution_note=$note
                    WHERE id=$id AND state='UNKNOWN';
                    """;
                q.Parameters.AddWithValue("$id", attemptId);
                q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
                q.Parameters.AddWithValue("$actor", string.IsNullOrWhiteSpace(actor) ? "unknown" : actor.Trim());
                q.Parameters.AddWithValue("$note", note ?? "");
                if (await q.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException("Diese Kartenerstattung wurde bereits geklärt oder existiert nicht mehr.");
            }
            await tx.CommitAsync(ct);
            await _audit.WriteAsync(actor, "CARD_REFUND_RESOLVED", "CARD_REFUND_ATTEMPT", attemptId, note ?? "", ct);
        });
}

public sealed class SaleRepository : ISaleRepository
{
    private readonly SqliteDatabase _db;
    public SaleRepository(SqliteDatabase db) => _db = db;
public async Task<Sale> CommitAsync(CheckoutSnapshot snapshot, CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var lines = (IReadOnlyList<CartLine>)snapshot.Lines;
        var discountCents = snapshot.DiscountCents;
        var paymentMethod = snapshot.Method;
        var operatorName = snapshot.OperatorName;
        if (lines.Count == 0)
            throw new InvalidOperationException("Warenkorb ist leer.");
        var listSubtotal = lines.Sum(x => x.ListLineTotalCents);
        var promotionDiscount = lines.Sum(x => x.PromotionDiscountCents);
        var subtotal = lines.Sum(x => x.LineTotalCents);
        var total = ReceiptTotals.Total(subtotal, discountCents);
        // R149: returned deposit is never combined with a manual discount, and a
        // payout (negative total) leaves the till only as cash.
        if (discountCents > 0 && lines.Any(PfandProducts.IsDepositReturn))
            throw new InvalidOperationException("Rabatt und Pfand-Rückgabe können nicht auf einem Bon kombiniert werden.");
        if (total < 0 && paymentMethod != PaymentMethod.Cash)
            throw new InvalidOperationException("Eine Pfand-Auszahlung ist nur bar möglich.");
        var now = DateTimeOffset.Now;
        operatorName = (operatorName ?? "").Trim();
        await using var c = _db.OpenConnection();
        await using var tx = await c.BeginTransactionAsync(ct);
        // Retried operation returns the original sale, before any new number or stock update.
        string expectedPriorState;
        await using (var existing = c.CreateCommand())
        {
            existing.Transaction = (SqliteTransaction)tx;
            existing.CommandText = "SELECT state,sale_id,snapshot FROM checkout_operations WHERE id=$id;";
            existing.Parameters.AddWithValue("$id", snapshot.OperationId);
            string state;
            long? previousSale;
            string saved;
            await using (var reader = await existing.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct))
                    throw new InvalidOperationException("Zahlungsjournal fehlt.");
                state = reader.GetString(0);
                previousSale = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                saved = reader.GetString(2);
            }

            // R136: normalised through the current record shape, so a payment
            // journalled by an older build (without fields added since) still
            // matches its own cart.
            var savedSnapshot = System.Text.Json.JsonSerializer.Deserialize<CheckoutSnapshot>(saved);
            if (savedSnapshot is null ||
                System.Text.Json.JsonSerializer.Serialize(savedSnapshot) != System.Text.Json.JsonSerializer.Serialize(snapshot))
                throw new InvalidOperationException("Zahlung und Warenkorb stimmen nicht überein.");
            if (state == "COMMITTED" && previousSale is long existingId)
            {
                await tx.RollbackAsync(ct);
                return await LoadSaleAsync(c, existingId, ct) ?? throw new InvalidOperationException("Originalbon fehlt.");
            }

            // R101: Mixed requires the same terminal-APPROVED state as Card
            // whenever it actually has a card portion to have charged.
            if (state != (snapshot.EffectiveCardPortionCents > 0 ? "APPROVED" : "CASH_READY"))
                throw new InvalidOperationException("Zahlung ungeklärt. Nicht erneut kassieren.");

            expectedPriorState = state;
        }

        FiscalRelease.RequireProduction();
        long receipt;
        await using (var q = c.CreateCommand())
        {
            q.Transaction = (SqliteTransaction)tx;
            q.CommandText = "UPDATE app_sequence SET value=value+1 WHERE key='receipt'; SELECT value FROM app_sequence WHERE key='receipt';";
            receipt = Convert.ToInt64(await q.ExecuteScalarAsync(ct));
        }

        long pickupNumber = 0;
        var edition = "";
        var pickupMode = "SALE";
        var legacyPickupEnabled = true;
        var hasExplicitPickupMode = false;
        await using (var config = c.CreateCommand())
        {
            config.Transaction = (SqliteTransaction)tx;
            config.CommandText = "SELECT key,value FROM app_settings WHERE key IN ('installation.edition','business.mode','imbiss.pickup_number.enabled','imbiss.pickup_number.mode');";
            await using var reader = await config.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var key = reader.GetString(0);
                var value = reader.GetString(1);
                if (key == "installation.edition" && value.Length > 0) edition = value.Trim().ToUpperInvariant();
                else if (key == "business.mode" && edition.Length == 0) edition = value.Trim().ToUpperInvariant();
                else if (key == "imbiss.pickup_number.enabled") legacyPickupEnabled = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                else if (key == "imbiss.pickup_number.mode") { pickupMode=value.Trim().ToUpperInvariant(); hasExplicitPickupMode=true; }
            }
        }
        if (!hasExplicitPickupMode) pickupMode=legacyPickupEnabled ? "SALE" : "OFF";
        if (pickupMode is not ("OFF" or "SALE" or "ORDER")) pickupMode="SALE";

        // ORDER mode gives the number when the order is accepted/parked. Checkout must reuse it.
        if (edition == "IMBISS" && snapshot.ParkedReceiptId is long pickupParkedId)
        {
            await using var existingPickup=c.CreateCommand(); existingPickup.Transaction=(SqliteTransaction)tx;
            existingPickup.CommandText="SELECT pickup_number FROM parked_receipts WHERE id=$id AND status='OPEN';";
            existingPickup.Parameters.AddWithValue("$id",pickupParkedId);
            var existing=await existingPickup.ExecuteScalarAsync(ct);
            if (existing is not null && existing != DBNull.Value) pickupNumber=Convert.ToInt64(existing);
        }
        if (edition == "IMBISS" && pickupNumber == 0 && pickupMode == "SALE")
        {
            // Operational queue number only - it never replaces the immutable
            // fiscal receipt number. R124: counts per service period (since the
            // last Tagesabschluss), no longer resetting at midnight mid-service.
            pickupNumber = await PickupSequence.NextAsync(c, (SqliteTransaction)tx, "", ct);
        }

        long saleId;
        await using (var q = c.CreateCommand())
        {
            q.Transaction = (SqliteTransaction)tx;
            q.CommandText = """
                INSERT INTO sales(
                    receipt_number,pickup_number,created_at,payment_method,
                    subtotal_cents,discount_cents,total_cents,fiscal_status,
                    list_subtotal_cents,promotion_discount_cents,
                    transaction_type,original_sale_id,cash_portion_cents,card_portion_cents,im_haus,started_at)
                VALUES(
                    $r,$pickup,$d,$p,
                    $s,$x,$t,'TEST_TSE_NOT_CONNECTED',
                    $list,$promotion,
                    'SALE',NULL,$cash,$card,$imHaus,$started);
                SELECT last_insert_rowid();
                """;
            q.Parameters.AddWithValue("$r", receipt);
            q.Parameters.AddWithValue("$pickup", pickupNumber);
            q.Parameters.AddWithValue("$d", now.ToString("O"));
            q.Parameters.AddWithValue("$p", paymentMethod.ToString().ToUpperInvariant());
            q.Parameters.AddWithValue("$s", subtotal);
            q.Parameters.AddWithValue("$x", discountCents);
            q.Parameters.AddWithValue("$t", total);
            q.Parameters.AddWithValue("$list", listSubtotal);
            q.Parameters.AddWithValue("$promotion", promotionDiscount);
            q.Parameters.AddWithValue("$cash", snapshot.EffectiveCashPortionCents);
            q.Parameters.AddWithValue("$card", snapshot.EffectiveCardPortionCents);
            q.Parameters.AddWithValue("$imHaus", snapshot.ImHaus ? 1 : 0);
            // R136: the first position of the Vorgang; without a tracked start
            // (a legacy recovery) the Vorgang is taken to begin now.
            q.Parameters.AddWithValue("$started", (snapshot.StartedAt ?? now).ToString("O"));
            saleId = Convert.ToInt64(await q.ExecuteScalarAsync(ct));
        }

        await using (var actor = c.CreateCommand())
        {
            actor.Transaction = (SqliteTransaction)tx;
            actor.CommandText = "INSERT INTO sale_operators(sale_id,operator_name) VALUES($sale,$operator);";
            actor.Parameters.AddWithValue("$sale", saleId);
            actor.Parameters.AddWithValue("$operator", operatorName);
            await actor.ExecuteNonQueryAsync(ct);
        }

        foreach (var line in lines)
        {
            await using var q = c.CreateCommand();
            q.Transaction = (SqliteTransaction)tx;
            q.CommandText = """
                INSERT INTO sale_items(
                  sale_id,product_id,product_name,variant_name,barcode,quantity,quantity_milli,
                  unit_price_cents,vat_rate,pfand_cents,line_total_cents,
                  list_unit_price_cents,list_line_total_cents,
                  promotion_id,promotion_name,promotion_percent,
                  promotion_discount_unit_cents,promotion_discount_cents,
                  promotion_start_date,promotion_end_date,vat_allocations_json,menu_components_json)
                VALUES(
                  $sale,$product,$name,$variant,$barcode,$qty,$qtyMilli,
                  $price,$vat,$pfand,$total,
                  $listUnit,$listTotal,
                  $promotionId,$promotionName,$promotionPercent,
                  $promotionUnit,$promotionTotal,
                  $promotionStart,$promotionEnd,$vatAllocations,$menuComponents);
                """;
            q.Parameters.AddWithValue("$sale", saleId);
            q.Parameters.AddWithValue("$product", line.ProductId);
            q.Parameters.AddWithValue("$name", line.ProductName);
            q.Parameters.AddWithValue("$variant", line.VariantName);
            q.Parameters.AddWithValue("$barcode", line.Barcode);
            q.Parameters.AddWithValue("$qty", Convert.ToDouble(line.Quantity));
            q.Parameters.AddWithValue("$qtyMilli", QuantityStorage.ToMilli(line.Quantity));
            q.Parameters.AddWithValue("$price", line.UnitPriceCents);
            q.Parameters.AddWithValue("$vat", line.VatRate);
            q.Parameters.AddWithValue("$pfand", line.PfandCents);
            q.Parameters.AddWithValue("$total", line.LineTotalCents);
            q.Parameters.AddWithValue("$listUnit", line.EffectiveListUnitPriceCents);
            q.Parameters.AddWithValue("$listTotal", line.ListLineTotalCents);
            q.Parameters.AddWithValue("$promotionId", line.PromotionId);
            q.Parameters.AddWithValue("$promotionName", line.PromotionName);
            q.Parameters.AddWithValue("$promotionPercent", line.PromotionPercent);
            q.Parameters.AddWithValue("$promotionUnit", line.PromotionDiscountUnitCents);
            q.Parameters.AddWithValue("$promotionTotal", line.PromotionDiscountCents);
            q.Parameters.AddWithValue("$promotionStart", line.PromotionStartDate);
            q.Parameters.AddWithValue("$promotionEnd", line.PromotionEndDate);
            q.Parameters.AddWithValue("$vatAllocations", VatAllocationStorage.Serialize(line));
            q.Parameters.AddWithValue("$menuComponents", MenuComponentStorage.Serialize(line));
            await q.ExecuteNonQueryAsync(ct);
            // R153: selected menu articles are immutable per sale line.
            // Use the captured choice/fixed-component snapshot for stock; only
            // legacy/static lines without a snapshot fall back to the current recipe.
            if (line.ProductId > 0)
            {
                var components = await StockComponentsAsync(
                    c,
                    (SqliteTransaction)tx,
                    line,
                    ct);
                foreach(var component in components)
                {
                    await using var stock = c.CreateCommand();
                    stock.Transaction = (SqliteTransaction)tx;
                    stock.CommandText = "UPDATE products SET stock_quantity=COALESCE(stock_quantity,0)-$qty,stock_milli=COALESCE(stock_milli,0)-$qtyMilli WHERE id=$id;";
                    var stockDelta = line.Quantity * component.Quantity;
                    stock.Parameters.AddWithValue("$qty", Convert.ToDouble(stockDelta));
                    stock.Parameters.AddWithValue("$qtyMilli", QuantityStorage.ToMilli(stockDelta));
                    stock.Parameters.AddWithValue("$id", component.Id);
                    await stock.ExecuteNonQueryAsync(ct);
                }
            }
        }

        // R143: the positions cancelled during capture, with the receipt.
        await CancelledPositionStore.InsertAsync(c, (SqliteTransaction)tx, CancelledPositionStore.Sales, saleId, snapshot.CancelledLines, ct);

        if (snapshot.ParkedReceiptId is long parkedId)
        {
            await using var park = c.CreateCommand();
            park.Transaction = (SqliteTransaction)tx;
            park.CommandText = "UPDATE parked_receipts SET status='CASHED',cashed_sale_id=$sale,cashed_at=$now,updated_at=$now WHERE id=$id AND status='OPEN';";
            park.Parameters.AddWithValue("$sale", saleId);
            park.Parameters.AddWithValue("$now", now.ToString("O"));
            park.Parameters.AddWithValue("$id", parkedId);
            if (await park.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Geparkter Bon wurde bereits abgeschlossen.");
        }

        await using (var done = c.CreateCommand())
        {
            done.Transaction = (SqliteTransaction)tx;
            done.CommandText = "UPDATE checkout_operations SET state='COMMITTED',sale_id=$sale,updated_at=$now WHERE id=$id AND state=$expected;";
            done.Parameters.AddWithValue("$sale", saleId);
            done.Parameters.AddWithValue("$now", now.ToString("O"));
            done.Parameters.AddWithValue("$id", snapshot.OperationId);
            done.Parameters.AddWithValue("$expected", expectedPriorState);
            // Same optimistic-concurrency guard as every other CheckoutJournal
            // transition: a concurrent commit of the same operation must not be
            // able to silently overwrite this sale_id with a second sale.
            if (await done.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Zahlung wurde zwischenzeitlich bereits abgeschlossen.");
        }

        TorCloudOutbox.EnqueueSale(c,(SqliteTransaction)tx,snapshot,receipt,pickupNumber,now);
        await tx.CommitAsync(ct);
        return new Sale
        {
            Id = saleId,
            ReceiptNumber = receipt,
            PickupNumber = pickupNumber,
            CreatedAt = now,
            PaymentMethod = paymentMethod,
            DiscountCents = discountCents,
            ListSubtotalCents = listSubtotal,
            PromotionDiscountCents = promotionDiscount,
            TotalCents = total,
            TransactionType = "SALE",
            Lines = lines.ToArray(),
            OperatorName = operatorName
        };
    });
}public async Task<Sale?> GetLastAsync(CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = "SELECT id FROM sales ORDER BY receipt_number DESC,id DESC LIMIT 1;";
        var value = await q.ExecuteScalarAsync(ct);
        return value is null ? null : await LoadSaleAsync(c, Convert.ToInt64(value), ct);
    });
}public async Task<Sale?> GetByIdAsync(long id, CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        await using var c = _db.OpenConnection();
        return await LoadSaleAsync(c, id, ct);
    });
}public async Task<IReadOnlyList<Sale>> SearchHistoryAsync(DateOnly from, DateOnly to,
    long? receiptNumber = null, PaymentMethod? method = null, CancellationToken ct = default)
{
    if (to < from) throw new ArgumentException("Das Enddatum liegt vor dem Startdatum.");
    if (receiptNumber is <= 0) throw new ArgumentException("Die Bonnummer muss positiv sein.");
    if (method.HasValue && !Enum.IsDefined(method.Value)) throw new ArgumentException("Unbekannte Zahlart.");
    return await IoQueue.RunAsync(async () =>
    {
        await using var c = _db.OpenConnection();
        var ids = new List<long>();
        await using (var q = c.CreateCommand())
        {
            // Calendar date as recorded on the original receipt; independent of today's UTC offset.
            // Kept as substr(created_at,1,10) BETWEEN rather than a text range: it only reads
            // the fixed "YYYY-MM-DD" prefix, so it stays correct even for created_at values
            // that don't carry the app's usual full fractional-second precision (e.g. imported
            // history). A range-bound rewrite would need every stored row to share the exact
            // same text width as the computed boundary, which isn't guaranteed.
            // Ordering uses the generated created_at_utc column instead of julianday(created_at):
            // both are equally correct across a DST transition, but only the former is indexable.
            var showWholeDay = from == to && receiptNumber is null && method is null;
            q.CommandText = showWholeDay
                ? """
                    SELECT id FROM sales
                    WHERE substr(created_at,1,10) = $from
                    ORDER BY created_at_utc DESC, id DESC;
                    """
                : """
                    SELECT id FROM sales
                    WHERE substr(created_at,1,10) >= $from AND substr(created_at,1,10) <= $to
                      AND ($number IS NULL OR receipt_number = $number)
                      AND ($method IS NULL OR payment_method = $method)
                    ORDER BY created_at_utc DESC, id DESC LIMIT 201;
                    """;
            q.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            q.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            q.Parameters.AddWithValue("$number", (object?)receiptNumber ?? DBNull.Value);
            q.Parameters.AddWithValue("$method", method is null ? DBNull.Value : method.Value.ToString().ToUpperInvariant());
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ids.Add(r.GetInt64(0));
        }
        var result = new List<Sale>();
        foreach (var id in ids)
        {
            var sale = await LoadSaleAsync(c, id, ct);
            if (sale is not null) result.Add(sale);
        }
        return result;
    });
}
// R122 (İ6): GetDailySinceLastZAsync was removed - see the note in
// TorPos.Core/Services.cs for why a dead method with a wrong period rule was
// worse than no method.
public async Task RecordDailyClosingAsync(string operatorName, CancellationToken ct = default)
{
    await IoQueue.RunAsync(async () =>
    {
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            INSERT INTO daily_closings(closed_at,operator_name,close_type)
            VALUES($closed,$operator,'Z_REPORT');
            """;
        q.Parameters.AddWithValue("$closed", DateTimeOffset.Now.ToString("O"));
        q.Parameters.AddWithValue("$operator", (operatorName ?? "").Trim());
        await q.ExecuteNonQueryAsync(ct);
    });
}
    private static async Task<Sale?> LoadSaleAsync(
        SqliteConnection c,
        long saleId,
        CancellationToken ct)
    {
        Sale? sale = null;
        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT s.receipt_number,s.pickup_number,s.created_at,s.payment_method,
                       s.discount_cents,s.total_cents,s.fiscal_status,
                       COALESCE(o.operator_name,''),
                       COALESCE(s.list_subtotal_cents,0),
                       COALESCE(s.promotion_discount_cents,0),
                       COALESCE(s.transaction_type,'SALE'),
                       s.original_sale_id,
                       orig.receipt_number,
                       COALESCE(t.client_id,''),
                       COALESCE(t.transaction_number,''),
                       COALESCE(t.signature_counter,''),
                       COALESCE(t.serial_number,''),
                       COALESCE(t.signature,''),
                       COALESCE(t.log_time,''),
                       COALESCE(t.outage,0),
                       COALESCE(s.cash_portion_cents,0),
                       COALESCE(s.card_portion_cents,0),
                       s.im_haus,
                       s.started_at,
                       COALESCE(t.start_log_time,''),
                       COALESCE(
                         (SELECT COALESCE(NULLIF(b.start_log_time,''), b.started_at)
                          FROM order_bestellungen b JOIN parked_receipts op ON op.id=b.parked_receipt_id
                          WHERE op.cashed_sale_id=s.id ORDER BY b.sequence LIMIT 1),
                         (SELECT COALESCE(NULLIF(op.tse_start_log_time,''), op.vorgang_started_at, op.created_at)
                          FROM parked_receipts op
                          WHERE op.cashed_sale_id=s.id AND (op.tse_transaction_number<>'' OR op.tse_outage=1) LIMIT 1))
                FROM sales s
                LEFT JOIN sale_operators o ON o.sale_id=s.id
                LEFT JOIN sale_tse_signatures t ON t.sale_id=s.id
                LEFT JOIN sales orig ON orig.id=s.original_sale_id
                WHERE s.id=$id;
                """;
            q.Parameters.AddWithValue("$id", saleId);
            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return null;

            sale = new Sale
            {
                Id = saleId,
                ReceiptNumber = r.GetInt64(0),
                PickupNumber = r.GetInt64(1),
                CreatedAt = DateTimeOffset.Parse(r.GetString(2)),
                PaymentMethod = r.GetString(3).ToUpperInvariant() switch
                {
                    "CARD" => PaymentMethod.Card,
                    "MIXED" => PaymentMethod.Mixed,
                    _ => PaymentMethod.Cash
                },
                DiscountCents = r.GetInt64(4),
                TotalCents = r.GetInt64(5),
                FiscalStatus = r.GetString(6),
                OperatorName = r.GetString(7),
                ListSubtotalCents = r.GetInt64(8),
                PromotionDiscountCents = r.GetInt64(9),
                TransactionType = r.GetString(10),
                OriginalSaleId = r.IsDBNull(11) ? null : r.GetInt64(11),
                OriginalReceiptNumber = r.IsDBNull(12) ? null : r.GetInt64(12),
                TseClientId = r.GetString(13),
                TseTransactionNumber = r.GetString(14),
                TseSignatureCounter = r.GetString(15),
                TseSerialNumber = r.GetString(16),
                TseSignature = r.GetString(17),
                TseLogTime = string.IsNullOrWhiteSpace(r.GetString(18)) ? null : DateTimeOffset.Parse(r.GetString(18)),
                TseOutage = r.GetInt64(19) != 0,
                CashPortionCents = r.GetInt64(20),
                CardPortionCents = r.GetInt64(21),
                ImHaus = r.IsDBNull(22) ? null : r.GetInt64(22) != 0,
                StartedAt = r.IsDBNull(23) ? null : DateTimeOffset.Parse(r.GetString(23)),
                TseStartLogTime = string.IsNullOrWhiteSpace(r.GetString(24)) ? null : DateTimeOffset.Parse(r.GetString(24)),
                OrderStartedAt = r.IsDBNull(25) ? null : DateTimeOffset.Parse(r.GetString(25))
            };
        }

        var lines = new List<CartLine>();
        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT id,product_id,product_name,variant_name,barcode,
                       CASE WHEN COALESCE(quantity_milli,0)<>0 THEN quantity_milli ELSE CAST(ROUND(quantity*1000.0) AS INTEGER) END,
                       unit_price_cents,vat_rate,pfand_cents,
                       list_unit_price_cents,
                       promotion_id,promotion_name,promotion_percent,
                       promotion_discount_unit_cents,
                       promotion_start_date,promotion_end_date,
                       COALESCE(vat_allocations_json,''),
                       COALESCE(menu_components_json,''),
                       COALESCE((SELECT p.unit FROM products p WHERE p.id=sale_items.product_id),'Stück')
                FROM sale_items WHERE sale_id=$sale ORDER BY id;
                """;
            q.Parameters.AddWithValue("$sale", saleId);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                lines.Add(new CartLine
                {
                    SaleItemId = r.GetInt64(0),
                    ProductId = r.GetInt64(1),
                    ProductName = r.GetString(2),
                    VariantName = r.GetString(3),
                    Barcode = r.GetString(4),
                    Quantity = QuantityStorage.FromMilli(r.GetInt64(5)),
                    UnitPriceCents = r.GetInt64(6),
                    VatRate = Convert.ToDecimal(r.GetDouble(7)),
                    PfandCents = r.GetInt64(8),
                    ListUnitPriceCents = r.GetInt64(9) > 0
                        ? r.GetInt64(9)
                        : r.GetInt64(6) + r.GetInt64(13),
                    PromotionId = r.GetInt64(10),
                    PromotionName = r.GetString(11),
                    PromotionPercent = r.GetInt32(12),
                    PromotionDiscountUnitCents = r.GetInt64(13),
                    PromotionStartDate = r.GetString(14),
                    PromotionEndDate = r.GetString(15),
                    VatAllocations = VatAllocationStorage.Deserialize(r.GetString(16)),
                    MenuComponents = MenuComponentStorage.Deserialize(r.GetString(17)),
                    Unit = r.GetString(18)
                });
            }
        }

        sale.Lines = lines;
        sale.CancelledLines = await CancelledPositionStore.LoadAsync(c, CancelledPositionStore.Sales, saleId, ct);

        if (sale.ListSubtotalCents <= 0)
            sale.ListSubtotalCents = lines.Sum(x => x.ListLineTotalCents);

        if (sale.PromotionDiscountCents <= 0)
            sale.PromotionDiscountCents = lines.Sum(x => x.PromotionDiscountCents);

        return sale;
    }
public async Task<string?> CheckReversalAllowedAsync(long originalSaleId, bool forFullStorno, CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        await using var c = _db.OpenConnection();

        var original = await LoadSaleAsync(c, originalSaleId, ct);
        if (original is null)
            return "Ursprungsbon wurde nicht gefunden.";
        if (original.CreatedAt.Date != DateTimeOffset.Now.Date)
            return "BON STORNO / TEILRETOURE ist nur am Verkaufstag möglich.";

        bool hasStorno, hasReturn;
        await using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT COUNT(*) FROM sales WHERE original_sale_id=$id AND transaction_type='STORNO';";
            q.Parameters.AddWithValue("$id", originalSaleId);
            hasStorno = Convert.ToInt64(await q.ExecuteScalarAsync(ct)) > 0;
        }
        await using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT COUNT(*) FROM sales WHERE original_sale_id=$id AND transaction_type='RETURN';";
            q.Parameters.AddWithValue("$id", originalSaleId);
            hasReturn = Convert.ToInt64(await q.ExecuteScalarAsync(ct)) > 0;
        }

        if (hasStorno)
            return "Dieser Bon wurde bereits vollständig storniert.";
        if (forFullStorno && hasReturn)
            return "Für diesen Bon existiert bereits eine Teilretoure - BON STORNO ist gesperrt, um eine doppelte Erstattung zu vermeiden. Verbleibende Positionen einzeln per Teilretoure zurückgeben.";
        return null;
    });
}

public async Task<Sale> RecordStornoAsync(long originalSaleId, string actor, string reason, string cardRefundEvidence = "", CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        await using var c = _db.OpenConnection();
        await using var tx = await c.BeginTransactionAsync(ct);

        var original = await LoadSaleAsync(c, originalSaleId, ct)
            ?? throw new InvalidOperationException("Ursprungsbon wurde nicht gefunden.");

        if (original.TransactionType != "SALE")
            throw new InvalidOperationException("Nur ein regulärer Verkauf kann storniert werden; dieser Bon ist selbst bereits eine Gegenbuchung.");
        if (original.CreatedAt.Date != DateTimeOffset.Now.Date)
            throw new InvalidOperationException("BON STORNO ist nur am Verkaufstag möglich.");

        var originalCashPortion = original.EffectiveCashPortionCents;
        var originalCardPortion = original.EffectiveCardPortionCents;

        // R102: a card component (pure KARTE or the card portion of a
        // Mixed sale) needs its terminal refund CONFIRMED before this is
        // ever called - see CheckoutApplicationService.
        // RefundStornoCardPortionAsync. Structurally refuses to record a
        // Storno against real money that was never actually returned.
        if (originalCardPortion > 0 && string.IsNullOrWhiteSpace(cardRefundEvidence))
            throw new InvalidOperationException("Karten-Anteil dieses Bons wurde noch nicht am Terminal erstattet. BON STORNO abgebrochen.");

        // R107: also rejects if this original already has ANY Teilretoure
        // against it - a full BON STORNO after a partial return would
        // refund the WHOLE original.TotalCents on top of what the
        // Teilretoure already refunded, a genuine double-refund (found by
        // the user's own source review). This is the authoritative,
        // transactional twin of CheckReversalAllowedAsync's pre-check.
        await using (var existing = c.CreateCommand())
        {
            existing.Transaction = (SqliteTransaction)tx;
            existing.CommandText = "SELECT COUNT(*) FROM sales WHERE original_sale_id=$id AND transaction_type IN ('STORNO','RETURN');";
            existing.Parameters.AddWithValue("$id", originalSaleId);
            if (Convert.ToInt64(await existing.ExecuteScalarAsync(ct)) > 0)
                throw new InvalidOperationException("Dieser Bon wurde bereits storniert oder teilweise retourniert. BON STORNO ist für einen bereits (teil-)stornierten Bon gesperrt.");
        }

        // Same build-level circuit breaker every other real fiscal booking
        // goes through - a Storno is just as much a Produktivbuchung as the
        // sale it reverses.
        FiscalRelease.RequireProduction();

        var now = DateTimeOffset.Now;
        long receipt;
        await using (var q = c.CreateCommand())
        {
            q.Transaction = (SqliteTransaction)tx;
            q.CommandText = "UPDATE app_sequence SET value=value+1 WHERE key='receipt'; SELECT value FROM app_sequence WHERE key='receipt';";
            receipt = Convert.ToInt64(await q.ExecuteScalarAsync(ct));
        }

        // Mirror the original's own pre-discount/discount/post-discount split
        // exactly (not just its final total_cents) - BusinessManagementService's
        // per-VAT-rate tax breakdown recomputes subtotal from the mirrored
        // sale_items and nets out sales.discount_cents itself; a Storno whose
        // discount_cents is wrongly 0 would over-subtract VAT whenever the
        // original sale carried a manual discount.
        var stornoSubtotal = original.Lines.Sum(x => x.LineTotalCents);

        long saleId;
        await using (var q = c.CreateCommand())
        {
            q.Transaction = (SqliteTransaction)tx;
            // R102: mirrors the original's own payment_method/cash+card
            // portions instead of hardcoding 'CASH' - a Storno against a
            // KARTE or Mixed original now correctly carries the same split,
            // so every R101 report query (which sums these two columns)
            // attributes the reversal to the right Bar/Karte bucket.
            q.CommandText = """
                INSERT INTO sales(
                    receipt_number,pickup_number,created_at,payment_method,
                    subtotal_cents,discount_cents,total_cents,fiscal_status,
                    list_subtotal_cents,promotion_discount_cents,
                    transaction_type,original_sale_id,cash_portion_cents,card_portion_cents,im_haus,started_at)
                VALUES(
                    $r,0,$d,$pm,
                    $subtotal,$discount,$total,'TEST_TSE_NOT_CONNECTED',
                    $list,$promotion,
                    'STORNO',$original,$cash,$card,$imHaus,$d);
                SELECT last_insert_rowid();
                """;
            q.Parameters.AddWithValue("$r", receipt);
            q.Parameters.AddWithValue("$d", now.ToString("O"));
            q.Parameters.AddWithValue("$pm", original.PaymentMethod.ToString().ToUpperInvariant());
            q.Parameters.AddWithValue("$subtotal", stornoSubtotal);
            q.Parameters.AddWithValue("$discount", original.DiscountCents);
            q.Parameters.AddWithValue("$total", original.TotalCents);
            q.Parameters.AddWithValue("$list", original.ListSubtotalCents);
            q.Parameters.AddWithValue("$promotion", original.PromotionDiscountCents);
            q.Parameters.AddWithValue("$original", originalSaleId);
            q.Parameters.AddWithValue("$cash", originalCashPortion);
            q.Parameters.AddWithValue("$card", originalCardPortion);
            q.Parameters.AddWithValue("$imHaus", original.ImHaus is bool stornoImHaus ? (stornoImHaus ? 1 : 0) : DBNull.Value);
            try
            {
                saleId = Convert.ToInt64(await q.ExecuteScalarAsync(ct));
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19 &&
                ex.Message.Contains("ux_sales_one_storno_per_original"))
            {
                // The COUNT check above already covers the normal sequential
                // case; this index is the authoritative guard against two
                // concurrent BON STORNO attempts on the same original sale
                // racing past that check at the same time.
                throw new InvalidOperationException("Dieser Bon wurde bereits storniert.");
            }
        }

        foreach (var line in original.Lines)
        {
            await using (var q = c.CreateCommand())
            {
                q.Transaction = (SqliteTransaction)tx;
                q.CommandText = """
                    INSERT INTO sale_items(
                      sale_id,product_id,product_name,variant_name,barcode,quantity,quantity_milli,
                      unit_price_cents,vat_rate,pfand_cents,line_total_cents,
                      list_unit_price_cents,list_line_total_cents,
                      promotion_id,promotion_name,promotion_percent,
                      promotion_discount_unit_cents,promotion_discount_cents,
                      promotion_start_date,promotion_end_date,vat_allocations_json,menu_components_json)
                    VALUES(
                      $sale,$product,$name,$variant,$barcode,$qty,$qtyMilli,
                      $price,$vat,$pfand,$total,
                      $listUnit,$listTotal,
                      $promotionId,$promotionName,$promotionPercent,
                      $promotionUnit,$promotionTotal,
                      $promotionStart,$promotionEnd,$vatAllocations,$menuComponents);
                    """;
                q.Parameters.AddWithValue("$sale", saleId);
                q.Parameters.AddWithValue("$product", line.ProductId);
                q.Parameters.AddWithValue("$name", line.ProductName);
                q.Parameters.AddWithValue("$variant", line.VariantName);
                q.Parameters.AddWithValue("$barcode", line.Barcode);
                q.Parameters.AddWithValue("$qty", Convert.ToDouble(line.Quantity));
                q.Parameters.AddWithValue("$qtyMilli", QuantityStorage.ToMilli(line.Quantity));
                q.Parameters.AddWithValue("$price", line.UnitPriceCents);
                q.Parameters.AddWithValue("$vat", line.VatRate);
                q.Parameters.AddWithValue("$pfand", line.PfandCents);
                q.Parameters.AddWithValue("$total", line.LineTotalCents);
                q.Parameters.AddWithValue("$listUnit", line.EffectiveListUnitPriceCents);
                q.Parameters.AddWithValue("$listTotal", line.ListLineTotalCents);
                q.Parameters.AddWithValue("$promotionId", line.PromotionId);
                q.Parameters.AddWithValue("$promotionName", line.PromotionName);
                q.Parameters.AddWithValue("$promotionPercent", line.PromotionPercent);
                q.Parameters.AddWithValue("$promotionUnit", line.PromotionDiscountUnitCents);
                q.Parameters.AddWithValue("$promotionTotal", line.PromotionDiscountCents);
                q.Parameters.AddWithValue("$promotionStart", line.PromotionStartDate);
                q.Parameters.AddWithValue("$promotionEnd", line.PromotionEndDate);
                q.Parameters.AddWithValue("$vatAllocations", VatAllocationStorage.Serialize(line));
                q.Parameters.AddWithValue("$menuComponents", MenuComponentStorage.Serialize(line));
                await q.ExecuteNonQueryAsync(ct);
            }

            // Mirror image of CommitAsync's stock decrement: give reversed
            // stock back to the same components the original sale consumed.
            await ReverseStockAsync(c, (SqliteTransaction)tx, line, line.Quantity, ct);
        }

        // R90: without this, the actor who authorized the BON STORNO was only
        // ever recoverable from audit_log's free-text details - the sale row
        // itself (and anything joining sale_operators, like
        // BuildOperatorSettlementAsync/the R88 Storno-/Retourenjournal)
        // couldn't attribute it to anyone. Same shape as CommitAsync's own
        // sale_operators insert.
        await using (var op = c.CreateCommand())
        {
            op.Transaction = (SqliteTransaction)tx;
            op.CommandText = "INSERT INTO sale_operators(sale_id,operator_name) VALUES($sale,$operator);";
            op.Parameters.AddWithValue("$sale", saleId);
            op.Parameters.AddWithValue("$operator", string.IsNullOrWhiteSpace(actor) ? "unknown" : actor.Trim());
            await op.ExecuteNonQueryAsync(ct);
        }

        await using (var log = c.CreateCommand())
        {
            log.Transaction = (SqliteTransaction)tx;
            log.CommandText = """
                INSERT INTO audit_log(created_at,actor,event_type,entity_type,entity_id,details)
                VALUES($at,$actor,'SALE_STORNO','SALE',$id,$details);
                """;
            log.Parameters.AddWithValue("$at", now.ToString("O"));
            log.Parameters.AddWithValue("$actor", string.IsNullOrWhiteSpace(actor) ? "unknown" : actor.Trim());
            log.Parameters.AddWithValue("$id", saleId.ToString());
            log.Parameters.AddWithValue("$details",
                $"original_sale_id={originalSaleId}; original_receipt={original.ReceiptNumber}; " +
                $"amount_cents={original.TotalCents}; reason={reason}" +
                (originalCardPortion > 0 ? $"; card_refund_evidence={cardRefundEvidence}" : ""));
            await log.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return await LoadSaleAsync(c, saleId, ct)
            ?? throw new InvalidOperationException("Stornobon konnte nicht geladen werden.");
    });
}
public async Task<Sale> RecordReturnAsync(long originalSaleId, IReadOnlyList<ReturnLineRequest> lines, string actor, string reason, string cardRefundEvidence = "", CancellationToken ct = default)
{
    if (lines is null || lines.Count == 0)
        throw new InvalidOperationException("Mindestens eine Position für die Retoure auswählen.");
    if (lines.Any(x => x.Quantity <= 0))
        throw new InvalidOperationException("Retoure-Menge muss größer als 0 sein.");
    if (lines.Select(x => x.SaleItemId).Distinct().Count() != lines.Count)
        throw new InvalidOperationException("Jede Position darf in einer Retoure nur einmal vorkommen.");

    return await IoQueue.RunAsync(async () =>
    {
        await using var c = _db.OpenConnection();
        await using var tx = await c.BeginTransactionAsync(ct);

        var original = await LoadSaleAsync(c, originalSaleId, ct)
            ?? throw new InvalidOperationException("Ursprungsbon wurde nicht gefunden.");

        if (original.TransactionType != "SALE")
            throw new InvalidOperationException("Nur ein regulärer Verkauf kann teilweise retourniert werden.");
        if (original.CreatedAt.Date != DateTimeOffset.Now.Date)
            throw new InvalidOperationException("TEILRETOURE ist nur am Verkaufstag möglich.");

        // R107: a fully storno'd sale has already had its ENTIRE amount
        // refunded - a Teilretoure against it afterward would refund
        // specific lines a SECOND time on top of that. Multiple
        // Teilretouren against the same original (not yet fully storno'd)
        // remain normal and unaffected - only a prior full STORNO blocks.
        await using (var existingStorno = c.CreateCommand())
        {
            existingStorno.Transaction = (SqliteTransaction)tx;
            existingStorno.CommandText = "SELECT COUNT(*) FROM sales WHERE original_sale_id=$id AND transaction_type='STORNO';";
            existingStorno.Parameters.AddWithValue("$id", originalSaleId);
            if (Convert.ToInt64(await existingStorno.ExecuteScalarAsync(ct)) > 0)
                throw new InvalidOperationException("Dieser Bon wurde bereits vollständig storniert. Teilretoure ist dafür gesperrt.");
        }

        var originalCashPortion = original.EffectiveCashPortionCents;

        var originalLinesById = original.Lines.ToDictionary(x => x.SaleItemId);

        foreach (var request in lines)
        {
            if (!originalLinesById.ContainsKey(request.SaleItemId))
                throw new InvalidOperationException($"Position {request.SaleItemId} gehört nicht zu diesem Bon.");
            // R149: returned deposit was paid out; it is not handed back as a Retoure.
            if (PfandProducts.IsDepositReturn(originalLinesById[request.SaleItemId]))
                throw new InvalidOperationException("Eine Pfand-Rückgabe kann nicht retourniert werden.");
        }

        // Same build-level circuit breaker every other real fiscal booking
        // goes through - a Retoure is just as much a Produktivbuchung as
        // the sale it partially reverses.
        FiscalRelease.RequireProduction();

        var now = DateTimeOffset.Now;
        long receipt;
        await using (var q = c.CreateCommand())
        {
            q.Transaction = (SqliteTransaction)tx;
            q.CommandText = "UPDATE app_sequence SET value=value+1 WHERE key='receipt'; SELECT value FROM app_sequence WHERE key='receipt';";
            receipt = Convert.ToInt64(await q.ExecuteScalarAsync(ct));
        }

        long totalCents = 0;
        var returnLines = new List<(CartLine Original, decimal Quantity, long LineTotalCents)>();

        foreach (var request in lines)
        {
            var originalLine = originalLinesById[request.SaleItemId];

            decimal alreadyReturned;
            await using (var q = c.CreateCommand())
            {
                q.Transaction = (SqliteTransaction)tx;
                q.CommandText = """
                    SELECT COALESCE(SUM(CASE WHEN COALESCE(i.quantity_milli,0)<>0 THEN i.quantity_milli ELSE CAST(ROUND(i.quantity*1000.0) AS INTEGER) END),0)
                    FROM sale_items i
                    JOIN sales s ON s.id=i.sale_id
                    WHERE i.original_sale_item_id=$item AND s.transaction_type='RETURN';
                    """;
                q.Parameters.AddWithValue("$item", request.SaleItemId);
                alreadyReturned = QuantityStorage.FromMilli(Convert.ToInt64(await q.ExecuteScalarAsync(ct)));
            }

            var remaining = originalLine.Quantity - alreadyReturned;
            if (request.Quantity > remaining)
                throw new InvalidOperationException(
                    $"Position \"{originalLine.ProductName}\": nur {remaining} von {originalLine.Quantity} für eine Retoure übrig, {request.Quantity} angefordert.");

            var lineTotal = originalLine.LineTotalCentsFor(request.Quantity);
            totalCents += lineTotal;
            returnLines.Add((originalLine, request.Quantity, lineTotal));
        }

        // R106: prorate the ORIGINAL sale's whole-Bon manual discount onto
        // this partial return - without this, returning a single item from
        // a discounted Bon credited back the item's full undiscounted
        // price, more than the customer actually paid for it. Shared with
        // MainWindow.OnPartialReturnClick (which needs the same discounted
        // amount to know how much to refund at the terminal) via
        // DiscountProration, not two independent copies of this formula.
        var originalSubtotal = original.Lines.Sum(x => x.LineTotalCents);
        var discountedTotalCents = DiscountProration.Prorate(totalCents, originalSubtotal, original.TotalCents);
        var returnDiscountCents = Math.Max(0L, totalCents - discountedTotalCents);

        // R102: a partial return of a Mixed original refunds cash/card
        // proportionally to the ORIGINAL sale's own cash/card ratio - there
        // is no unambiguous way to attribute a specific returned LINE to
        // one tender type over the other. Based on the DISCOUNT-ADJUSTED
        // amount actually being refunded, not the raw pre-discount total.
        var returnCashPortion = original.TotalCents > 0
            ? (long)Math.Round((decimal)discountedTotalCents * originalCashPortion / original.TotalCents, MidpointRounding.AwayFromZero)
            : 0;
        var returnCardPortion = discountedTotalCents - returnCashPortion;

        // R102: same structural refusal as RecordStornoAsync - a nonzero
        // card portion of THIS return must already be confirmed refunded
        // at the terminal before the DB reversal is ever recorded.
        if (returnCardPortion > 0 && string.IsNullOrWhiteSpace(cardRefundEvidence))
            throw new InvalidOperationException("Karten-Anteil dieser Retoure wurde noch nicht am Terminal erstattet. Retoure abgebrochen.");

        long saleId;
        await using (var q = c.CreateCommand())
        {
            q.Transaction = (SqliteTransaction)tx;
            q.CommandText = """
                INSERT INTO sales(
                    receipt_number,pickup_number,created_at,payment_method,
                    subtotal_cents,discount_cents,total_cents,fiscal_status,
                    transaction_type,original_sale_id,cash_portion_cents,card_portion_cents,im_haus,started_at)
                VALUES(
                    $r,0,$d,$pm,
                    $subtotal,$discount,$total,'TEST_TSE_NOT_CONNECTED',
                    'RETURN',$original,$cash,$card,$imHaus,$d);
                SELECT last_insert_rowid();
                """;
            q.Parameters.AddWithValue("$r", receipt);
            q.Parameters.AddWithValue("$d", now.ToString("O"));
            q.Parameters.AddWithValue("$pm", original.PaymentMethod.ToString().ToUpperInvariant());
            q.Parameters.AddWithValue("$subtotal", totalCents);
            q.Parameters.AddWithValue("$discount", returnDiscountCents);
            q.Parameters.AddWithValue("$total", discountedTotalCents);
            q.Parameters.AddWithValue("$original", originalSaleId);
            q.Parameters.AddWithValue("$cash", returnCashPortion);
            q.Parameters.AddWithValue("$card", returnCardPortion);
            q.Parameters.AddWithValue("$imHaus", original.ImHaus is bool returnImHaus ? (returnImHaus ? 1 : 0) : DBNull.Value);
            saleId = Convert.ToInt64(await q.ExecuteScalarAsync(ct));
        }

        foreach (var (originalLine, quantity, lineTotal) in returnLines)
        {
            await using (var q = c.CreateCommand())
            {
                q.Transaction = (SqliteTransaction)tx;
                q.CommandText = """
                    INSERT INTO sale_items(
                      sale_id,product_id,product_name,variant_name,barcode,quantity,quantity_milli,
                      unit_price_cents,vat_rate,pfand_cents,line_total_cents,
                      list_unit_price_cents,list_line_total_cents,
                      promotion_id,promotion_name,promotion_percent,
                      promotion_discount_unit_cents,promotion_discount_cents,
                      promotion_start_date,promotion_end_date,
                      vat_allocations_json,menu_components_json,original_sale_item_id)
                    VALUES(
                      $sale,$product,$name,$variant,$barcode,$qty,$qtyMilli,
                      $price,$vat,$pfand,$total,
                      $listUnit,$listTotal,
                      $promotionId,$promotionName,$promotionPercent,
                      $promotionUnit,$promotionTotal,
                      $promotionStart,$promotionEnd,
                      $vatAllocations,$menuComponents,$originalItem);
                    """;
                q.Parameters.AddWithValue("$sale", saleId);
                q.Parameters.AddWithValue("$product", originalLine.ProductId);
                q.Parameters.AddWithValue("$name", originalLine.ProductName);
                q.Parameters.AddWithValue("$variant", originalLine.VariantName);
                q.Parameters.AddWithValue("$barcode", originalLine.Barcode);
                q.Parameters.AddWithValue("$qty", Convert.ToDouble(quantity));
                q.Parameters.AddWithValue("$qtyMilli", QuantityStorage.ToMilli(quantity));
                q.Parameters.AddWithValue("$price", originalLine.UnitPriceCents);
                q.Parameters.AddWithValue("$vat", originalLine.VatRate);
                q.Parameters.AddWithValue("$pfand", originalLine.PfandCents);
                q.Parameters.AddWithValue("$total", lineTotal);
                q.Parameters.AddWithValue("$listUnit", originalLine.EffectiveListUnitPriceCents);
                q.Parameters.AddWithValue("$listTotal", originalLine.ListLineTotalCentsFor(quantity));
                q.Parameters.AddWithValue("$promotionId", originalLine.PromotionId);
                q.Parameters.AddWithValue("$promotionName", originalLine.PromotionName);
                q.Parameters.AddWithValue("$promotionPercent", originalLine.PromotionPercent);
                q.Parameters.AddWithValue("$promotionUnit", originalLine.PromotionDiscountUnitCents);
                q.Parameters.AddWithValue("$promotionTotal", originalLine.PromotionDiscountCentsFor(quantity));
                q.Parameters.AddWithValue("$promotionStart", originalLine.PromotionStartDate);
                q.Parameters.AddWithValue("$promotionEnd", originalLine.PromotionEndDate);
                q.Parameters.AddWithValue("$vatAllocations", VatAllocationStorage.Serialize(originalLine));
                q.Parameters.AddWithValue("$menuComponents", MenuComponentStorage.Serialize(originalLine));
                q.Parameters.AddWithValue("$originalItem", originalLine.SaleItemId);
                await q.ExecuteNonQueryAsync(ct);
            }

            await ReverseStockAsync(c, (SqliteTransaction)tx, originalLine, quantity, ct);
        }

        // R90: same reasoning as RecordStornoAsync - without this the Teilretoure
        // is unattributable at the sale-row level, only in audit_log free text.
        await using (var op = c.CreateCommand())
        {
            op.Transaction = (SqliteTransaction)tx;
            op.CommandText = "INSERT INTO sale_operators(sale_id,operator_name) VALUES($sale,$operator);";
            op.Parameters.AddWithValue("$sale", saleId);
            op.Parameters.AddWithValue("$operator", string.IsNullOrWhiteSpace(actor) ? "unknown" : actor.Trim());
            await op.ExecuteNonQueryAsync(ct);
        }

        await using (var log = c.CreateCommand())
        {
            log.Transaction = (SqliteTransaction)tx;
            log.CommandText = """
                INSERT INTO audit_log(created_at,actor,event_type,entity_type,entity_id,details)
                VALUES($at,$actor,'SALE_RETURN','SALE',$id,$details);
                """;
            log.Parameters.AddWithValue("$at", now.ToString("O"));
            log.Parameters.AddWithValue("$actor", string.IsNullOrWhiteSpace(actor) ? "unknown" : actor.Trim());
            log.Parameters.AddWithValue("$id", saleId.ToString());
            log.Parameters.AddWithValue("$details",
                $"original_sale_id={originalSaleId}; original_receipt={original.ReceiptNumber}; " +
                $"lines={returnLines.Count}; amount_cents={totalCents}; reason={reason}" +
                (returnCardPortion > 0 ? $"; card_refund_evidence={cardRefundEvidence}" : ""));
            await log.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return await LoadSaleAsync(c, saleId, ct)
            ?? throw new InvalidOperationException("Retourenbon konnte nicht geladen werden.");
    });
}
private static async Task<IReadOnlyList<(long Id, decimal Quantity)>> StockComponentsAsync(
    SqliteConnection c,
    SqliteTransaction tx,
    CartLine line,
    CancellationToken ct)
{
    if (line.MenuComponents.Length > 0)
        return line.MenuComponents
            .Select(x => (x.ProductId, x.Quantity))
            .ToArray();

    var components = new List<(long Id, decimal Quantity)>();
    await using (var combo = c.CreateCommand())
    {
        combo.Transaction = tx;
        combo.CommandText = "SELECT component_product_id,CASE WHEN COALESCE(quantity_milli,0)<>0 THEN quantity_milli ELSE CAST(ROUND(quantity*1000.0) AS INTEGER) END FROM product_combo_items WHERE product_id=$id AND COALESCE(choice_group,'')='' ORDER BY sort_order;";
        combo.Parameters.AddWithValue("$id", line.ProductId);
        await using var cr = await combo.ExecuteReaderAsync(ct);
        while (await cr.ReadAsync(ct))
            components.Add((cr.GetInt64(0), QuantityStorage.FromMilli(cr.GetInt64(1))));
    }

    if (components.Count == 0)
        components.Add((line.ProductId, 1m));

    return components;
}

private static async Task ReverseStockAsync(
    SqliteConnection c,
    SqliteTransaction tx,
    CartLine line,
    decimal quantity,
    CancellationToken ct)
{
    if (line.ProductId <= 0)
        return;

    var components = await StockComponentsAsync(c, tx, line, ct);
    foreach (var component in components)
    {
        await using var stock = c.CreateCommand();
        stock.Transaction = tx;
        stock.CommandText = "UPDATE products SET stock_quantity=COALESCE(stock_quantity,0)+$qty,stock_milli=COALESCE(stock_milli,0)+$qtyMilli WHERE id=$id;";
        var stockDelta = quantity * component.Quantity;
        stock.Parameters.AddWithValue("$qty", Convert.ToDouble(stockDelta));
        stock.Parameters.AddWithValue("$qtyMilli", QuantityStorage.ToMilli(stockDelta));
        stock.Parameters.AddWithValue("$id", component.Id);
        await stock.ExecuteNonQueryAsync(ct);
    }
}
public async Task RecordTseResultAsync(long saleId, SaleTseResult result, CancellationToken ct = default)
{
    await IoQueue.RunAsync(async () =>
    {
        await using var c = _db.OpenConnection();

        // R122 (audit finding F6): sale_tse_signatures is written exactly once
        // per sale - signature OR outage - and cannot be updated or deleted
        // afterwards, matching the immutability the sales row itself has via
        // trg_sales_no_update. A sale recorded as an outage therefore cannot be
        // signed later; see the migration-8 comment in SchemaMigrationService
        // for the legal basis (R129: AEAO zu § 146a Nr. 1.14 provides for
        // documenting and marking an outage, not for signing it afterwards).
        //
        // Nothing in this build calls this twice for the same sale. If a future
        // change ever does, it must not surface as a raw "SQLite Error 19:
        // UNIQUE constraint failed" - that reads like a database fault rather
        // than the rule it actually is.
        await using (var existing = c.CreateCommand())
        {
            existing.CommandText = "SELECT outage FROM sale_tse_signatures WHERE sale_id=$id;";
            existing.Parameters.AddWithValue("$id", saleId);
            var current = await existing.ExecuteScalarAsync(ct);
            if (current is not null)
            {
                var wasOutage = Convert.ToInt64(current) == 1;
                throw new InvalidOperationException(
                    $"Für Beleg-ID {saleId} existiert bereits ein endgültiger TSE-Eintrag " +
                    $"({(wasOutage ? "TSE-Ausfall" : "signiert")}). Ein nachträgliches Signieren " +
                    "ist bewusst nicht vorgesehen.");
            }
        }

        await using var q = c.CreateCommand();
        q.CommandText = """
            INSERT INTO sale_tse_signatures(
              sale_id,client_id,transaction_number,signature_counter,
              serial_number,signature,log_time,outage,created_at,start_log_time)
            VALUES($id,$client,$txn,$counter,$serial,$sig,$logtime,$outage,$now,$startlog);
            """;
        q.Parameters.AddWithValue("$id", saleId);
        q.Parameters.AddWithValue("$client", result.ClientId);
        q.Parameters.AddWithValue("$txn", result.TransactionNumber);
        q.Parameters.AddWithValue("$counter", result.SignatureCounter);
        q.Parameters.AddWithValue("$serial", result.SerialNumber);
        q.Parameters.AddWithValue("$sig", result.Signature);
        q.Parameters.AddWithValue("$logtime", result.LogTime?.ToString("O") ?? "");
        q.Parameters.AddWithValue("$outage", result.Signed ? 0 : 1);
        q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        q.Parameters.AddWithValue("$startlog", result.StartLogTime?.ToString("O") ?? "");
        await q.ExecuteNonQueryAsync(ct);
    });
}
}


public sealed class ParkedReceiptRepository : IParkedReceiptRepository
{
    private readonly SqliteDatabase _db;
    public Action? PrintCommitted { private get; set; }
    public ParkedReceiptRepository(SqliteDatabase db) => _db = db;
public async Task<ParkedReceipt> ParkAsync(IReadOnlyList<CartLine> lines, long discountCents, string createdBy, bool assignPickupNumber = false, CancellationToken ct = default, bool training = false, bool orderPrint = false, bool imHaus = false)
{
    return await IoQueue.RunAsync(async () =>
    {
        if (lines.Count == 0)
            throw new InvalidOperationException("Leerer Bon kann nicht geparkt werden.");
        var now = DateTimeOffset.Now;
        var subtotal = lines.Sum(x => x.LineTotalCents);
        var total = ReceiptTotals.Total(subtotal, discountCents);
        await using var c = _db.OpenConnection();
        await using var tx = await c.BeginTransactionAsync(ct);
        long parkNumber;
        await using (var seq = c.CreateCommand())
        {
            seq.Transaction = (SqliteTransaction)tx;
            seq.CommandText = """
                UPDATE app_sequence
                SET value=value+1
                WHERE key='parked_receipt';

                SELECT value
                FROM app_sequence
                WHERE key='parked_receipt';
                """;
            parkNumber = Convert.ToInt64(await seq.ExecuteScalarAsync(ct));
        }

        long pickupNumber=0;
        if (assignPickupNumber)
        {
            // R124: same service-period counter as a direct sale (see PickupSequence).
            pickupNumber=await PickupSequence.NextAsync(c,(SqliteTransaction)tx,training ? "training." : "",ct);
        }

        long parkedId;
        await using (var q = c.CreateCommand())
        {
            q.Transaction = (SqliteTransaction)tx;
            q.CommandText = """
                INSERT INTO parked_receipts(
                  park_number,pickup_number,created_at,updated_at,created_by,
                  subtotal_cents,discount_cents,total_cents,status,is_training,im_haus)
                VALUES($park,$pickup,$created,$updated,$user,$subtotal,$discount,$total,'OPEN',$training,$imHaus);

                SELECT last_insert_rowid();
                """;
            q.Parameters.AddWithValue("$park", parkNumber);
            q.Parameters.AddWithValue("$pickup", pickupNumber);
            q.Parameters.AddWithValue("$created", now.ToString("O"));
            q.Parameters.AddWithValue("$updated", now.ToString("O"));
            q.Parameters.AddWithValue("$user", createdBy ?? "");
            q.Parameters.AddWithValue("$subtotal", subtotal);
            q.Parameters.AddWithValue("$discount", discountCents);
            q.Parameters.AddWithValue("$total", total);
            q.Parameters.AddWithValue("$training", training ? 1 : 0);
            q.Parameters.AddWithValue("$imHaus", imHaus ? 1 : 0);
            parkedId = Convert.ToInt64(await q.ExecuteScalarAsync(ct));
        }

        await InsertItemsAsync(c, (SqliteTransaction)tx, parkedId, lines, ct);
        await OrderPrintOutbox.AuditAsync(c,(SqliteTransaction)tx,parkedId,createdBy??"SYSTEM",orderPrint?"ORDER_ACCEPT":"PARK",ct);
        if(orderPrint)await OrderPrintOutbox.EnqueueAsync(c,(SqliteTransaction)tx,parkedId,"ACCEPT",ct);
        await tx.CommitAsync(ct);
        if(orderPrint) PrintCommitted?.Invoke();
        return new ParkedReceipt
        {
            Id = parkedId,
            ParkNumber = parkNumber,
            PickupNumber = pickupNumber,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = createdBy ?? "",
            DiscountCents = discountCents,
            TotalCents = total,
            Lines = CloneLines(lines),
            ImHaus = imHaus
        };
    });
}public async Task UpdateAsync(long parkedReceiptId, IReadOnlyList<CartLine> lines, long discountCents, CancellationToken ct = default, bool orderPrint = false, string actor = "SYSTEM", bool imHaus = false)
{
    await IoQueue.RunAsync(async () =>
    {
        if (lines.Count == 0)
            throw new InvalidOperationException("Leerer Bon kann nicht geparkt werden.");
        var subtotal = lines.Sum(x => x.LineTotalCents);
        var total = ReceiptTotals.Total(subtotal, discountCents);
        var now = DateTimeOffset.Now;
        await using var c = _db.OpenConnection();
        await using var tx = await c.BeginTransactionAsync(ct);
        await using (var q = c.CreateCommand())
        {
            q.Transaction = (SqliteTransaction)tx;
            q.CommandText = """
                UPDATE parked_receipts
                SET updated_at=$updated,
                    workflow_version=workflow_version+1,
                    preparation_state='ACCEPTED',
                    subtotal_cents=$subtotal,
                    discount_cents=$discount,
                    total_cents=$total,
                    im_haus=$imHaus
                WHERE id=$id AND status='OPEN';
                """;
            q.Parameters.AddWithValue("$updated", now.ToString("O"));
            q.Parameters.AddWithValue("$subtotal", subtotal);
            q.Parameters.AddWithValue("$discount", discountCents);
            q.Parameters.AddWithValue("$total", total);
            q.Parameters.AddWithValue("$imHaus", imHaus ? 1 : 0);
            q.Parameters.AddWithValue("$id", parkedReceiptId);
            if (await q.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Geparkter Bon wurde nicht gefunden.");
        }

        await using (var del = c.CreateCommand())
        {
            del.Transaction = (SqliteTransaction)tx;
            del.CommandText = """
                DELETE FROM parked_receipt_items
                WHERE parked_receipt_id=$id;
                """;
            del.Parameters.AddWithValue("$id", parkedReceiptId);
            await del.ExecuteNonQueryAsync(ct);
        }

        await InsertItemsAsync(c, (SqliteTransaction)tx, parkedReceiptId, lines, ct);
        await OrderPrintOutbox.AuditAsync(c,(SqliteTransaction)tx,parkedReceiptId,actor,"PARK_CHANGE",ct);
        if(orderPrint)await OrderPrintOutbox.EnqueueAsync(c,(SqliteTransaction)tx,parkedReceiptId,"CHANGE",ct);
        await tx.CommitAsync(ct);
        if(orderPrint) PrintCommitted?.Invoke();
    });
}public async Task<IReadOnlyList<ParkedReceipt>> GetOpenAsync(CancellationToken ct = default, bool training = false)
{
    return await IoQueue.RunAsync(async () =>
    {
        var result = new List<ParkedReceipt>();
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT id,park_number,pickup_number,created_at,updated_at,created_by,
                   discount_cents,total_cents,order_note,
                   COALESCE(tse_client_id,''),COALESCE(tse_transaction_number,''),
                   COALESCE(tse_signature_counter,''),COALESCE(tse_serial_number,''),
                   COALESCE(tse_signature,''),COALESCE(tse_log_time,''),COALESCE(tse_outage,0),
                   COALESCE(im_haus,0),vorgang_started_at,COALESCE(tse_start_log_time,'')
            FROM parked_receipts
            WHERE status='OPEN' AND COALESCE(is_training,0)=$training
            ORDER BY created_at;
            """;
        q.Parameters.AddWithValue("$training", training ? 1 : 0);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            result.Add(new ParkedReceipt
            {
                Id = r.GetInt64(0), ParkNumber = r.GetInt64(1), PickupNumber = r.GetInt64(2),
                CreatedAt = DateTimeOffset.Parse(r.GetString(3)), UpdatedAt = DateTimeOffset.Parse(r.GetString(4)),
                CreatedBy = r.GetString(5), DiscountCents = r.GetInt64(6), TotalCents = r.GetInt64(7), OrderNote=r.GetString(8),
                TseClientId = r.GetString(9), TseTransactionNumber = r.GetString(10), TseSignatureCounter = r.GetString(11),
                TseSerialNumber = r.GetString(12), TseSignature = r.GetString(13),
                TseLogTime = string.IsNullOrWhiteSpace(r.GetString(14)) ? null : DateTimeOffset.Parse(r.GetString(14)),
                TseOutage = r.GetInt64(15) != 0,
                ImHaus = r.GetInt64(16) != 0,
                VorgangStartedAt = r.IsDBNull(17) ? null : DateTimeOffset.Parse(r.GetString(17)),
                TseStartLogTime = string.IsNullOrWhiteSpace(r.GetString(18)) ? null : DateTimeOffset.Parse(r.GetString(18))
            });
        }

        foreach (var parked in result)
            parked.Lines = await LoadItemsAsync(c, parked.Id, ct);
        return result;
    });
}public async Task<int> GetOpenCountAsync(CancellationToken ct = default, bool training = false)
{
    return await IoQueue.RunAsync(async () =>
    {
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT COUNT(*)
            FROM parked_receipts
            WHERE status='OPEN' AND COALESCE(is_training,0)=$training;
            """;
        q.Parameters.AddWithValue("$training", training ? 1 : 0);
        return Convert.ToInt32(await q.ExecuteScalarAsync(ct));
    });
}public async Task<ParkedReceipt?> GetOpenByIdAsync(long id, CancellationToken ct = default, bool training = false)
{
    return await IoQueue.RunAsync(async () =>
    {
        await using var c = _db.OpenConnection();
        ParkedReceipt? result = null;
        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT id,park_number,pickup_number,created_at,updated_at,created_by,
                       discount_cents,total_cents,order_note,
                       COALESCE(tse_client_id,''),COALESCE(tse_transaction_number,''),
                       COALESCE(tse_signature_counter,''),COALESCE(tse_serial_number,''),
                       COALESCE(tse_signature,''),COALESCE(tse_log_time,''),COALESCE(tse_outage,0),
                       COALESCE(im_haus,0),vorgang_started_at,COALESCE(tse_start_log_time,'')
                FROM parked_receipts
                WHERE id=$id AND status='OPEN' AND COALESCE(is_training,0)=$training;
                """;
            q.Parameters.AddWithValue("$id", id);
            q.Parameters.AddWithValue("$training", training ? 1 : 0);
            await using var r = await q.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                result = new ParkedReceipt
                {
                    Id = r.GetInt64(0),
                    ParkNumber = r.GetInt64(1),
                    PickupNumber = r.GetInt64(2),
                    CreatedAt = DateTimeOffset.Parse(r.GetString(3)),
                    UpdatedAt = DateTimeOffset.Parse(r.GetString(4)),
                    CreatedBy = r.GetString(5),
                    DiscountCents = r.GetInt64(6),
                    TotalCents = r.GetInt64(7),
                    OrderNote=r.GetString(8),
                    TseClientId = r.GetString(9),
                    TseTransactionNumber = r.GetString(10),
                    TseSignatureCounter = r.GetString(11),
                    TseSerialNumber = r.GetString(12),
                    TseSignature = r.GetString(13),
                    TseLogTime = string.IsNullOrWhiteSpace(r.GetString(14)) ? null : DateTimeOffset.Parse(r.GetString(14)),
                    TseOutage = r.GetInt64(15) != 0,
                    ImHaus = r.GetInt64(16) != 0,
                    VorgangStartedAt = r.IsDBNull(17) ? null : DateTimeOffset.Parse(r.GetString(17)),
                    TseStartLogTime = string.IsNullOrWhiteSpace(r.GetString(18)) ? null : DateTimeOffset.Parse(r.GetString(18))
                };
            }
        }

        if (result is null)
            return null;
        result.Lines = await LoadItemsAsync(c, result.Id, ct);
        return result;
    });
}public async Task MarkCashedAsync(long id, long saleId, CancellationToken ct = default)
{
    await IoQueue.RunAsync(async () =>
    {
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            UPDATE parked_receipts
            SET status='CASHED',
                cashed_sale_id=$sale,
                cashed_at=$time,
                updated_at=$time
            WHERE id=$id AND status='OPEN';
            """;
        q.Parameters.AddWithValue("$sale", saleId);
        q.Parameters.AddWithValue("$time", DateTimeOffset.Now.ToString("O"));
        q.Parameters.AddWithValue("$id", id);
        await q.ExecuteNonQueryAsync(ct);
    });
}
public async Task CancelAsync(long id, CancellationToken ct = default, bool orderPrint = false, string actor = "SYSTEM")
{
    await IoQueue.RunAsync(async () =>
    {
        await using var c = _db.OpenConnection();
        await using var tx=c.BeginTransaction();
        await using var q = c.CreateCommand();
        q.Transaction=tx;
        q.CommandText = """
            UPDATE parked_receipts
            SET status='CANCELLED',
                updated_at=$time
            WHERE id=$id AND status='OPEN';
            """;
        q.Parameters.AddWithValue("$time", DateTimeOffset.Now.ToString("O"));
        q.Parameters.AddWithValue("$id", id);
        if (await q.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("Geparkter Bon wurde nicht gefunden oder ist nicht mehr offen.");
        await OrderPrintOutbox.AuditAsync(c,tx,id,actor,"PARK_CANCEL",ct);
        if(orderPrint)await OrderPrintOutbox.EnqueueAsync(c,tx,id,"CANCEL",ct);
        await tx.CommitAsync(ct);
        if(orderPrint) PrintCommitted?.Invoke();
    });
}
public async Task RecordTseResultAsync(long parkedReceiptId, SaleTseResult result, CancellationToken ct = default)
{
    await IoQueue.RunAsync(async () =>
    {
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        // parked_receipts is not immutable (an order can legitimately change
        // before it's cashed), so - unlike sale_tse_signatures - this is a
        // plain in-place UPDATE.
        q.CommandText = """
            UPDATE parked_receipts
            SET tse_client_id=$client,
                tse_transaction_number=$txn,
                tse_signature_counter=$counter,
                tse_serial_number=$serial,
                tse_signature=$sig,
                tse_log_time=$logtime,
                tse_outage=$outage,
                tse_start_log_time=$startlog
            WHERE id=$id;
            """;
        q.Parameters.AddWithValue("$client", result.ClientId);
        q.Parameters.AddWithValue("$txn", result.TransactionNumber);
        q.Parameters.AddWithValue("$counter", result.SignatureCounter);
        q.Parameters.AddWithValue("$serial", result.SerialNumber);
        q.Parameters.AddWithValue("$sig", result.Signature);
        q.Parameters.AddWithValue("$logtime", result.LogTime?.ToString("O") ?? "");
        q.Parameters.AddWithValue("$outage", result.Signed ? 0 : 1);
        q.Parameters.AddWithValue("$startlog", result.StartLogTime?.ToString("O") ?? "");
        q.Parameters.AddWithValue("$id", parkedReceiptId);
        await q.ExecuteNonQueryAsync(ct);
    });
}
    private static async Task InsertItemsAsync(
        SqliteConnection c,
        SqliteTransaction tx,
        long parkedId,
        IReadOnlyList<CartLine> lines,
        CancellationToken ct)
    {
        foreach (var line in lines)
        {
            await using var q = c.CreateCommand();
            q.Transaction = tx;
            q.CommandText = """
                INSERT INTO parked_receipt_items(
                  parked_receipt_id,product_id,product_name,variant_name,
                  barcode,quantity,quantity_milli,unit_price_cents,vat_rate,pfand_cents,line_total_cents,
                  list_unit_price_cents,list_line_total_cents,
                  promotion_id,promotion_name,promotion_percent,
                  promotion_discount_unit_cents,promotion_discount_cents,
                  promotion_start_date,promotion_end_date,im_haus_applicable,
                  vat_allocations_json,menu_components_json)
                VALUES(
                  $parked,$product,$name,$variant,
                  $barcode,$qty,$qtyMilli,$price,$vat,$pfand,$total,
                  $listUnit,$listTotal,
                  $promotionId,$promotionName,$promotionPercent,
                  $promotionUnit,$promotionTotal,
                  $promotionStart,$promotionEnd,$imHaus,
                  $vatAllocations,$menuComponents);
                """;
            q.Parameters.AddWithValue("$parked", parkedId);
            q.Parameters.AddWithValue("$product", line.ProductId);
            q.Parameters.AddWithValue("$name", line.ProductName);
            q.Parameters.AddWithValue("$variant", line.VariantName);
            q.Parameters.AddWithValue("$barcode", line.Barcode);
            q.Parameters.AddWithValue("$qty", Convert.ToDouble(line.Quantity));
            q.Parameters.AddWithValue("$qtyMilli", QuantityStorage.ToMilli(line.Quantity));
            q.Parameters.AddWithValue("$price", line.UnitPriceCents);
            q.Parameters.AddWithValue("$vat", line.VatRate);
            q.Parameters.AddWithValue("$imHaus", line.ImHausApplicable ? 1 : 0);
            q.Parameters.AddWithValue("$pfand", line.PfandCents);
            q.Parameters.AddWithValue("$total", line.LineTotalCents);
            q.Parameters.AddWithValue("$listUnit", line.EffectiveListUnitPriceCents);
            q.Parameters.AddWithValue("$listTotal", line.ListLineTotalCents);
            q.Parameters.AddWithValue("$promotionId", line.PromotionId);
            q.Parameters.AddWithValue("$promotionName", line.PromotionName);
            q.Parameters.AddWithValue("$promotionPercent", line.PromotionPercent);
            q.Parameters.AddWithValue("$promotionUnit", line.PromotionDiscountUnitCents);
            q.Parameters.AddWithValue("$promotionTotal", line.PromotionDiscountCents);
            q.Parameters.AddWithValue("$promotionStart", line.PromotionStartDate);
            q.Parameters.AddWithValue("$promotionEnd", line.PromotionEndDate);
            q.Parameters.AddWithValue("$vatAllocations", VatAllocationStorage.Serialize(line));
            q.Parameters.AddWithValue("$menuComponents", MenuComponentStorage.Serialize(line));
            await q.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<IReadOnlyList<CartLine>> LoadItemsAsync(
        SqliteConnection c,
        long parkedId,
        CancellationToken ct)
    {
        var result = new List<CartLine>();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT product_id,product_name,variant_name,barcode,
                   CASE WHEN COALESCE(quantity_milli,0)<>0 THEN quantity_milli ELSE CAST(ROUND(quantity*1000.0) AS INTEGER) END,
                   unit_price_cents,vat_rate,pfand_cents,
                   list_unit_price_cents,
                   promotion_id,promotion_name,promotion_percent,
                   promotion_discount_unit_cents,
                   promotion_start_date,promotion_end_date,
                   COALESCE(im_haus_applicable,1),
                   COALESCE(vat_allocations_json,''),
                   COALESCE(menu_components_json,''),
                   COALESCE((SELECT p.unit FROM products p WHERE p.id=parked_receipt_items.product_id),'Stück')
            FROM parked_receipt_items
            WHERE parked_receipt_id=$id
            ORDER BY id;
            """;
        q.Parameters.AddWithValue("$id", parkedId);

        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            result.Add(new CartLine
            {
                ProductId = r.GetInt64(0),
                ProductName = r.GetString(1),
                VariantName = r.GetString(2),
                Barcode = r.GetString(3),
                Quantity = QuantityStorage.FromMilli(r.GetInt64(4)),
                UnitPriceCents = r.GetInt64(5),
                VatRate = Convert.ToDecimal(r.GetDouble(6)),
                PfandCents = r.GetInt64(7),
                ListUnitPriceCents = r.GetInt64(8) > 0
                    ? r.GetInt64(8)
                    : r.GetInt64(5) + r.GetInt64(12),
                PromotionId = r.GetInt64(9),
                PromotionName = r.GetString(10),
                PromotionPercent = r.GetInt32(11),
                PromotionDiscountUnitCents = r.GetInt64(12),
                PromotionStartDate = r.GetString(13),
                PromotionEndDate = r.GetString(14),
                ImHausApplicable = r.GetInt64(15) != 0,
                VatAllocations = VatAllocationStorage.Deserialize(r.GetString(16)),
                MenuComponents = MenuComponentStorage.Deserialize(r.GetString(17)),
                Unit = r.GetString(18)
            });
        }

        return result;
    }

    private static IReadOnlyList<CartLine> CloneLines(
        IReadOnlyList<CartLine> lines) =>
        lines.Select(line => new CartLine
        {
            ProductId = line.ProductId,
            ProductName = line.ProductName,
            VariantName = line.VariantName,
            Barcode = line.Barcode,
            Quantity = line.Quantity,
            Unit = line.Unit,
            UnitPriceCents = line.UnitPriceCents,
            ListUnitPriceCents = line.EffectiveListUnitPriceCents,
            VatRate = line.VatRate,
            VatAllocations = line.VatAllocations.ToArray(),
            MenuComponents = line.MenuComponents.ToArray(),
            ImHausApplicable = line.ImHausApplicable,
            PfandCents = line.PfandCents,
            PromotionId = line.PromotionId,
            PromotionName = line.PromotionName,
            PromotionPercent = line.PromotionPercent,
            PromotionDiscountUnitCents = line.PromotionDiscountUnitCents,
            PromotionStartDate = line.PromotionStartDate,
            PromotionEndDate = line.PromotionEndDate
        }).ToArray();
}

public sealed class DailyClosingGuard : IDailyClosingGuard
{
    private readonly IParkedReceiptRepository _parked;
    private readonly SqliteDatabase? _db;
    public DailyClosingGuard(IParkedReceiptRepository parked, SqliteDatabase? db = null)
    {
        _parked = parked;
        _db = db;
    }
public async Task<DailyCloseCheck> CheckAsync(CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var count = await _parked.GetOpenCountAsync(ct);
        if (count > 0)
        {
            return new DailyCloseCheck(false, count, $"Z-Abschluss gesperrt: {count} geparkte Bon(s) sind noch offen. " + "Bitte zuerst alle geparkten Bons kassieren.");
        }

        // R136: AEAO zu § 146a Nr. 2.2.3.3 - at a closing no Vorgang may still
        // be open in the TSE (a cart at the till).
        if (_db is not null)
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM tse_vorgaenge WHERE state='OPEN';";
            var open = Convert.ToInt32(await q.ExecuteScalarAsync(ct));
            if (open > 0)
                return new DailyCloseCheck(false, open, "Z-Abschluss gesperrt: an der Kasse ist noch ein Vorgang offen. Bitte zuerst kassieren, parken oder den Bon leeren.");
        }

        return new DailyCloseCheck(true, 0, "Z-Abschluss freigegeben: keine geparkten Bons offen.");
    });
}}


public sealed class ProductCatalogCache : IProductCatalog
{
    private readonly IProductRepository _repo;
    private readonly PerformanceCounters _perf;
    private IReadOnlyList<ProductGroup> _groups=Array.Empty<ProductGroup>();
    private IReadOnlyList<Category> _categories=Array.Empty<Category>();
    private IReadOnlyList<Product> _products=Array.Empty<Product>();
    private FrozenDictionary<string,Product> _byBarcode=
        new Dictionary<string,Product>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal);
    private FrozenDictionary<long,Product[]> _byCategory=
        new Dictionary<long,Product[]>().ToFrozenDictionary();

    public ProductCatalogCache(IProductRepository repo,PerformanceCounters perf){_repo=repo;_perf=perf;}
    public IReadOnlyList<ProductGroup> Groups=>_groups;
    public IReadOnlyList<Category> Categories=>_categories;
    public IReadOnlyList<Product> Products=>_products;
public async ValueTask ReloadAsync(CancellationToken ct = default)
{
    await IoQueue.RunAsync(async () =>
    {
        using var catalogPerformanceScope = _perf.Measure("catalog.reload");
        _groups = await _repo.GetGroupsAsync(ct);
        _categories = await _repo.GetCategoriesAsync(ct);
        _products = await _repo.GetActiveProductsAsync(ct);
        _byBarcode = _products.Where(x => !string.IsNullOrWhiteSpace(x.Barcode)).GroupBy(x => x.Barcode.Trim(), StringComparer.Ordinal).Select(g => g.First()).ToFrozenDictionary(x => x.Barcode.Trim(), x => x, StringComparer.Ordinal);
        _byCategory = _products.GroupBy(x => x.CategoryId).ToFrozenDictionary(g => g.Key, g => g.OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ToArray());
    });
}
    public bool TryGetByBarcode(string barcode,out Product? product)
    {
        using var barcodePerformanceScope=_perf.Measure("barcode.lookup");
        return _byBarcode.TryGetValue(barcode.Trim(),out product);
    }

    public IReadOnlyList<Product> GetByCategory(long categoryId)=>
        _byCategory.TryGetValue(categoryId,out var p)?p:Array.Empty<Product>();
}

public sealed class ProductImageStore
{
public async Task<string> ImportAsync(string sourcePath, CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return "";
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (ext is not ".jpg" and not ".jpeg" and not ".png" and not ".webp" and not ".bmp")
            throw new InvalidOperationException("Nur JPG, PNG, WEBP oder BMP.");
        var target = Path.Combine(AppPaths.ProductImagesPath, $"{Guid.NewGuid():N}{ext}");
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, ct);
        return target;
    });
}}


public sealed class SettingsRepository : ISettingsRepository
{
    private readonly SqliteDatabase _db;
    public SettingsRepository(SqliteDatabase db) => _db = db;
public async Task<IReadOnlyDictionary<string, string>> LoadAllAsync(CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = "SELECT key,value FROM app_settings ORDER BY key;";
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result[r.GetString(0)] = r.GetString(1);
        return result;
    });
}public async Task<string> GetAsync(string key, string defaultValue = "", CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = "SELECT value FROM app_settings WHERE key=$key;";
        q.Parameters.AddWithValue("$key", key);
        var value = await q.ExecuteScalarAsync(ct);
        return value?.ToString() ?? defaultValue;
    });
}public async Task SaveManyAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct = default)
{
    await IoQueue.RunAsync(async () =>
    {
        if (values.Count == 0)
            return;
        await using var c = _db.OpenConnection();

        // R132 (DSFinV-K 3.2): company master data may only change when no
        // Vorgang is waiting for a closing - otherwise that closing would be
        // exported under data it was not recorded with. This guard is the
        // last line; DsfinvkMasterDataService creates the closing
        // automatically before saving.
        var changed = await DsfinvkMasterDataStore.ChangedKeysAsync(c, values, ct);
        if (changed.Count > 0 && await DsfinvkMasterDataStore.HasOpenVorgaengeAsync(c, ct))
            throw new MasterDataChangeRequiresClosingException(changed);

        await using var tx = await c.BeginTransactionAsync(ct);
        foreach (var pair in values)
        {
            await using var q = c.CreateCommand();
            q.Transaction = (SqliteTransaction)tx;
            q.CommandText = """
                INSERT INTO app_settings(key,value) VALUES($key,$value)
                ON CONFLICT(key) DO UPDATE SET value=excluded.value;
                """;
            q.Parameters.AddWithValue("$key", pair.Key);
            q.Parameters.AddWithValue("$value", pair.Value ?? "");
            await q.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    });
}}

public sealed class DatabaseBackupService
{
    private readonly SqliteDatabase _db;
    public DatabaseBackupService(SqliteDatabase db) => _db = db;

    public string ResolveDirectory(string? configured)
    {
        var directory = string.IsNullOrWhiteSpace(configured)
            ? AppPaths.BackupsPath
            : configured.Trim();

        Directory.CreateDirectory(directory);
        return directory;
    }

    public bool TestDirectory(string? configured, out string message)
    {
        try
        {
            var directory = ResolveDirectory(configured);
            var probe = Path.Combine(directory, $".tor-write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "TOR");
            File.Delete(probe);
            message = $"Verzeichnis erreichbar: {directory}";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Verzeichnis nicht erreichbar: {ex.Message}";
            return false;
        }
    }

    public Task<string> CreateBackupAsync(string? configured, CancellationToken ct = default)
    {
        return CreateNamedBackupAsync(
            configured,
            "TOR-POS",
            ct);
    }

    public Task<string> CreateMigrationBackupAsync(
        string? configured = null,
        CancellationToken ct = default)
    {
        var directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppPaths.BackupsPath, "Migrationen")
            : configured.Trim();

        return CreateNamedBackupAsync(
            directory,
            "TOR-POS-PRE-MIGRATION",
            ct);
    }

    private Task<string> CreateNamedBackupAsync(
        string? configured,
        string prefix,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            var directory = ResolveDirectory(configured);
            var target = Path.Combine(
                directory,
                $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.db");

            // R72.4:
            // A generated backup DB is a short-lived snapshot file.
            // Never put its connection into the Microsoft.Data.Sqlite pool.
            // A pooled native SQLite handle can survive Dispose() on Windows
            // and keep FullBackupService's staging directory locked.
            using (var source = _db.OpenConnection())
            using (var destination = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = target,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Private,
                    Pooling = false
                }.ToString()))
            {
                destination.Open();
                source.BackupDatabase(destination);

                using var verify = destination.CreateCommand();
                verify.CommandText = "PRAGMA quick_check;";

                var check =
                    Convert.ToString(
                        verify.ExecuteScalar()) ?? "";

                if (!string.Equals(
                    check,
                    "ok",
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Backup konnte nicht verifiziert werden: " + check);
                }
            }

            // All destination handles are closed before the path is returned.
            return target;
        }, ct);
    }
}
