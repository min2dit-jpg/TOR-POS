using Microsoft.Data.Sqlite;

namespace TorPos.Infrastructure;

/// <summary>
/// R69 database schema coordinator.
/// Existing pre-R69 databases are treated as schema version 0.
/// SqliteDatabase.InitializeAsync remains the legacy compatibility bootstrap;
/// all schema changes introduced after R69 must be added here as ordered migrations.
/// </summary>
public sealed class SchemaMigrationService
{
    public const int TargetSchemaVersion = 31;

    private readonly SqliteDatabase _db;
    private readonly DatabaseBackupService _backup;
    private readonly string? _migrationBackupDirectory;

    public SchemaMigrationService(
        SqliteDatabase db,
        DatabaseBackupService backup,
        string? migrationBackupDirectory = null)
    {
        _db = db;
        _backup = backup;
        _migrationBackupDirectory = migrationBackupDirectory;
    }

    public Task<SchemaMigrationResult> InitializeDatabaseAsync(
        CancellationToken ct = default)
        => IoQueue.RunAsync(() => InitializeDatabaseCoreAsync(ct));

    private async Task<SchemaMigrationResult> InitializeDatabaseCoreAsync(
        CancellationToken ct)
    {
        var existedBefore =
            File.Exists(_db.DatabasePath) &&
            new FileInfo(_db.DatabasePath).Length > 0;

        var before = existedBefore
            ? await ReadVersionWithoutCreatingAsync(ct)
            : 0;

        if (before > TargetSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Die Datenbank hat Schema-Version {before}, " +
                $"diese TOR POS-Version unterstützt nur bis {TargetSchemaVersion}. " +
                "Ein Downgrade wird aus Sicherheitsgründen verweigert.");
        }

        string? backupPath = null;

        // Existing unversioned/older customer DB: backup BEFORE legacy bootstrap
        // or any versioned migration is allowed to touch its schema.
        if (existedBefore && before < TargetSchemaVersion)
        {
            backupPath = await _backup.CreateMigrationBackupAsync(
                _migrationBackupDirectory,
                ct);
        }

        // Compatibility bootstrap for all historical installations.
        // IMPORTANT R69 RULE: no NEW post-R69 schema changes belong in this method.
        await _db.InitializeAsync(ct);

        await EnsureMetadataTablesAsync(ct);

        var current = await GetCurrentVersionAsync(ct);
        if (current > TargetSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Die Datenbank hat Schema-Version {current}, " +
                $"diese TOR POS-Version unterstützt nur bis {TargetSchemaVersion}.");
        }

        var applied = new List<int>();

        foreach (var migration in OrderedMigrations)
        {
            if (migration.Version <= current)
                continue;

            if (migration.Version != current + 1)
            {
                throw new InvalidOperationException(
                    $"Ungültige Migration-Reihenfolge: erwartet {current + 1}, " +
                    $"gefunden {migration.Version}.");
            }

            await ApplyMigrationAsync(
                migration,
                backupPath,
                ct);

            current = migration.Version;
            applied.Add(current);
        }

        var finalStatus = await GetStatusAsync(ct);

        if (finalStatus.CurrentVersion != TargetSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Schema-Migration unvollständig. Ist={finalStatus.CurrentVersion}, " +
                $"Soll={TargetSchemaVersion}.");
        }

        return new SchemaMigrationResult(
            before,
            finalStatus.CurrentVersion,
            existedBefore,
            backupPath,
            applied);
    }

    public async Task<SchemaMigrationStatus> GetStatusAsync(
        CancellationToken ct = default)
    {
        await using var c = _db.OpenConnection();

        if (!await TableExistsAsync(c, "schema_version", ct))
        {
            return new SchemaMigrationStatus(
                0,
                TargetSchemaVersion,
                false,
                "",
                0);
        }

        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT version, updated_at, app_version
            FROM schema_version
            WHERE singleton_id=1;
            """;

        await using var r = await q.ExecuteReaderAsync(ct);

        if (!await r.ReadAsync(ct))
        {
            return new SchemaMigrationStatus(
                0,
                TargetSchemaVersion,
                true,
                "",
                0);
        }

        var version = r.GetInt32(0);
        var updated = r.IsDBNull(1) ? "" : r.GetString(1);

        await using var count = c.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
        var historyCount = Convert.ToInt32(
            await count.ExecuteScalarAsync(ct));

        return new SchemaMigrationStatus(
            version,
            TargetSchemaVersion,
            true,
            updated,
            historyCount);
    }

    public async Task<int> GetCurrentVersionAsync(
        CancellationToken ct = default)
    {
        var status = await GetStatusAsync(ct);
        return status.CurrentVersion;
    }

    private async Task<int> ReadVersionWithoutCreatingAsync(
        CancellationToken ct)
    {
        // Read-only connection is used so merely checking an old customer DB
        // cannot create migration metadata before the safety backup.
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _db.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        await using var c = new SqliteConnection(cs);
        await c.OpenAsync(ct);

        if (!await TableExistsAsync(c, "schema_version", ct))
            return 0;

        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT COALESCE(
                (SELECT version
                 FROM schema_version
                 WHERE singleton_id=1),
                0);
            """;

        return Convert.ToInt32(
            await q.ExecuteScalarAsync(ct));
    }

    private async Task EnsureMetadataTablesAsync(
        CancellationToken ct)
    {
        await using var c = _db.OpenConnection();
        await using var tx = c.BeginTransaction();

        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_version(
                    singleton_id INTEGER PRIMARY KEY CHECK(singleton_id=1),
                    version INTEGER NOT NULL,
                    updated_at TEXT NOT NULL,
                    app_version TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS schema_migrations(
                    version INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    applied_at TEXT NOT NULL,
                    app_version TEXT NOT NULL,
                    pre_migration_backup TEXT NOT NULL DEFAULT ''
                );
                """;
            await q.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    private async Task ApplyMigrationAsync(
        DatabaseMigration migration,
        string? backupPath,
        CancellationToken ct)
    {
        await using var c = _db.OpenConnection();
        await using var tx = c.BeginTransaction();

        try
        {
            await migration.ApplyAsync(c, tx, ct);

            var now = DateTimeOffset.UtcNow.ToString("O");

            await using (var history = c.CreateCommand())
            {
                history.Transaction = tx;
                history.CommandText = """
                    INSERT INTO schema_migrations(
                        version,
                        name,
                        applied_at,
                        app_version,
                        pre_migration_backup)
                    VALUES(
                        $version,
                        $name,
                        $applied,
                        $app,
                        $backup);
                    """;
                history.Parameters.AddWithValue("$version", migration.Version);
                history.Parameters.AddWithValue("$name", migration.Name);
                history.Parameters.AddWithValue("$applied", now);
                history.Parameters.AddWithValue("$app", TorPos.Core.TorRelease.Version);
                history.Parameters.AddWithValue("$backup", backupPath ?? "");
                await history.ExecuteNonQueryAsync(ct);
            }

            await using (var version = c.CreateCommand())
            {
                version.Transaction = tx;
                version.CommandText = """
                    INSERT INTO schema_version(
                        singleton_id,
                        version,
                        updated_at,
                        app_version)
                    VALUES(1,$version,$updated,$app)
                    ON CONFLICT(singleton_id) DO UPDATE SET
                        version=excluded.version,
                        updated_at=excluded.updated_at,
                        app_version=excluded.app_version;
                    """;
                version.Parameters.AddWithValue("$version", migration.Version);
                version.Parameters.AddWithValue("$updated", now);
                version.Parameters.AddWithValue("$app", TorPos.Core.TorRelease.Version);
                await version.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection c,
        string table,
        CancellationToken ct)
    {
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type='table' AND name=$name;
            """;
        q.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(
            await q.ExecuteScalarAsync(ct)) > 0;
    }

    private static readonly IReadOnlyList<DatabaseMigration> OrderedMigrations =
        new DatabaseMigration[]
        {
            new(
                1,
                "R69_VERSIONED_SCHEMA_BASELINE",
                static (_, _, _) => Task.CompletedTask),

            new(
                2,
                "R70_CONTROLLED_POS_ACTION_AUDIT",
                static async (c, tx, ct) =>
                {
                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        CREATE TABLE IF NOT EXISTS pos_action_log(
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            created_at TEXT NOT NULL,
                            action_id TEXT NOT NULL,
                            phase TEXT NOT NULL,
                            actor TEXT NOT NULL,
                            register_id TEXT NOT NULL,
                            operation_id TEXT NOT NULL,
                            action_type TEXT NOT NULL,
                            reason TEXT NOT NULL,
                            entity_type TEXT NOT NULL,
                            entity_id TEXT NOT NULL,
                            before_total_cents INTEGER NOT NULL,
                            after_total_cents INTEGER NOT NULL,
                            amount_cents INTEGER NOT NULL,
                            details TEXT NOT NULL DEFAULT '',
                            prev_hash TEXT NOT NULL,
                            entry_hash TEXT NOT NULL UNIQUE,
                            UNIQUE(action_id,phase)
                        );

                        CREATE INDEX IF NOT EXISTS ix_pos_action_log_created
                            ON pos_action_log(created_at,id);

                        CREATE INDEX IF NOT EXISTS ix_pos_action_log_operation
                            ON pos_action_log(operation_id,id);

                        CREATE INDEX IF NOT EXISTS ix_pos_action_log_type
                            ON pos_action_log(action_type,id);

                        CREATE TRIGGER IF NOT EXISTS trg_pos_action_no_update
                        BEFORE UPDATE ON pos_action_log
                        BEGIN
                            SELECT RAISE(ABORT,'controlled POS action log is append-only');
                        END;

                        CREATE TRIGGER IF NOT EXISTS trg_pos_action_no_delete
                        BEFORE DELETE ON pos_action_log
                        BEGIN
                            SELECT RAISE(ABORT,'controlled POS action log is append-only');
                        END;

                        INSERT OR IGNORE INTO app_settings(key,value)
                        VALUES(
                            'function.discount_reasons',
                            'Kundenrabatt|Kulanz|Aktion|Preisabweichung|Sonstiger Grund');

                        INSERT OR IGNORE INTO app_settings(key,value)
                        VALUES(
                            'function.cancel_reasons',
                            'Kunde abgebrochen|Fehleingabe|Doppelerfassung|Zahlung nicht gewünscht|Sonstiger Grund');
                        """;
                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                3,
                "R71_ANGEBOT_PROMOTIONS_AND_REPORTING",
                static async (c, tx, ct) =>
                {
                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        CREATE TABLE IF NOT EXISTS promotion_campaigns(
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            name TEXT NOT NULL,
                            discount_percent INTEGER NOT NULL
                                CHECK(discount_percent IN (10,15,20,25,30)),
                            start_date TEXT NOT NULL,
                            end_date TEXT NOT NULL,
                            scope_type TEXT NOT NULL
                                CHECK(scope_type IN ('ALL','CATEGORY','PRODUCT')),
                            target_id INTEGER NOT NULL DEFAULT 0,
                            target_name TEXT NOT NULL DEFAULT '',
                            is_enabled INTEGER NOT NULL DEFAULT 1,
                            created_by TEXT NOT NULL,
                            created_at TEXT NOT NULL,
                            updated_at TEXT NOT NULL,
                            CHECK(start_date <= end_date)
                        );

                        CREATE INDEX IF NOT EXISTS ix_promotion_active_dates
                            ON promotion_campaigns(
                                is_enabled,start_date,end_date,scope_type,target_id);

                        CREATE TRIGGER IF NOT EXISTS trg_promotion_no_delete
                        BEFORE DELETE ON promotion_campaigns
                        BEGIN
                            SELECT RAISE(
                                ABORT,
                                'promotion history cannot be deleted');
                        END;

                        CREATE TRIGGER IF NOT EXISTS trg_promotion_immutable_except_disable
                        BEFORE UPDATE ON promotion_campaigns
                        WHEN NOT (
                            OLD.is_enabled=1
                            AND NEW.is_enabled=0
                            AND NEW.id=OLD.id
                            AND NEW.name=OLD.name
                            AND NEW.discount_percent=OLD.discount_percent
                            AND NEW.start_date=OLD.start_date
                            AND NEW.end_date=OLD.end_date
                            AND NEW.scope_type=OLD.scope_type
                            AND NEW.target_id=OLD.target_id
                            AND NEW.target_name=OLD.target_name
                            AND NEW.created_by=OLD.created_by
                            AND NEW.created_at=OLD.created_at
                        )
                        BEGIN
                            SELECT RAISE(
                                ABORT,
                                'promotion is immutable; disable and create a new campaign');
                        END;

                        ALTER TABLE sales
                            ADD COLUMN list_subtotal_cents INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE sales
                            ADD COLUMN promotion_discount_cents INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE sales
                            ADD COLUMN transaction_type TEXT NOT NULL DEFAULT 'SALE';
                        ALTER TABLE sales
                            ADD COLUMN original_sale_id INTEGER NULL;

                        CREATE INDEX IF NOT EXISTS ix_sales_transaction_type
                            ON sales(transaction_type,created_at);

                        ALTER TABLE sale_items
                            ADD COLUMN list_unit_price_cents INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE sale_items
                            ADD COLUMN list_line_total_cents INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE sale_items
                            ADD COLUMN promotion_id INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE sale_items
                            ADD COLUMN promotion_name TEXT NOT NULL DEFAULT '';
                        ALTER TABLE sale_items
                            ADD COLUMN promotion_percent INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE sale_items
                            ADD COLUMN promotion_discount_unit_cents INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE sale_items
                            ADD COLUMN promotion_discount_cents INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE sale_items
                            ADD COLUMN promotion_start_date TEXT NOT NULL DEFAULT '';
                        ALTER TABLE sale_items
                            ADD COLUMN promotion_end_date TEXT NOT NULL DEFAULT '';

                        CREATE INDEX IF NOT EXISTS ix_sale_items_promotion
                            ON sale_items(promotion_id,sale_id);

                        ALTER TABLE parked_receipt_items
                            ADD COLUMN list_unit_price_cents INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE parked_receipt_items
                            ADD COLUMN list_line_total_cents INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE parked_receipt_items
                            ADD COLUMN promotion_id INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE parked_receipt_items
                            ADD COLUMN promotion_name TEXT NOT NULL DEFAULT '';
                        ALTER TABLE parked_receipt_items
                            ADD COLUMN promotion_percent INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE parked_receipt_items
                            ADD COLUMN promotion_discount_unit_cents INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE parked_receipt_items
                            ADD COLUMN promotion_discount_cents INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE parked_receipt_items
                            ADD COLUMN promotion_start_date TEXT NOT NULL DEFAULT '';
                        ALTER TABLE parked_receipt_items
                            ADD COLUMN promotion_end_date TEXT NOT NULL DEFAULT '';

                        ALTER TABLE z_report_archive
                            ADD COLUMN list_gross_cents INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE z_report_archive
                            ADD COLUMN promotion_discount_cents INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE z_report_archive
                            ADD COLUMN manual_discount_cents INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE z_report_archive
                            ADD COLUMN storno_cents INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE z_report_archive
                            ADD COLUMN return_cents INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE z_report_archive
                            ADD COLUMN vat7_net_cents INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE z_report_archive
                            ADD COLUMN vat7_tax_cents INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE z_report_archive
                            ADD COLUMN vat19_net_cents INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE z_report_archive
                            ADD COLUMN vat19_tax_cents INTEGER NOT NULL DEFAULT 0;

                        INSERT OR IGNORE INTO app_settings(key,value)
                        VALUES('promotion.allow_manual_discount','false');
                        """;

                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                4,
                "R72_AUTH_KDF_HARDENING_AND_PROMOTION_40_50",
                static async (c, tx, ct) =>
                {
                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        -- Existing R71 credential hashes were PBKDF2-SHA256 / 180k.
                        -- New rows use 600k by default. Existing rows are explicitly
                        -- marked 180k and upgraded after their next successful login.
                        ALTER TABLE users
                            ADD COLUMN password_kdf TEXT NOT NULL
                                DEFAULT 'PBKDF2-SHA256';
                        ALTER TABLE users
                            ADD COLUMN password_iterations INTEGER NOT NULL
                                DEFAULT 600000;
                        ALTER TABLE users
                            ADD COLUMN pin_kdf TEXT NOT NULL
                                DEFAULT 'PBKDF2-SHA256';
                        ALTER TABLE users
                            ADD COLUMN pin_iterations INTEGER NOT NULL
                                DEFAULT 600000;

                        UPDATE users
                        SET password_iterations=180000,
                            pin_iterations=180000;

                        -- SQLite cannot change the original R71 CHECK constraint
                        -- in place. Rebuild transactionally, preserving campaign IDs
                        -- and full history while adding 40% and 50%.
                        DROP TRIGGER IF EXISTS trg_promotion_no_delete;
                        DROP TRIGGER IF EXISTS trg_promotion_immutable_except_disable;
                        DROP INDEX IF EXISTS ix_promotion_active_dates;

                        ALTER TABLE promotion_campaigns
                            RENAME TO promotion_campaigns_r71;

                        CREATE TABLE promotion_campaigns(
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            name TEXT NOT NULL,
                            discount_percent INTEGER NOT NULL
                                CHECK(discount_percent IN (10,15,20,25,30,40,50)),
                            start_date TEXT NOT NULL,
                            end_date TEXT NOT NULL,
                            scope_type TEXT NOT NULL
                                CHECK(scope_type IN ('ALL','CATEGORY','PRODUCT')),
                            target_id INTEGER NOT NULL DEFAULT 0,
                            target_name TEXT NOT NULL DEFAULT '',
                            is_enabled INTEGER NOT NULL DEFAULT 1,
                            created_by TEXT NOT NULL,
                            created_at TEXT NOT NULL,
                            updated_at TEXT NOT NULL,
                            CHECK(start_date <= end_date)
                        );

                        INSERT INTO promotion_campaigns(
                            id,name,discount_percent,start_date,end_date,
                            scope_type,target_id,target_name,is_enabled,
                            created_by,created_at,updated_at)
                        SELECT
                            id,name,discount_percent,start_date,end_date,
                            scope_type,target_id,target_name,is_enabled,
                            created_by,created_at,updated_at
                        FROM promotion_campaigns_r71
                        ORDER BY id;

                        DROP TABLE promotion_campaigns_r71;

                        CREATE INDEX ix_promotion_active_dates
                            ON promotion_campaigns(
                                is_enabled,start_date,end_date,scope_type,target_id);

                        CREATE TRIGGER trg_promotion_no_delete
                        BEFORE DELETE ON promotion_campaigns
                        BEGIN
                            SELECT RAISE(
                                ABORT,
                                'promotion history cannot be deleted');
                        END;

                        CREATE TRIGGER trg_promotion_immutable_except_disable
                        BEFORE UPDATE ON promotion_campaigns
                        WHEN NOT (
                            OLD.is_enabled=1
                            AND NEW.is_enabled=0
                            AND NEW.name=OLD.name
                            AND NEW.discount_percent=OLD.discount_percent
                            AND NEW.start_date=OLD.start_date
                            AND NEW.end_date=OLD.end_date
                            AND NEW.scope_type=OLD.scope_type
                            AND NEW.target_id=OLD.target_id
                            AND NEW.target_name=OLD.target_name
                            AND NEW.created_by=OLD.created_by
                            AND NEW.created_at=OLD.created_at
                        )
                        BEGIN
                            SELECT RAISE(
                                ABORT,
                                'promotion is immutable; disable and create a new campaign');
                        END;
                        """;

                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                5,
                "R73_PAYMENT_OUTCOME_STATE_MACHINE",
                static async (c, tx, ct) =>
                {
                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        ALTER TABLE checkout_operations
                            ADD COLUMN terminal_outcome TEXT NOT NULL
                                DEFAULT 'NONE'
                                CHECK(terminal_outcome IN (
                                    'NONE',
                                    'APPROVED',
                                    'DECLINED',
                                    'CANCELLED',
                                    'NOT_SENT',
                                    'UNKNOWN'));

                        ALTER TABLE checkout_operations
                            ADD COLUMN terminal_code TEXT NOT NULL
                                DEFAULT '';

                        ALTER TABLE checkout_operations
                            ADD COLUMN terminal_message TEXT NOT NULL
                                DEFAULT '';

                        ALTER TABLE checkout_operations
                            ADD COLUMN terminal_submitted INTEGER NOT NULL
                                DEFAULT 0
                                CHECK(terminal_submitted IN (0,1));

                        ALTER TABLE checkout_operations
                            ADD COLUMN resolution TEXT NOT NULL
                                DEFAULT 'NONE'
                                CHECK(resolution IN (
                                    'NONE',
                                    'AUTO_APPROVED',
                                    'AUTO_NOT_CHARGED',
                                    'MANUAL_PAID',
                                    'MANUAL_NOT_CHARGED'));

                        ALTER TABLE checkout_operations
                            ADD COLUMN resolution_actor TEXT NOT NULL
                                DEFAULT '';

                        ALTER TABLE checkout_operations
                            ADD COLUMN resolution_at TEXT NOT NULL
                                DEFAULT '';

                        -- Preserve the safety meaning of unresolved R72.x rows.
                        -- SENT/UNKNOWN/APPROVED all imply that a card payment may
                        -- already have reached the terminal. Do not invent a final
                        -- terminal outcome for legacy APPROVED rows because they may
                        -- have been resolved manually.
                        UPDATE checkout_operations
                        SET terminal_submitted=1
                        WHERE state IN ('SENT','UNKNOWN','APPROVED');

                        UPDATE checkout_operations
                        SET terminal_outcome='UNKNOWN'
                        WHERE state='UNKNOWN';

                        CREATE INDEX IF NOT EXISTS ix_checkout_terminal_outcome
                            ON checkout_operations(
                                terminal_outcome,
                                updated_at);
                        """;

                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                6,
                "R75.2_PERFORMANCE_INDEXES",
                static async (c, tx, ct) =>
                {
                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        -- sale_items had no index on sale_id at all: every receipt
                        -- reprint, history search and report that loads a sale's
                        -- lines forced a full table scan (grows without bound as
                        -- sales accumulate).
                        CREATE INDEX IF NOT EXISTS ix_sale_items_sale
                            ON sale_items(sale_id);

                        -- audit_log is append-only and unbounded; personnel and
                        -- storno reports filtered/grouped on these columns with no
                        -- supporting index.
                        CREATE INDEX IF NOT EXISTS ix_audit_log_created
                            ON audit_log(created_at);

                        CREATE INDEX IF NOT EXISTS ix_audit_log_actor_created
                            ON audit_log(actor, created_at);
                        """;

                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                7,
                "R75.2_UTC_TIMESTAMP_COLUMNS",
                static async (c, tx, ct) =>
                {
                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        -- created_at is stored as a local-time ISO-8601 string with its
                        -- own UTC offset (DateTimeOffset.Now.ToString("O")). Comparing or
                        -- ordering those strings directly is only chronologically correct
                        -- within a single fixed offset: during the once-a-year DST
                        -- "fall back" hour, two valid local times can carry different
                        -- offsets and sort out of true chronological order.
                        --
                        -- created_at_utc is a generated column (computed automatically for
                        -- every existing and future row directly from created_at; no
                        -- INSERT statement anywhere needs to change) that normalizes to a
                        -- constant-offset UTC representation, so plain text comparison and
                        -- ORDER BY on it is always chronologically correct and index-usable.
                        ALTER TABLE sales
                            ADD COLUMN created_at_utc TEXT
                                GENERATED ALWAYS AS (
                                    strftime('%Y-%m-%dT%H:%M:%fZ', created_at)
                                ) VIRTUAL;

                        CREATE INDEX IF NOT EXISTS ix_sales_created_utc
                            ON sales(created_at_utc);

                        ALTER TABLE cash_movements
                            ADD COLUMN created_at_utc TEXT
                                GENERATED ALWAYS AS (
                                    strftime('%Y-%m-%dT%H:%M:%fZ', created_at)
                                ) VIRTUAL;

                        CREATE INDEX IF NOT EXISTS ix_cash_movements_created_utc
                            ON cash_movements(created_at_utc);

                        ALTER TABLE audit_log
                            ADD COLUMN created_at_utc TEXT
                                GENERATED ALWAYS AS (
                                    strftime('%Y-%m-%dT%H:%M:%fZ', created_at)
                                ) VIRTUAL;

                        CREATE INDEX IF NOT EXISTS ix_audit_log_created_utc
                            ON audit_log(created_at_utc);

                        ALTER TABLE pos_action_log
                            ADD COLUMN created_at_utc TEXT
                                GENERATED ALWAYS AS (
                                    strftime('%Y-%m-%dT%H:%M:%fZ', created_at)
                                ) VIRTUAL;

                        CREATE INDEX IF NOT EXISTS ix_pos_action_log_created_utc
                            ON pos_action_log(created_at_utc);
                        """;

                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                8,
                "R78_SALE_TSE_SIGNATURES",
                static async (c, tx, ct) =>
                {
                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        -- R78: durable, append-only storage for the TSE Beleg signature of
                        -- a completed sale (KassenSichV §6: TSE-Transaktionsnummer,
                        -- Signaturzähler, Seriennummer, Prüfwert). A SEPARATE table, not
                        -- new columns on sales - trg_sales_no_update forbids ANY update to
                        -- an already-committed sale row, and signing necessarily happens
                        -- after that row is committed. Numeric TSE values are stored as
                        -- TEXT to avoid any ulong/long range surprises - they are opaque
                        -- identifiers, never arithmetic.
                        --
                        -- R122 (audit finding F6) CORRECTED COMMENT. The original text here
                        -- claimed that a failed signing attempt "simply never gets a row".
                        -- That is NOT what the code does and must not be: an outage DOES
                        -- write a row with outage=1, and that row is precisely what puts the
                        -- legally required "TSE-AUSFALL / Vorgang ohne TSE-Signatur" note on
                        -- the receipt (R113 exists because the outage path used to write
                        -- nothing at all and the note was therefore never printed).
                        --
                        -- Because sale_id is the PRIMARY KEY and the triggers below forbid
                        -- UPDATE and DELETE, that record is FINAL: exactly one per sale,
                        -- signature or outage, and a sale recorded as an outage can never be
                        -- signed afterwards (Nachsignieren).
                        --
                        -- R129: that open decision is closed by the legal position, not by
                        -- taste. AEAO zu § 146a Nr. 1.14 (BMF 30.06.2023, unchanged by the
                        -- 17.03.2026 amendment) sets out what an outage requires: outage
                        -- times and reason documented (tse_outage_log), the outage visible
                        -- on the receipt, the till may keep running, the cause is fixed
                        -- without delay. Nothing there provides for signing the affected
                        -- sales later, and it could not repair them: § 2 KassenSichV wants
                        -- the transaction started "unmittelbar", with its times fixed by the
                        -- security module - a later signature would carry the TSE's later
                        -- time, not the time of the sale. DSFinV-K records such a sale in
                        -- TSE_Transaktionen with the explanation in TSE_TA_FEHLER instead.
                        -- So: one final record per sale stays. See Desktop/R129-CHANGELOG.md.
                        CREATE TABLE IF NOT EXISTS sale_tse_signatures(
                          sale_id INTEGER PRIMARY KEY,
                          client_id TEXT NOT NULL DEFAULT '',
                          transaction_number TEXT NOT NULL DEFAULT '',
                          signature_counter TEXT NOT NULL DEFAULT '',
                          serial_number TEXT NOT NULL DEFAULT '',
                          signature TEXT NOT NULL DEFAULT '',
                          log_time TEXT NOT NULL DEFAULT '',
                          outage INTEGER NOT NULL DEFAULT 0,
                          created_at TEXT NOT NULL,
                          FOREIGN KEY(sale_id) REFERENCES sales(id));

                        CREATE TRIGGER IF NOT EXISTS trg_sale_tse_signatures_no_update
                        BEFORE UPDATE ON sale_tse_signatures
                        BEGIN
                          SELECT RAISE(ABORT,'TSE signature record is immutable once written');
                        END;

                        CREATE TRIGGER IF NOT EXISTS trg_sale_tse_signatures_no_delete
                        BEFORE DELETE ON sale_tse_signatures
                        BEGIN
                          SELECT RAISE(ABORT,'TSE signature record cannot be deleted');
                        END;
                        """;

                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                9,
                "R80_SALES_ONE_STORNO_PER_ORIGINAL",
                static async (c, tx, ct) =>
                {
                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        -- Found on review: RecordStornoAsync's own "already storno'd?"
                        -- check (SELECT COUNT(*) ... before INSERT, same transaction) is
                        -- not by itself race-proof against two concurrent BON STORNO
                        -- attempts on the same original sale. A partial unique index makes
                        -- a double storno impossible at the database level, not just the
                        -- application level - the same pattern ux_checkout_one_open already
                        -- uses for "at most one open checkout".
                        CREATE UNIQUE INDEX IF NOT EXISTS ux_sales_one_storno_per_original
                            ON sales(original_sale_id)
                            WHERE transaction_type='STORNO';
                        """;

                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                10,
                "R82_PARTIAL_RETURN_LINE_TRACKING",
                static async (c, tx, ct) =>
                {
                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        -- R82: a partial Retoure's mirrored sale_items row points back to
                        -- the exact original line it reverses, so RecordReturnAsync can sum
                        -- "how much of this original line has already been returned" across
                        -- every prior RETURN sale, and refuse a request that would return
                        -- more than what is actually left.
                        ALTER TABLE sale_items
                            ADD COLUMN original_sale_item_id INTEGER NULL;

                        CREATE INDEX IF NOT EXISTS ix_sale_items_original
                            ON sale_items(original_sale_item_id);
                        """;

                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                11,
                "R83_ORDER_ACCEPTANCE_TSE_SIGNATURE",
                static async (c, tx, ct) =>
                {
                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        -- R83: TSE signature of an IMBISS ORDER's acceptance, as its own
                        -- "Bestellung-V1" Vorgang - separate from the Kassenbeleg-V1 signed
                        -- at payment time (R78). Unlike sales, parked_receipts carries no
                        -- immutability trigger (an order can legitimately change before it
                        -- is cashed), so this is plain columns updated in place, not a
                        -- separate append-only table like sale_tse_signatures.
                        ALTER TABLE parked_receipts
                            ADD COLUMN tse_client_id TEXT NOT NULL DEFAULT '';
                        ALTER TABLE parked_receipts
                            ADD COLUMN tse_transaction_number TEXT NOT NULL DEFAULT '';
                        ALTER TABLE parked_receipts
                            ADD COLUMN tse_signature_counter TEXT NOT NULL DEFAULT '';
                        ALTER TABLE parked_receipts
                            ADD COLUMN tse_serial_number TEXT NOT NULL DEFAULT '';
                        ALTER TABLE parked_receipts
                            ADD COLUMN tse_signature TEXT NOT NULL DEFAULT '';
                        ALTER TABLE parked_receipts
                            ADD COLUMN tse_log_time TEXT NOT NULL DEFAULT '';
                        ALTER TABLE parked_receipts
                            ADD COLUMN tse_outage INTEGER NOT NULL DEFAULT 0;
                        """;

                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                12,
                "R132_CLOSING_MASTER_DATA",
                static async (c, tx, ct) =>
                {
                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        -- R132: DSFinV-K 3.2 keeps the Stammdaten once per
                        -- Kassenabschluss. The master data (company, till,
                        -- software version) every Z-Bericht was recorded under,
                        -- as JSON. Closings from before R132 keep '' and the
                        -- export falls back to the current settings for them,
                        -- saying so.
                        ALTER TABLE z_report_archive
                            ADD COLUMN master_data TEXT NOT NULL DEFAULT '';
                        """;

                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                13,
                "R133_TSE_MASTER_DATA_AND_IM_HAUS",
                static async (c, tx, ct) =>
                {
                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        -- R133: Stamm_TSE (DSFinV-K 3.2.7) once per TSE, read from the
                        -- TSE's own TAR export. A TSE's certificate and algorithm never
                        -- change, so the record is immutable.
                        CREATE TABLE IF NOT EXISTS tse_master_data(
                          serial_number TEXT PRIMARY KEY,
                          signature_algorithm TEXT NOT NULL,
                          signature_algorithm_oid TEXT NOT NULL,
                          log_time_format TEXT NOT NULL,
                          process_data_encoding TEXT NOT NULL,
                          public_key TEXT NOT NULL,
                          certificate TEXT NOT NULL,
                          source TEXT NOT NULL,
                          recorded_at TEXT NOT NULL);

                        CREATE TRIGGER IF NOT EXISTS trg_tse_master_data_no_update
                        BEFORE UPDATE ON tse_master_data
                        BEGIN
                          SELECT RAISE(ABORT,'TSE master data are immutable');
                        END;

                        CREATE TRIGGER IF NOT EXISTS trg_tse_master_data_no_delete
                        BEFORE DELETE ON tse_master_data
                        BEGIN
                          SELECT RAISE(ABORT,'TSE master data cannot be deleted');
                        END;

                        -- R133: Im Haus (1) / Außer Haus (0) of every sale, for
                        -- DSFinV-K Bonpos.INHAUS. NULL for sales from before R133,
                        -- where the choice was not stored.
                        ALTER TABLE sales
                            ADD COLUMN im_haus INTEGER NULL;
                        """;

                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                14,
                "R134_CASH_MOVEMENT_BUSINESS_CASE_AND_TSE",
                static async (c, tx, ct) =>
                {
                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        -- R134: AEAO zu § 146a Nr. 1.10.2 - Geldtransit, Privateinlage,
                        -- Privatentnahme, Lohnzahlung ... are Geschäftsvorfälle. The
                        -- DSFinV-K GV_TYP of every Einlage/Entnahme; '' for movements
                        -- from before R134.
                        ALTER TABLE cash_movements
                            ADD COLUMN business_case TEXT NOT NULL DEFAULT '';

                        -- R134: the TSE result of a production Einlage/Entnahme, one
                        -- final record per movement - the same rule as
                        -- sale_tse_signatures (R122/R129). The outage reason is kept
                        -- with the record.
                        CREATE TABLE IF NOT EXISTS cash_movement_tse_signatures(
                          movement_id INTEGER PRIMARY KEY,
                          client_id TEXT NOT NULL DEFAULT '',
                          transaction_number TEXT NOT NULL DEFAULT '',
                          signature_counter TEXT NOT NULL DEFAULT '',
                          serial_number TEXT NOT NULL DEFAULT '',
                          signature TEXT NOT NULL DEFAULT '',
                          log_time TEXT NOT NULL DEFAULT '',
                          outage INTEGER NOT NULL DEFAULT 0,
                          outage_reason TEXT NOT NULL DEFAULT '',
                          created_at TEXT NOT NULL,
                          FOREIGN KEY(movement_id) REFERENCES cash_movements(id));

                        CREATE TRIGGER IF NOT EXISTS trg_cash_movement_tse_no_update
                        BEFORE UPDATE ON cash_movement_tse_signatures
                        BEGIN
                          SELECT RAISE(ABORT,'cash movement TSE record is final');
                        END;

                        CREATE TRIGGER IF NOT EXISTS trg_cash_movement_tse_no_delete
                        BEFORE DELETE ON cash_movement_tse_signatures
                        BEGIN
                          SELECT RAISE(ABORT,'cash movement TSE record cannot be deleted');
                        END;
                        """;

                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                15,
                "R135_TRAINING_AS_AVTRAINING",
                static async (c, tx, ct) =>
                {
                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        -- R135: training sales of a till that books for real, recorded
                        -- and TSE-secured as AVTraining (AEAO zu § 146a Nr. 1.11.1,
                        -- DSFinV-K 4.2.6). Own tables, never sales: nothing here can
                        -- reach a report, the stock or a Z total. Append-only.
                        CREATE TABLE IF NOT EXISTS training_receipts(
                          id INTEGER PRIMARY KEY AUTOINCREMENT,
                          training_number INTEGER NOT NULL UNIQUE,
                          created_at TEXT NOT NULL,
                          created_at_utc TEXT GENERATED ALWAYS AS (strftime('%Y-%m-%dT%H:%M:%fZ', created_at)) VIRTUAL,
                          operator_name TEXT NOT NULL,
                          payment_method TEXT NOT NULL,
                          discount_cents INTEGER NOT NULL,
                          total_cents INTEGER NOT NULL,
                          cash_portion_cents INTEGER NOT NULL,
                          card_portion_cents INTEGER NOT NULL,
                          im_haus INTEGER NOT NULL);

                        CREATE TABLE IF NOT EXISTS training_receipt_items(
                          id INTEGER PRIMARY KEY AUTOINCREMENT,
                          training_id INTEGER NOT NULL REFERENCES training_receipts(id),
                          product_id INTEGER NOT NULL,
                          product_name TEXT NOT NULL,
                          variant_name TEXT NOT NULL,
                          barcode TEXT NOT NULL,
                          quantity REAL NOT NULL,
                          unit_price_cents INTEGER NOT NULL,
                          vat_rate REAL NOT NULL,
                          pfand_cents INTEGER NOT NULL,
                          line_total_cents INTEGER NOT NULL,
                          list_unit_price_cents INTEGER NOT NULL,
                          promotion_id INTEGER NOT NULL,
                          promotion_name TEXT NOT NULL,
                          promotion_percent INTEGER NOT NULL,
                          promotion_discount_unit_cents INTEGER NOT NULL);

                        CREATE TABLE IF NOT EXISTS training_tse_signatures(
                          training_id INTEGER PRIMARY KEY REFERENCES training_receipts(id),
                          client_id TEXT NOT NULL DEFAULT '',
                          transaction_number TEXT NOT NULL DEFAULT '',
                          signature_counter TEXT NOT NULL DEFAULT '',
                          serial_number TEXT NOT NULL DEFAULT '',
                          signature TEXT NOT NULL DEFAULT '',
                          log_time TEXT NOT NULL DEFAULT '',
                          outage INTEGER NOT NULL DEFAULT 0,
                          outage_reason TEXT NOT NULL DEFAULT '',
                          created_at TEXT NOT NULL);

                        CREATE INDEX IF NOT EXISTS ix_training_receipts_created_utc
                            ON training_receipts(created_at_utc);

                        CREATE TRIGGER IF NOT EXISTS trg_training_receipts_no_update BEFORE UPDATE ON training_receipts
                        BEGIN SELECT RAISE(ABORT,'training receipts are immutable'); END;
                        CREATE TRIGGER IF NOT EXISTS trg_training_receipts_no_delete BEFORE DELETE ON training_receipts
                        BEGIN SELECT RAISE(ABORT,'training receipts cannot be deleted'); END;
                        CREATE TRIGGER IF NOT EXISTS trg_training_items_no_update BEFORE UPDATE ON training_receipt_items
                        BEGIN SELECT RAISE(ABORT,'training receipt items are immutable'); END;
                        CREATE TRIGGER IF NOT EXISTS trg_training_items_no_delete BEFORE DELETE ON training_receipt_items
                        BEGIN SELECT RAISE(ABORT,'training receipt items cannot be deleted'); END;
                        CREATE TRIGGER IF NOT EXISTS trg_training_tse_no_update BEFORE UPDATE ON training_tse_signatures
                        BEGIN SELECT RAISE(ABORT,'training TSE record is final'); END;
                        CREATE TRIGGER IF NOT EXISTS trg_training_tse_no_delete BEFORE DELETE ON training_tse_signatures
                        BEGIN SELECT RAISE(ABORT,'training TSE record cannot be deleted'); END;
                        """;

                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                16,
                "R136_TSE_START_AT_VORGANGSBEGINN",
                static async (c, tx, ct) =>
                {
                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        -- R136: AEAO zu § 146a Nr. 2.2.2 - the TSE transaction is started
                        -- when the Vorgang begins (first position), not after payment.
                        -- Operational state of the open transactions, kept across a
                        -- restart; the fiscal records stay in the *_tse_signatures tables.
                        CREATE TABLE IF NOT EXISTS tse_vorgaenge(
                          id TEXT PRIMARY KEY,
                          training INTEGER NOT NULL DEFAULT 0,
                          started_at TEXT NOT NULL,
                          client_id TEXT NOT NULL DEFAULT '',
                          transaction_number TEXT NOT NULL DEFAULT '',
                          start_log_time TEXT NOT NULL DEFAULT '',
                          start_error TEXT NOT NULL DEFAULT '',
                          state TEXT NOT NULL CHECK(state IN ('OPEN','PARKED','FINISHED','ABORTED')),
                          parked_receipt_id INTEGER NULL,
                          reference TEXT NOT NULL DEFAULT '',
                          updated_at TEXT NOT NULL);

                        CREATE INDEX IF NOT EXISTS ix_tse_vorgaenge_state
                            ON tse_vorgaenge(state);

                        -- R136: a Vorgang that ended without becoming a receipt
                        -- (AEAO Nr. 1.11.1 "Belegabbrüche", DSFinV-K AVBelegabbruch).
                        -- Append-only, like every other fiscal record.
                        CREATE TABLE IF NOT EXISTS aborted_vorgaenge(
                          id INTEGER PRIMARY KEY AUTOINCREMENT,
                          vorgang_id TEXT NOT NULL UNIQUE,
                          training INTEGER NOT NULL,
                          started_at TEXT NOT NULL,
                          ended_at TEXT NOT NULL,
                          ended_at_utc TEXT GENERATED ALWAYS AS (strftime('%Y-%m-%dT%H:%M:%fZ', ended_at)) VIRTUAL,
                          operator_name TEXT NOT NULL,
                          discount_cents INTEGER NOT NULL,
                          total_cents INTEGER NOT NULL,
                          client_id TEXT NOT NULL DEFAULT '',
                          transaction_number TEXT NOT NULL DEFAULT '',
                          signature_counter TEXT NOT NULL DEFAULT '',
                          serial_number TEXT NOT NULL DEFAULT '',
                          signature TEXT NOT NULL DEFAULT '',
                          start_log_time TEXT NOT NULL DEFAULT '',
                          log_time TEXT NOT NULL DEFAULT '',
                          outage INTEGER NOT NULL DEFAULT 0,
                          outage_reason TEXT NOT NULL DEFAULT '');

                        CREATE TABLE IF NOT EXISTS aborted_vorgang_items(
                          id INTEGER PRIMARY KEY AUTOINCREMENT,
                          aborted_id INTEGER NOT NULL REFERENCES aborted_vorgaenge(id),
                          product_id INTEGER NOT NULL,
                          product_name TEXT NOT NULL,
                          variant_name TEXT NOT NULL,
                          barcode TEXT NOT NULL,
                          quantity REAL NOT NULL,
                          unit_price_cents INTEGER NOT NULL,
                          vat_rate REAL NOT NULL,
                          pfand_cents INTEGER NOT NULL,
                          line_total_cents INTEGER NOT NULL,
                          list_unit_price_cents INTEGER NOT NULL,
                          promotion_id INTEGER NOT NULL,
                          promotion_name TEXT NOT NULL,
                          promotion_percent INTEGER NOT NULL,
                          promotion_discount_unit_cents INTEGER NOT NULL);

                        CREATE INDEX IF NOT EXISTS ix_aborted_vorgaenge_ended_utc
                            ON aborted_vorgaenge(ended_at_utc);

                        CREATE TRIGGER IF NOT EXISTS trg_aborted_vorgaenge_no_update BEFORE UPDATE ON aborted_vorgaenge
                        BEGIN SELECT RAISE(ABORT,'aborted Vorgang records are immutable'); END;
                        CREATE TRIGGER IF NOT EXISTS trg_aborted_vorgaenge_no_delete BEFORE DELETE ON aborted_vorgaenge
                        BEGIN SELECT RAISE(ABORT,'aborted Vorgang records cannot be deleted'); END;
                        CREATE TRIGGER IF NOT EXISTS trg_aborted_items_no_update BEFORE UPDATE ON aborted_vorgang_items
                        BEGIN SELECT RAISE(ABORT,'aborted Vorgang items are immutable'); END;
                        CREATE TRIGGER IF NOT EXISTS trg_aborted_items_no_delete BEFORE DELETE ON aborted_vorgang_items
                        BEGIN SELECT RAISE(ABORT,'aborted Vorgang items cannot be deleted'); END;

                        -- R136: Vorgangsbeginn (DSFinV-K BON_START) and the TSE log time
                        -- of StartTransaction (TSE_TA_START). NULL / '' for records from
                        -- before R136.
                        ALTER TABLE sales ADD COLUMN started_at TEXT NULL;
                        ALTER TABLE sale_tse_signatures ADD COLUMN start_log_time TEXT NOT NULL DEFAULT '';
                        ALTER TABLE cash_movement_tse_signatures ADD COLUMN start_log_time TEXT NOT NULL DEFAULT '';
                        ALTER TABLE training_receipts ADD COLUMN started_at TEXT NULL;
                        ALTER TABLE training_tse_signatures ADD COLUMN start_log_time TEXT NOT NULL DEFAULT '';
                        ALTER TABLE parked_receipts ADD COLUMN vorgang_started_at TEXT NULL;
                        ALTER TABLE parked_receipts ADD COLUMN tse_start_log_time TEXT NOT NULL DEFAULT '';
                        """;

                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                17,
                "R137_ORDER_CHANGES_SECURED",
                static async (c, tx, ct) =>
                {
                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        -- R137: DSFinV-K 4.2.3 - orders are Vorgänge of their own. Every
                        -- acceptance, change and cancellation of an order is its own
                        -- Bestellung-V1 transaction; a change holds only the difference, a
                        -- cancellation everything secured before with reversed sign. One
                        -- immutable record each, with its positions and TSE result.
                        CREATE TABLE IF NOT EXISTS order_bestellungen(
                          id INTEGER PRIMARY KEY AUTOINCREMENT,
                          parked_receipt_id INTEGER NOT NULL REFERENCES parked_receipts(id),
                          sequence INTEGER NOT NULL,
                          kind TEXT NOT NULL CHECK(kind IN ('ANNAHME','AENDERUNG','STORNO')),
                          started_at TEXT NOT NULL,
                          created_at TEXT NOT NULL,
                          created_at_utc TEXT GENERATED ALWAYS AS (strftime('%Y-%m-%dT%H:%M:%fZ', created_at)) VIRTUAL,
                          operator_name TEXT NOT NULL,
                          im_haus INTEGER NOT NULL,
                          total_cents INTEGER NOT NULL,
                          client_id TEXT NOT NULL DEFAULT '',
                          transaction_number TEXT NOT NULL DEFAULT '',
                          signature_counter TEXT NOT NULL DEFAULT '',
                          serial_number TEXT NOT NULL DEFAULT '',
                          signature TEXT NOT NULL DEFAULT '',
                          start_log_time TEXT NOT NULL DEFAULT '',
                          log_time TEXT NOT NULL DEFAULT '',
                          outage INTEGER NOT NULL DEFAULT 0,
                          outage_reason TEXT NOT NULL DEFAULT '',
                          UNIQUE(parked_receipt_id, sequence));

                        CREATE TABLE IF NOT EXISTS order_bestellung_items(
                          id INTEGER PRIMARY KEY AUTOINCREMENT,
                          bestellung_id INTEGER NOT NULL REFERENCES order_bestellungen(id),
                          product_id INTEGER NOT NULL,
                          product_name TEXT NOT NULL,
                          variant_name TEXT NOT NULL,
                          barcode TEXT NOT NULL,
                          quantity REAL NOT NULL,
                          unit_price_cents INTEGER NOT NULL,
                          vat_rate REAL NOT NULL,
                          pfand_cents INTEGER NOT NULL,
                          line_total_cents INTEGER NOT NULL,
                          list_unit_price_cents INTEGER NOT NULL,
                          promotion_id INTEGER NOT NULL,
                          promotion_name TEXT NOT NULL,
                          promotion_percent INTEGER NOT NULL,
                          promotion_discount_unit_cents INTEGER NOT NULL);

                        CREATE INDEX IF NOT EXISTS ix_order_bestellungen_created_utc
                            ON order_bestellungen(created_at_utc);

                        CREATE TRIGGER IF NOT EXISTS trg_order_bestellungen_no_update BEFORE UPDATE ON order_bestellungen
                        BEGIN SELECT RAISE(ABORT,'order records are immutable'); END;
                        CREATE TRIGGER IF NOT EXISTS trg_order_bestellungen_no_delete BEFORE DELETE ON order_bestellungen
                        BEGIN SELECT RAISE(ABORT,'order records cannot be deleted'); END;
                        CREATE TRIGGER IF NOT EXISTS trg_order_bestellung_items_no_update BEFORE UPDATE ON order_bestellung_items
                        BEGIN SELECT RAISE(ABORT,'order record items are immutable'); END;
                        CREATE TRIGGER IF NOT EXISTS trg_order_bestellung_items_no_delete BEFORE DELETE ON order_bestellung_items
                        BEGIN SELECT RAISE(ABORT,'order record items cannot be deleted'); END;
                        """;

                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                18,
                "R143_CANCELLED_POSITIONS",
                static async (c, tx, ct) =>
                {
                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        -- R143: DSFinV-K 4.2.3 - positions cancelled during capture
                        -- (SOFORT STORNO, a lowered quantity) belong to the Einzelaufzeichnung
                        -- of the receipt; AEAO zu § 146a Nr. 1.11.1 names the Sofort-Stornierung.
                        -- Stored once with the receipt, immutable.
                        CREATE TABLE IF NOT EXISTS sale_cancelled_items(
                          id INTEGER PRIMARY KEY AUTOINCREMENT,
                          sale_id INTEGER NOT NULL REFERENCES sales(id),
                          product_id INTEGER NOT NULL,
                          product_name TEXT NOT NULL,
                          variant_name TEXT NOT NULL,
                          barcode TEXT NOT NULL,
                          quantity REAL NOT NULL,
                          unit_price_cents INTEGER NOT NULL,
                          vat_rate REAL NOT NULL,
                          pfand_cents INTEGER NOT NULL,
                          list_unit_price_cents INTEGER NOT NULL,
                          promotion_id INTEGER NOT NULL,
                          promotion_name TEXT NOT NULL,
                          promotion_percent INTEGER NOT NULL,
                          promotion_discount_unit_cents INTEGER NOT NULL);

                        CREATE TABLE IF NOT EXISTS training_cancelled_items(
                          id INTEGER PRIMARY KEY AUTOINCREMENT,
                          training_id INTEGER NOT NULL REFERENCES training_receipts(id),
                          product_id INTEGER NOT NULL,
                          product_name TEXT NOT NULL,
                          variant_name TEXT NOT NULL,
                          barcode TEXT NOT NULL,
                          quantity REAL NOT NULL,
                          unit_price_cents INTEGER NOT NULL,
                          vat_rate REAL NOT NULL,
                          pfand_cents INTEGER NOT NULL,
                          list_unit_price_cents INTEGER NOT NULL,
                          promotion_id INTEGER NOT NULL,
                          promotion_name TEXT NOT NULL,
                          promotion_percent INTEGER NOT NULL,
                          promotion_discount_unit_cents INTEGER NOT NULL);

                        CREATE INDEX IF NOT EXISTS ix_sale_cancelled_items_sale ON sale_cancelled_items(sale_id);
                        CREATE INDEX IF NOT EXISTS ix_training_cancelled_items_training ON training_cancelled_items(training_id);

                        CREATE TRIGGER IF NOT EXISTS trg_sale_cancelled_items_no_update BEFORE UPDATE ON sale_cancelled_items
                        BEGIN SELECT RAISE(ABORT,'cancelled positions are immutable'); END;
                        CREATE TRIGGER IF NOT EXISTS trg_sale_cancelled_items_no_delete BEFORE DELETE ON sale_cancelled_items
                        BEGIN SELECT RAISE(ABORT,'cancelled positions cannot be deleted'); END;
                        CREATE TRIGGER IF NOT EXISTS trg_training_cancelled_items_no_update BEFORE UPDATE ON training_cancelled_items
                        BEGIN SELECT RAISE(ABORT,'cancelled positions are immutable'); END;
                        CREATE TRIGGER IF NOT EXISTS trg_training_cancelled_items_no_delete BEFORE DELETE ON training_cancelled_items
                        BEGIN SELECT RAISE(ABORT,'cancelled positions cannot be deleted'); END;
                        """;

                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                19,
                "R147_TRAINING_ORDER_LINK",
                static async (c, tx, ct) =>
                {
                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        -- R147: DSFinV-K 2.7.1 - the order a training receipt paid, so
                        -- its Abrechnungskreis links it to the training order records
                        -- as a real receipt is linked through cashed_sale_id. Immutable.
                        CREATE TABLE IF NOT EXISTS training_receipt_orders(
                          training_id INTEGER PRIMARY KEY REFERENCES training_receipts(id),
                          parked_receipt_id INTEGER NOT NULL REFERENCES parked_receipts(id));

                        CREATE TRIGGER IF NOT EXISTS trg_training_receipt_orders_no_update BEFORE UPDATE ON training_receipt_orders
                        BEGIN SELECT RAISE(ABORT,'training order links are immutable'); END;
                        CREATE TRIGGER IF NOT EXISTS trg_training_receipt_orders_no_delete BEFORE DELETE ON training_receipt_orders
                        BEGIN SELECT RAISE(ABORT,'training order links cannot be deleted'); END;
                        """;

                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                20,
                "R151_MENU_VAT_ALLOCATION_SNAPSHOTS",
                static async (c, tx, ct) =>
                {
                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        -- R151: one customer-facing menu position may contain more
                        -- than one VAT rate internally. Persist the exact per-unit
                        -- allocation snapshot next to every position type that can
                        -- later become part of a receipt / order / reversal.
                        ALTER TABLE sale_items
                            ADD COLUMN vat_allocations_json TEXT NOT NULL DEFAULT '';
                        ALTER TABLE parked_receipt_items
                            ADD COLUMN vat_allocations_json TEXT NOT NULL DEFAULT '';
                        ALTER TABLE training_receipt_items
                            ADD COLUMN vat_allocations_json TEXT NOT NULL DEFAULT '';
                        ALTER TABLE order_bestellung_items
                            ADD COLUMN vat_allocations_json TEXT NOT NULL DEFAULT '';
                        ALTER TABLE sale_cancelled_items
                            ADD COLUMN vat_allocations_json TEXT NOT NULL DEFAULT '';
                        ALTER TABLE training_cancelled_items
                            ADD COLUMN vat_allocations_json TEXT NOT NULL DEFAULT '';
                        """;

                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                21,
                "R153_MENU_CHOICE_GROUPS",
                static async (c, tx, ct) =>
                {
                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        -- R153: fixed menu components keep choice_group empty.
                        -- Rows sharing a non-empty choice_group are alternatives;
                        -- exactly one is selected at sale time.
                        ALTER TABLE product_combo_items
                            ADD COLUMN choice_group TEXT NOT NULL DEFAULT '';

                        -- Persist the exact selected ingredients of each commercial
                        -- menu line. This makes stock reversal and historical replay
                        -- independent of later recipe / Artikelstamm changes.
                        ALTER TABLE sale_items
                            ADD COLUMN menu_components_json TEXT NOT NULL DEFAULT '';
                        ALTER TABLE parked_receipt_items
                            ADD COLUMN menu_components_json TEXT NOT NULL DEFAULT '';
                        ALTER TABLE training_receipt_items
                            ADD COLUMN menu_components_json TEXT NOT NULL DEFAULT '';
                        ALTER TABLE order_bestellung_items
                            ADD COLUMN menu_components_json TEXT NOT NULL DEFAULT '';
                        ALTER TABLE sale_cancelled_items
                            ADD COLUMN menu_components_json TEXT NOT NULL DEFAULT '';
                        ALTER TABLE training_cancelled_items
                            ADD COLUMN menu_components_json TEXT NOT NULL DEFAULT '';
                        """;

                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                22,
                "R154_DATEV_KASSENARCHIV_OUTBOX",
                static async (c, tx, ct) =>
                {
                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        CREATE TABLE IF NOT EXISTS datev_kassenarchiv_outbox(
                          id INTEGER PRIMARY KEY AUTOINCREMENT,
                          z_archive_id INTEGER NOT NULL UNIQUE REFERENCES z_report_archive(id),
                          z_number INTEGER NOT NULL,
                          created_at TEXT NOT NULL,
                          period_from TEXT NOT NULL,
                          period_to TEXT NOT NULL,
                          state TEXT NOT NULL DEFAULT 'PREPARING',
                          package_path TEXT NOT NULL DEFAULT '',
                          package_sha256 TEXT NOT NULL DEFAULT '',
                          attempt_count INTEGER NOT NULL DEFAULT 0,
                          last_attempt_at TEXT,
                          last_error TEXT NOT NULL DEFAULT '',
                          remote_archive_id TEXT NOT NULL DEFAULT '',
                          sent_at TEXT);

                        CREATE UNIQUE INDEX IF NOT EXISTS ux_datev_kassenarchiv_z_number
                          ON datev_kassenarchiv_outbox(z_number);
                        CREATE INDEX IF NOT EXISTS ix_datev_kassenarchiv_state
                          ON datev_kassenarchiv_outbox(state,z_number);

                        CREATE TRIGGER IF NOT EXISTS trg_datev_kassenarchiv_identity_no_update
                        BEFORE UPDATE OF z_archive_id,z_number,period_from,period_to,package_path,package_sha256
                        ON datev_kassenarchiv_outbox
                        WHEN OLD.package_sha256<>'' OR OLD.state='SENT'
                        BEGIN
                          SELECT RAISE(ABORT,'DATEV Kassenarchiv package identity is immutable');
                        END;

                        CREATE TRIGGER IF NOT EXISTS trg_datev_kassenarchiv_no_delete
                        BEFORE DELETE ON datev_kassenarchiv_outbox
                        BEGIN
                          SELECT RAISE(ABORT,'DATEV Kassenarchiv outbox cannot be deleted');
                        END;
                        """;

                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                23,
                "R155_DATEV_KASSENBUCH_ASCII_EXPORT",
                static async (c, tx, ct) =>
                {
                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        CREATE TABLE IF NOT EXISTS datev_kassenbuch_ascii_exports(
                          id INTEGER PRIMARY KEY AUTOINCREMENT,
                          z_archive_id INTEGER NOT NULL UNIQUE REFERENCES z_report_archive(id),
                          z_number INTEGER NOT NULL UNIQUE,
                          created_at TEXT NOT NULL,
                          csv_path TEXT NOT NULL DEFAULT '',
                          csv_sha256 TEXT NOT NULL DEFAULT '',
                          pdf_path TEXT NOT NULL DEFAULT '',
                          email_state TEXT NOT NULL DEFAULT 'READY',
                          email_recipient TEXT NOT NULL DEFAULT '',
                          email_attempts INTEGER NOT NULL DEFAULT 0,
                          email_sent_at TEXT,
                          last_error TEXT NOT NULL DEFAULT '');

                        CREATE INDEX IF NOT EXISTS ix_datev_ascii_email_state
                          ON datev_kassenbuch_ascii_exports(email_state,z_number);

                        CREATE TRIGGER IF NOT EXISTS trg_datev_ascii_identity_no_update
                        BEFORE UPDATE OF z_archive_id,z_number,csv_path,csv_sha256,pdf_path
                        ON datev_kassenbuch_ascii_exports
                        WHEN OLD.csv_sha256<>''
                        BEGIN
                          SELECT RAISE(ABORT,'DATEV Kassenbuch ASCII export identity is immutable');
                        END;

                        CREATE TRIGGER IF NOT EXISTS trg_datev_ascii_no_delete
                        BEFORE DELETE ON datev_kassenbuch_ascii_exports
                        BEGIN
                          SELECT RAISE(ABORT,'DATEV Kassenbuch ASCII export log cannot be deleted');
                        END;
                        """;

                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                24,
                "R172_FIXED_POINT_QUANTITIES",
                static async (c, tx, ct) =>
                {
                    // R172: future quantity/stock writes use scaled INTEGER
                    // (1 unit = 1000 milli-units; for kg this is 1 gram).
                    // Historical append-only rows keep quantity_milli=0 and are
                    // read through the legacy REAL fallback. This avoids
                    // rewriting immutable fiscal history during migration.
                    var statements = new[]
                    {
                        "ALTER TABLE products ADD COLUMN stock_milli INTEGER NOT NULL DEFAULT 0;",
                        "ALTER TABLE products ADD COLUMN min_stock_milli INTEGER NOT NULL DEFAULT 0;",
                        "ALTER TABLE product_combo_items ADD COLUMN quantity_milli INTEGER NOT NULL DEFAULT 0;",
                        "ALTER TABLE sale_items ADD COLUMN quantity_milli INTEGER NOT NULL DEFAULT 0;",
                        "ALTER TABLE parked_receipt_items ADD COLUMN quantity_milli INTEGER NOT NULL DEFAULT 0;",
                        "ALTER TABLE training_receipt_items ADD COLUMN quantity_milli INTEGER NOT NULL DEFAULT 0;",
                        "ALTER TABLE aborted_vorgang_items ADD COLUMN quantity_milli INTEGER NOT NULL DEFAULT 0;",
                        "ALTER TABLE order_bestellung_items ADD COLUMN quantity_milli INTEGER NOT NULL DEFAULT 0;",
                        "ALTER TABLE sale_cancelled_items ADD COLUMN quantity_milli INTEGER NOT NULL DEFAULT 0;",
                        "ALTER TABLE training_cancelled_items ADD COLUMN quantity_milli INTEGER NOT NULL DEFAULT 0;"
                    };

                    foreach (var sql in statements)
                    {
                        await using var q = c.CreateCommand();
                        q.Transaction = tx;
                        q.CommandText = sql;
                        await q.ExecuteNonQueryAsync(ct);
                    }

                    await using (var backfill = c.CreateCommand())
                    {
                        backfill.Transaction = tx;
                        backfill.CommandText = """
                            UPDATE products
                            SET stock_milli=CAST(ROUND(COALESCE(stock_quantity,0)*1000.0) AS INTEGER),
                                min_stock_milli=CAST(ROUND(COALESCE(min_stock_quantity,0)*1000.0) AS INTEGER);

                            UPDATE product_combo_items
                            SET quantity_milli=CAST(ROUND(quantity*1000.0) AS INTEGER);

                            UPDATE parked_receipt_items
                            SET quantity_milli=CAST(ROUND(quantity*1000.0) AS INTEGER);
                            """;
                        await backfill.ExecuteNonQueryAsync(ct);
                    }

                    // The new TOR code writes both the INTEGER value and
                    // the legacy REAL compatibility mirror. No INSERT trigger
                    // is used here: historical test/import tooling and older
                    // companion builds may still write only the REAL column.
                    // Readers therefore prefer *_milli when present and fall
                    // back to a one-time REAL->milli conversion for such rows.
                }),

            new(
                25,
                "R183_RESTAURANT_FOUNDATION",
                static async (c, tx, ct) =>
                {
                    // TOR Restaurant has its own application data root. Keep the
                    // restaurant-only schema out of Einzelhandel/Gastro databases
                    // even though all products share the ordered migration ledger.
                    var edition = Environment.GetEnvironmentVariable("TOR_POS_PRODUCT_EDITION");
                    if (!string.Equals(edition, "RESTAURANT", StringComparison.OrdinalIgnoreCase))
                        return;

                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        CREATE TABLE IF NOT EXISTS restaurant_areas(
                          id INTEGER PRIMARY KEY AUTOINCREMENT,
                          name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                          sort_order INTEGER NOT NULL DEFAULT 0,
                          is_active INTEGER NOT NULL DEFAULT 1 CHECK(is_active IN (0,1)));

                        CREATE TABLE IF NOT EXISTS restaurant_tables(
                          id INTEGER PRIMARY KEY AUTOINCREMENT,
                          area_id INTEGER NOT NULL REFERENCES restaurant_areas(id),
                          code TEXT NOT NULL COLLATE NOCASE UNIQUE,
                          display_name TEXT NOT NULL,
                          seats INTEGER NOT NULL DEFAULT 2 CHECK(seats BETWEEN 1 AND 99),
                          sort_order INTEGER NOT NULL DEFAULT 0,
                          is_active INTEGER NOT NULL DEFAULT 1 CHECK(is_active IN (0,1)),
                          version INTEGER NOT NULL DEFAULT 1 CHECK(version>=1));

                        CREATE INDEX IF NOT EXISTS ix_restaurant_tables_area_sort
                          ON restaurant_tables(area_id,is_active,sort_order,id);

                        CREATE TABLE IF NOT EXISTS restaurant_sessions(
                          id TEXT PRIMARY KEY,
                          table_id INTEGER NOT NULL REFERENCES restaurant_tables(id),
                          opened_at TEXT NOT NULL,
                          updated_at TEXT NOT NULL,
                          closed_at TEXT NULL,
                          state TEXT NOT NULL CHECK(state IN ('OPEN','CHECK_REQUESTED','CLOSED','CANCELLED')),
                          opened_by TEXT NOT NULL,
                          assigned_waiter TEXT NOT NULL,
                          guest_count INTEGER NOT NULL DEFAULT 1 CHECK(guest_count BETWEEN 1 AND 999),
                          note TEXT NOT NULL DEFAULT '',
                          version INTEGER NOT NULL DEFAULT 1 CHECK(version>=1));

                        CREATE UNIQUE INDEX IF NOT EXISTS ux_restaurant_one_live_session_per_table
                          ON restaurant_sessions(table_id)
                          WHERE state IN ('OPEN','CHECK_REQUESTED');

                        CREATE INDEX IF NOT EXISTS ix_restaurant_sessions_state
                          ON restaurant_sessions(state,updated_at);

                        CREATE TABLE IF NOT EXISTS restaurant_session_items(
                          id INTEGER PRIMARY KEY AUTOINCREMENT,
                          session_id TEXT NOT NULL REFERENCES restaurant_sessions(id),
                          line_token TEXT NOT NULL UNIQUE,
                          product_id INTEGER NOT NULL,
                          product_name TEXT NOT NULL,
                          variant_name TEXT NOT NULL DEFAULT '',
                          quantity_milli INTEGER NOT NULL CHECK(quantity_milli>0),
                          unit_price_cents INTEGER NOT NULL CHECK(unit_price_cents>=0),
                          vat_rate REAL NOT NULL,
                          pfand_cents INTEGER NOT NULL DEFAULT 0 CHECK(pfand_cents>=0),
                          state TEXT NOT NULL DEFAULT 'ACTIVE' CHECK(state IN ('ACTIVE','PAID','CANCELLED')),
                          added_by TEXT NOT NULL,
                          added_at TEXT NOT NULL,
                          version INTEGER NOT NULL DEFAULT 1 CHECK(version>=1));

                        CREATE INDEX IF NOT EXISTS ix_restaurant_session_items_session
                          ON restaurant_session_items(session_id,state,id);

                        CREATE TABLE IF NOT EXISTS restaurant_session_events(
                          id INTEGER PRIMARY KEY AUTOINCREMENT,
                          session_id TEXT NOT NULL REFERENCES restaurant_sessions(id),
                          event_type TEXT NOT NULL,
                          actor TEXT NOT NULL,
                          device_id TEXT NOT NULL DEFAULT '',
                          created_at TEXT NOT NULL,
                          payload_json TEXT NOT NULL DEFAULT '');

                        CREATE INDEX IF NOT EXISTS ix_restaurant_session_events_session
                          ON restaurant_session_events(session_id,id);

                        CREATE TRIGGER IF NOT EXISTS trg_restaurant_events_no_update
                        BEFORE UPDATE ON restaurant_session_events
                        BEGIN
                          SELECT RAISE(ABORT,'restaurant session events are append-only');
                        END;

                        CREATE TRIGGER IF NOT EXISTS trg_restaurant_events_no_delete
                        BEFORE DELETE ON restaurant_session_events
                        BEGIN
                          SELECT RAISE(ABORT,'restaurant session events cannot be deleted');
                        END;
                        """;

                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                26,
                "R184_RESTAURANT_PAYMENT_RESERVATIONS",
                static async (c, tx, ct) =>
                {
                    var edition = Environment.GetEnvironmentVariable("TOR_POS_PRODUCT_EDITION");
                    if (!string.Equals(edition, "RESTAURANT", StringComparison.OrdinalIgnoreCase))
                        return;

                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        CREATE TABLE IF NOT EXISTS restaurant_payment_reservations(
                          operation_id TEXT PRIMARY KEY,
                          session_id TEXT NOT NULL REFERENCES restaurant_sessions(id),
                          expected_session_version INTEGER NOT NULL,
                          state TEXT NOT NULL CHECK(state IN ('PREPARED','CANCELLED','APPLIED')),
                          created_at TEXT NOT NULL,
                          updated_at TEXT NOT NULL,
                          sale_id INTEGER NULL UNIQUE REFERENCES sales(id));

                        CREATE TABLE IF NOT EXISTS restaurant_payment_reservation_items(
                          operation_id TEXT NOT NULL REFERENCES restaurant_payment_reservations(operation_id),
                          session_item_id INTEGER NOT NULL REFERENCES restaurant_session_items(id),
                          quantity_milli INTEGER NOT NULL CHECK(quantity_milli>0),
                          amount_cents INTEGER NOT NULL CHECK(amount_cents>=0),
                          PRIMARY KEY(operation_id,session_item_id));

                        CREATE INDEX IF NOT EXISTS ix_restaurant_payment_session
                          ON restaurant_payment_reservations(session_id,state,created_at);
                        """;
                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                27,
                "R185_RESTAURANT_BESTELLUNG_TSE",
                static async (c, tx, ct) =>
                {
                    var edition = Environment.GetEnvironmentVariable("TOR_POS_PRODUCT_EDITION");
                    if (!string.Equals(edition, "RESTAURANT", StringComparison.OrdinalIgnoreCase))
                        return;

                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        CREATE TABLE IF NOT EXISTS restaurant_bestellungen(
                          id INTEGER PRIMARY KEY AUTOINCREMENT,
                          session_id TEXT NOT NULL REFERENCES restaurant_sessions(id),
                          sequence INTEGER NOT NULL,
                          kind TEXT NOT NULL CHECK(kind IN ('ANNAHME','AENDERUNG','STORNO')),
                          started_at TEXT NOT NULL,
                          created_at TEXT NOT NULL,
                          created_at_utc TEXT GENERATED ALWAYS AS (strftime('%Y-%m-%dT%H:%M:%fZ', created_at)) VIRTUAL,
                          operator_name TEXT NOT NULL,
                          total_cents INTEGER NOT NULL,
                          client_id TEXT NOT NULL DEFAULT '',
                          transaction_number TEXT NOT NULL DEFAULT '',
                          signature_counter TEXT NOT NULL DEFAULT '',
                          serial_number TEXT NOT NULL DEFAULT '',
                          signature TEXT NOT NULL DEFAULT '',
                          start_log_time TEXT NOT NULL DEFAULT '',
                          log_time TEXT NOT NULL DEFAULT '',
                          outage INTEGER NOT NULL DEFAULT 0,
                          outage_reason TEXT NOT NULL DEFAULT '',
                          UNIQUE(session_id,sequence));

                        CREATE TABLE IF NOT EXISTS restaurant_bestellung_items(
                          id INTEGER PRIMARY KEY AUTOINCREMENT,
                          bestellung_id INTEGER NOT NULL REFERENCES restaurant_bestellungen(id),
                          product_id INTEGER NOT NULL,
                          product_name TEXT NOT NULL,
                          quantity_milli INTEGER NOT NULL,
                          unit_price_cents INTEGER NOT NULL,
                          vat_rate REAL NOT NULL,
                          pfand_cents INTEGER NOT NULL DEFAULT 0);

                        CREATE INDEX IF NOT EXISTS ix_restaurant_bestellungen_session
                          ON restaurant_bestellungen(session_id,sequence);

                        CREATE INDEX IF NOT EXISTS ix_restaurant_bestellungen_created
                          ON restaurant_bestellungen(created_at_utc);

                        CREATE TRIGGER IF NOT EXISTS trg_restaurant_bestellungen_no_update
                        BEFORE UPDATE ON restaurant_bestellungen
                        BEGIN
                          SELECT RAISE(ABORT,'restaurant order records are immutable');
                        END;

                        CREATE TRIGGER IF NOT EXISTS trg_restaurant_bestellungen_no_delete
                        BEFORE DELETE ON restaurant_bestellungen
                        BEGIN
                          SELECT RAISE(ABORT,'restaurant order records cannot be deleted');
                        END;

                        CREATE TRIGGER IF NOT EXISTS trg_restaurant_bestellung_items_no_update
                        BEFORE UPDATE ON restaurant_bestellung_items
                        BEGIN
                          SELECT RAISE(ABORT,'restaurant order items are immutable');
                        END;

                        CREATE TRIGGER IF NOT EXISTS trg_restaurant_bestellung_items_no_delete
                        BEFORE DELETE ON restaurant_bestellung_items
                        BEGIN
                          SELECT RAISE(ABORT,'restaurant order items cannot be deleted');
                        END;
                        """;
                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                28,
                "R186_RESTAURANT_KITCHEN_OUTBOX",
                static async (c, tx, ct) =>
                {
                    var edition = Environment.GetEnvironmentVariable("TOR_POS_PRODUCT_EDITION");
                    if (!string.Equals(edition, "RESTAURANT", StringComparison.OrdinalIgnoreCase))
                        return;

                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        CREATE TABLE IF NOT EXISTS restaurant_kitchen_jobs(
                          id TEXT PRIMARY KEY,
                          session_id TEXT NOT NULL REFERENCES restaurant_sessions(id),
                          session_item_id INTEGER NULL REFERENCES restaurant_session_items(id),
                          action TEXT NOT NULL CHECK(action IN ('NEW','CANCEL','MOVE','NOTE')),
                          station TEXT NOT NULL DEFAULT '',
                          printer_name TEXT NOT NULL DEFAULT '',
                          payload_json TEXT NOT NULL,
                          state TEXT NOT NULL CHECK(state IN ('PENDING','HANDED_OVER','FAILED')),
                          attempts INTEGER NOT NULL DEFAULT 0,
                          last_error TEXT NOT NULL DEFAULT '',
                          created_at TEXT NOT NULL,
                          handed_over_at TEXT NULL);

                        CREATE INDEX IF NOT EXISTS ix_restaurant_kitchen_jobs_state
                          ON restaurant_kitchen_jobs(state,created_at);

                        CREATE INDEX IF NOT EXISTS ix_restaurant_kitchen_jobs_session
                          ON restaurant_kitchen_jobs(session_id,created_at);

                        CREATE TABLE IF NOT EXISTS restaurant_kitchen_status(
                          session_item_id INTEGER PRIMARY KEY REFERENCES restaurant_session_items(id),
                          status TEXT NOT NULL CHECK(status IN ('OFFEN','IN_ARBEIT','FERTIG')),
                          updated_at TEXT NOT NULL,
                          updated_by TEXT NOT NULL DEFAULT '');
                        """;
                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                29,
                "R187_RESTAURANT_HANDHELD_PAIRING",
                static async (c, tx, ct) =>
                {
                    var edition = Environment.GetEnvironmentVariable("TOR_POS_PRODUCT_EDITION");
                    if (!string.Equals(edition, "RESTAURANT", StringComparison.OrdinalIgnoreCase))
                        return;

                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        CREATE TABLE IF NOT EXISTS restaurant_pairing_codes(
                          id TEXT PRIMARY KEY,
                          code_hash TEXT NOT NULL,
                          created_at TEXT NOT NULL,
                          expires_at TEXT NOT NULL,
                          created_by TEXT NOT NULL,
                          consumed_at TEXT NULL,
                          consumed_by_device TEXT NOT NULL DEFAULT '');

                        CREATE INDEX IF NOT EXISTS ix_restaurant_pairing_expires
                          ON restaurant_pairing_codes(expires_at,consumed_at);

                        CREATE TABLE IF NOT EXISTS restaurant_handheld_devices(
                          device_id TEXT PRIMARY KEY,
                          display_name TEXT NOT NULL,
                          token_hash TEXT NOT NULL UNIQUE,
                          paired_at TEXT NOT NULL,
                          paired_by TEXT NOT NULL,
                          last_seen_at TEXT NOT NULL,
                          is_active INTEGER NOT NULL DEFAULT 1 CHECK(is_active IN (0,1)));

                        CREATE INDEX IF NOT EXISTS ix_restaurant_handheld_active
                          ON restaurant_handheld_devices(is_active,last_seen_at);
                        """;
                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                30,
                "R188_RESTAURANT_KITCHEN_NOTE",
                static async (c, tx, ct) =>
                {
                    var edition = Environment.GetEnvironmentVariable("TOR_POS_PRODUCT_EDITION");
                    if (!string.Equals(edition, "RESTAURANT", StringComparison.OrdinalIgnoreCase))
                        return;

                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        ALTER TABLE restaurant_kitchen_jobs
                          RENAME TO restaurant_kitchen_jobs_r187;

                        CREATE TABLE restaurant_kitchen_jobs(
                          id TEXT PRIMARY KEY,
                          session_id TEXT NOT NULL REFERENCES restaurant_sessions(id),
                          session_item_id INTEGER NULL REFERENCES restaurant_session_items(id),
                          action TEXT NOT NULL CHECK(action IN ('NEW','CANCEL','MOVE','NOTE')),
                          station TEXT NOT NULL DEFAULT '',
                          printer_name TEXT NOT NULL DEFAULT '',
                          payload_json TEXT NOT NULL,
                          state TEXT NOT NULL CHECK(state IN ('PENDING','HANDED_OVER','FAILED')),
                          attempts INTEGER NOT NULL DEFAULT 0,
                          last_error TEXT NOT NULL DEFAULT '',
                          created_at TEXT NOT NULL,
                          handed_over_at TEXT NULL);

                        INSERT INTO restaurant_kitchen_jobs(
                          id,session_id,session_item_id,action,station,printer_name,
                          payload_json,state,attempts,last_error,created_at,handed_over_at)
                        SELECT
                          id,session_id,session_item_id,action,station,printer_name,
                          payload_json,state,attempts,last_error,created_at,handed_over_at
                        FROM restaurant_kitchen_jobs_r187;

                        DROP TABLE restaurant_kitchen_jobs_r187;

                        CREATE INDEX IF NOT EXISTS ix_restaurant_kitchen_jobs_state
                          ON restaurant_kitchen_jobs(state,created_at);

                        CREATE INDEX IF NOT EXISTS ix_restaurant_kitchen_jobs_session
                          ON restaurant_kitchen_jobs(session_id,created_at);
                        """;
                    await q.ExecuteNonQueryAsync(ct);
                }),

            new(
                31,
                "R189_RESTAURANT_RESERVATIONS",
                static async (c, tx, ct) =>
                {
                    var edition = Environment.GetEnvironmentVariable("TOR_POS_PRODUCT_EDITION");
                    if (!string.Equals(edition, "RESTAURANT", StringComparison.OrdinalIgnoreCase))
                        return;

                    await using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = """
                        CREATE TABLE IF NOT EXISTS restaurant_reservations(
                          id TEXT PRIMARY KEY,
                          reservation_at TEXT NOT NULL,
                          duration_minutes INTEGER NOT NULL DEFAULT 120
                            CHECK(duration_minutes BETWEEN 15 AND 1440),
                          guest_count INTEGER NOT NULL
                            CHECK(guest_count BETWEEN 1 AND 999),
                          customer_name TEXT NOT NULL,
                          phone TEXT NOT NULL DEFAULT '',
                          note TEXT NOT NULL DEFAULT '',
                          table_id INTEGER NULL REFERENCES restaurant_tables(id),
                          status TEXT NOT NULL DEFAULT 'BOOKED'
                            CHECK(status IN ('BOOKED','SEATED','CANCELLED','NO_SHOW','COMPLETED')),
                          created_at TEXT NOT NULL,
                          updated_at TEXT NOT NULL,
                          created_by TEXT NOT NULL,
                          updated_by TEXT NOT NULL,
                          version INTEGER NOT NULL DEFAULT 1 CHECK(version>=1));

                        CREATE INDEX IF NOT EXISTS ix_restaurant_reservations_time
                          ON restaurant_reservations(reservation_at,status);

                        CREATE INDEX IF NOT EXISTS ix_restaurant_reservations_table
                          ON restaurant_reservations(table_id,reservation_at,status);
                        """;
                    await q.ExecuteNonQueryAsync(ct);
                })
        };

    private sealed record DatabaseMigration(
        int Version,
        string Name,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> ApplyAsync);
}

public sealed record SchemaMigrationResult(
    int FromVersion,
    int ToVersion,
    bool ExistingDatabase,
    string? BackupPath,
    IReadOnlyList<int> AppliedVersions);

public sealed record SchemaMigrationStatus(
    int CurrentVersion,
    int TargetVersion,
    bool MetadataPresent,
    string UpdatedAt,
    int HistoryCount)
{
    public bool IsCurrent =>
        MetadataPresent &&
        CurrentVersion == TargetVersion;
}
