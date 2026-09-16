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
    public const int TargetSchemaVersion = 12;

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

    public async Task<SchemaMigrationResult> InitializeDatabaseAsync(
        CancellationToken ct = default)
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
