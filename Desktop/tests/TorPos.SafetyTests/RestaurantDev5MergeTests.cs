using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

// Restaurant DEV5 = DEV4 merged with main. Main took schema 42 for C-4 while
// DEV4 had recorded its Restaurant migrations as 42-47. DEV5 keeps main's 42,
// moves Restaurant to 43-48 and refuses any database whose recorded migration
// names do not match - above all a DEV4 test database, which would otherwise run
// only migration 48 and count as current without C-4's table.
// The checkout part locks how main's O-7 draft discard, main's receipt policy
// and DEV4's reservation cleanup work together at the table.
public static class RestaurantDev5MergeTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "restaurant-dev5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var previousEdition = Environment.GetEnvironmentVariable("TOR_POS_PRODUCT_EDITION");
        try
        {
            MigrationDefinitions(assert);

            Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", null, EnvironmentVariableTarget.Process);
            await RetailMain42Upgrade(dir, assert);

            Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", "RESTAURANT", EnvironmentVariableTarget.Process);
            await FreshRestaurant(dir, assert);
            await RestaurantMain42Upgrade(dir, assert);
            await Dev4DatabaseRefused(dir, assert);
            await WrongNameRefused(dir, assert);
            await ReservationLifecycle(dir, assert);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", previousEdition, EnvironmentVariableTarget.Process);
            SqliteConnection.ClearAllPools();
        }

        CheckoutWiring(assert);
    }

    private static readonly string[] RestaurantDev5Names =
    {
        "RESTAURANT_RECIPES_AND_OPTIONS",
        "RESTAURANT_ORDER_OPTION_SNAPSHOT",
        "RESTAURANT_EXACT_PARTIAL_PAYMENT_CENTS",
        "RESTAURANT_IMMUTABLE_LINE_SNAPSHOTS",
        "RESTAURANT_KDS_ITEM_LOOKUP_INDEX",
        "RESTAURANT_SERVICE_MODE"
    };

    private static void MigrationDefinitions(Action<bool, string> assert)
    {
        var defined = SchemaMigrationService.DefinedMigrations;
        assert(
            SchemaMigrationService.TargetSchemaVersion == 48 &&
            defined.Count == 48 &&
            defined.Select((x, i) => x.Version == i + 1).All(x => x) &&
            defined.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() == defined.Count,
            "DEV5 schema: migrations 1-48 are contiguous and every migration name is used exactly once");

        assert(
            defined[41] == (42, "C4_CLOUD_OUTBOX_REJECTED") &&
            defined.Skip(42).Select(x => x.Name).SequenceEqual(RestaurantDev5Names) &&
            defined.Skip(42).Select(x => x.Version).SequenceEqual(new[] { 43, 44, 45, 46, 47, 48 }),
            "DEV5 schema: main's 42 stays C-4; the Restaurant migrations follow as 43-48 in their DEV4 order");

        // Source guard: what the file declares (not only what the list exposes)
        // - one declaration per number, one number per name, 43-48 Restaurant-
        // only and edition-gated. A parallel branch adding its own 43 or reusing
        // a name fails here, and the next free number stays 49.
        var source = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Infrastructure/SchemaMigrationService.cs"));
        var declared = System.Text.RegularExpressions.Regex
            .Matches(source, @"new\(\s*(\d+)\s*,\s*""([A-Z0-9_.]+)""")
            .Select(m => (Version: int.Parse(m.Groups[1].Value), Name: m.Groups[2].Value, At: m.Index))
            .ToList();
        var restaurantGated = declared
            .Where(d => d.Version is >= 43 and <= 48)
            .All(d =>
            {
                var next = declared.FirstOrDefault(x => x.At > d.At);
                var body = next.Name is null ? source[d.At..] : source[d.At..next.At];
                return d.Name.StartsWith("RESTAURANT_", StringComparison.Ordinal) &&
                       body.Contains("TOR_POS_PRODUCT_EDITION", StringComparison.Ordinal);
            });
        assert(
            declared.Count == SchemaMigrationService.TargetSchemaVersion &&
            declared.Select(d => (d.Version, d.Name)).SequenceEqual(defined) &&
            declared.GroupBy(d => d.Version).All(g => g.Count() == 1) &&
            declared.GroupBy(d => d.Name).All(g => g.Count() == 1) &&
            restaurantGated,
            "DEV5 schema source guard: each migration number and name is declared exactly once, 43-48 are Restaurant-only and edition-gated, the next free number is 49");
    }

    // A) fresh DB -> 48 with a complete, correctly named history.
    private static async Task FreshRestaurant(string dir, Action<bool, string> assert)
    {
        var (db, migrator, backups) = Open(dir, "fresh-restaurant");
        var result = await migrator.InitializeDatabaseAsync();
        var history = await HistoryAsync(db);

        assert(
            result.FromVersion == 0 && result.ToVersion == 48 && result.BackupPath is null &&
            result.AppliedVersions.SequenceEqual(Enumerable.Range(1, 48)) &&
            history.Select(x => (x.Version, x.Name)).SequenceEqual(SchemaMigrationService.DefinedMigrations),
            "DEV5 A: a fresh Restaurant database reaches schema 48 with one history row per migration, 1-48 without gaps and with the defined names");

        await using var c = db.OpenConnection();
        assert(
            TableExists(c, "cloud_outbox_rejected") &&
            TableExists(c, "restaurant_ingredients") &&
            TableExists(c, "restaurant_session_service_mode") &&
            IndexExists(c, "ix_restaurant_kitchen_jobs_item_action"),
            "DEV5 A: the fresh Restaurant database has C-4's table and the Restaurant recipe, service-mode and KDS schema");

        // E) reopening a current database changes nothing.
        var backupsBefore = CountFiles(backups);
        var again = await migrator.InitializeDatabaseAsync();
        assert(
            again.FromVersion == 48 && again.ToVersion == 48 &&
            again.AppliedVersions.Count == 0 && again.BackupPath is null &&
            CountFiles(backups) == backupsBefore &&
            (await HistoryAsync(db)).Count == 48,
            "DEV5 E: reopening a current database applies no migration, writes no backup and adds no history row");
    }

    // B) a database main left at 42 (Restaurant edition) -> 43-48 are applied.
    private static async Task RestaurantMain42Upgrade(string dir, Action<bool, string> assert)
    {
        var (db, main42, _) = Open(dir, "main42-restaurant");
        await main42.InitializeDatabaseUpToAsync(42);
        await MarkRejectedCloudEventAsync(db);

        await using (var c = db.OpenConnection())
        {
            assert(
                await VersionAsync(db) == 42 &&
                TableExists(c, "cloud_outbox_rejected") &&
                !TableExists(c, "restaurant_ingredients"),
                "DEV5 B setup: the main database stands at 42 with C-4's table and without DEV5 Restaurant tables");
        }

        var (_, dev5, backups) = Open(dir, "main42-restaurant");
        var result = await dev5.InitializeDatabaseAsync();
        var history = await HistoryAsync(db);

        assert(
            result.FromVersion == 42 && result.ToVersion == 48 &&
            result.AppliedVersions.SequenceEqual(new[] { 43, 44, 45, 46, 47, 48 }) &&
            result.BackupPath is not null && File.Exists(result.BackupPath) &&
            history.First(x => x.Version == 42).Name == "C4_CLOUD_OUTBOX_REJECTED" &&
            history.Where(x => x.Version > 42).Select(x => x.Name).SequenceEqual(RestaurantDev5Names),
            "DEV5 B: a main schema-42 Restaurant database gets a backup, then exactly 43-48; history 42 still names C-4");

        await using (var c = db.OpenConnection())
        {
            assert(
                await RejectedCloudEventKeptAsync(c) &&
                TableExists(c, "restaurant_ingredients") &&
                TableExists(c, "restaurant_recipes") &&
                TableExists(c, "restaurant_order_options") &&
                TableExists(c, "restaurant_session_service_mode") &&
                ColumnExists(c, "restaurant_session_items", "order_options") &&
                ColumnExists(c, "restaurant_session_items", "paid_cents") &&
                ColumnExists(c, "restaurant_bestellung_items", "vat_allocations_json") &&
                IndexExists(c, "ix_restaurant_kitchen_jobs_item_action"),
                "DEV5 B: the upgrade keeps cloud_outbox_rejected and its parked event and creates the Restaurant tables, columns and KDS index");
        }

        var backupsBefore = CountFiles(backups);
        var again = await dev5.InitializeDatabaseAsync();
        assert(
            again.AppliedVersions.Count == 0 && again.BackupPath is null && CountFiles(backups) == backupsBefore,
            "DEV5 E: reopening the upgraded Restaurant database is idempotent and writes no further backup");
    }

    // Einzelhandel/Gastro: a main-42 till upgrades to 48 without Restaurant schema.
    private static async Task RetailMain42Upgrade(string dir, Action<bool, string> assert)
    {
        var (db, main42, _) = Open(dir, "main42-retail");
        await main42.InitializeDatabaseUpToAsync(42);
        await MarkRejectedCloudEventAsync(db);

        var (_, dev5, _) = Open(dir, "main42-retail");
        var result = await dev5.InitializeDatabaseAsync();

        await using var c = db.OpenConnection();
        assert(
            result.FromVersion == 42 && result.ToVersion == 48 &&
            result.AppliedVersions.SequenceEqual(new[] { 43, 44, 45, 46, 47, 48 }) &&
            await RejectedCloudEventKeptAsync(c) &&
            !TableExists(c, "restaurant_ingredients") &&
            !TableExists(c, "restaurant_session_service_mode"),
            "DEV5 Einzelhandel/Gastro: a main schema-42 till reaches 48, keeps its parked Cloud event and gets no Restaurant tables");
    }

    // C) the DEV4 history (42 = RESTAURANT_RECIPES_AND_OPTIONS ... 47 = SERVICE_MODE).
    private static async Task Dev4DatabaseRefused(string dir, Action<bool, string> assert)
    {
        var (db, fresh, _) = Open(dir, "dev4-restaurant");
        await fresh.InitializeDatabaseAsync();

        // Reproduce what the DEV4 build left behind: the same Restaurant schema,
        // recorded as 42-47, and no C-4 table.
        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = """
                DROP TABLE cloud_outbox_rejected;
                DELETE FROM schema_migrations WHERE version=42;
                UPDATE schema_migrations SET version=version-1 WHERE version BETWEEN 43 AND 48;
                UPDATE schema_version SET version=47 WHERE singleton_id=1;
                -- A table the legacy bootstrap (SqliteDatabase.InitializeAsync)
                -- recreates: if it reappears, the bootstrap ran before the check.
                DROP TABLE category_visual_data;
                PRAGMA wal_checkpoint(TRUNCATE);
                """;
            await q.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        var dev4History = await HistoryAsync(db);
        assert(
            dev4History.Count == 47 &&
            dev4History.First(x => x.Version == 42).Name == "RESTAURANT_RECIPES_AND_OPTIONS" &&
            dev4History.First(x => x.Version == 47).Name == "RESTAURANT_SERVICE_MODE",
            "DEV5 C setup: the database carries the DEV4 history 42-47 without C-4");

        var hashBefore = DatabaseFileHash(db);
        var (_, dev5, backups) = Open(dir, "dev4-restaurant");
        var message = await RefusalAsync(() => dev5.InitializeDatabaseAsync());
        var hashAfter = DatabaseFileHash(db);

        assert(
            message.StartsWith(SchemaMigrationService.Dev4DatabaseRefusedMessage, StringComparison.Ordinal) &&
            message.Contains("Migration 42", StringComparison.Ordinal) &&
            message.Contains("RESTAURANT_RECIPES_AND_OPTIONS", StringComparison.Ordinal) &&
            message.Contains("C4_CLOUD_OUTBOX_REJECTED", StringComparison.Ordinal),
            "DEV5 C: a DEV4 Restaurant test database is refused with the German DEV4 message naming migration 42 and both names");

        await using (var c = db.OpenConnection())
        {
            assert(
                await VersionAsync(db) == 47 &&
                (await HistoryAsync(db)).Count == 47 &&
                !(await HistoryAsync(db)).Any(x => x.Version == 48) &&
                !TableExists(c, "cloud_outbox_rejected") &&
                CountFiles(backups) == 0,
                "DEV5 C: the refused DEV4 database is left untouched - migration 48 did not run, it is not counted current and no backup was written");
        }

        assert(
            hashBefore.Length > 0 && hashAfter == hashBefore,
            $"DEV5 C: the refused DEV4 database file is byte-for-byte unchanged (SHA-256 {hashBefore[..Math.Min(12, hashBefore.Length)]}…, database and WAL)");

        await using (var c = db.OpenConnection())
        {
            assert(
                !TableExists(c, "category_visual_data"),
                "DEV5 C: the history check runs before the legacy bootstrap - a table the bootstrap would recreate is still missing after the refusal");
        }

        var secondMessage = await RefusalAsync(() => dev5.InitializeDatabaseAsync());
        assert(
            secondMessage == message && await VersionAsync(db) == 47,
            "DEV5 C: the DEV4 database stays refused on every start, it never becomes current by a retry");
    }

    // D) right version, wrong name -> fail-closed.
    private static async Task WrongNameRefused(string dir, Action<bool, string> assert)
    {
        var (db, fresh, _) = Open(dir, "wrong-name");
        await fresh.InitializeDatabaseAsync();
        var expected30 = SchemaMigrationService.DefinedMigrations[29].Name;

        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "UPDATE schema_migrations SET name='OTHER_BRANCH_MIGRATION' WHERE version=30;";
            await q.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        var (_, dev5, backups) = Open(dir, "wrong-name");
        var message = await RefusalAsync(() => dev5.InitializeDatabaseAsync());
        assert(
            message.Contains("Migration 30", StringComparison.Ordinal) &&
            message.Contains("\"OTHER_BRANCH_MIGRATION\"", StringComparison.Ordinal) &&
            message.Contains($"\"{expected30}\"", StringComparison.Ordinal) &&
            !message.Contains("DEV4", StringComparison.Ordinal) &&
            CountFiles(backups) == 0,
            "DEV5 D: a history row with the right version but another migration's name is refused in German, naming version and both names");

        // The same check guards a database that is behind: nothing is migrated.
        var (behindDb, behind, _) = Open(dir, "wrong-name-behind");
        await behind.InitializeDatabaseUpToAsync(42);
        await using (var c = behindDb.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "UPDATE schema_migrations SET name='SOMETHING_ELSE' WHERE version=42;";
            await q.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        var (_, dev5Behind, behindBackups) = Open(dir, "wrong-name-behind");
        var behindMessage = await RefusalAsync(() => dev5Behind.InitializeDatabaseAsync());
        assert(
            behindMessage.Contains("Migration 42", StringComparison.Ordinal) &&
            behindMessage.Contains("\"SOMETHING_ELSE\"", StringComparison.Ordinal) &&
            await VersionAsync(behindDb) == 42 &&
            CountFiles(behindBackups) == 0,
            "DEV5 D: a mismatching name stops an outstanding upgrade before backup or migration 43");
    }

    private static async Task ReservationLifecycle(string dir, Action<bool, string> assert)
    {
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "reservation.db"));
        var repo = new RestaurantRepository(db);
        var areaId = await repo.SaveAreaAsync("DEV5", 1);
        var tableId = await repo.SaveTableAsync(areaId, "DEV5-1", "DEV5 Tisch", 2, 1);
        var session = await repo.OpenTableAsync(tableId, "DEV5-TEST", 1, "KASSE-DEV5");
        var product = new Product
        {
            Id = 950001,
            Name = "DEV5 Artikel",
            BasePriceCents = 1250,
            VatRate = 19m,
            Unit = "Stück",
            IsActive = true
        };
        var item = await repo.AddItemAsync(session.Id, session.Version, product, 2m, "DEV5-TEST", "KASSE-DEV5");

        async Task<RestaurantCheckoutDraft> DraftAsync()
        {
            var current = await repo.GetSessionAsync(session.Id)
                ?? throw new InvalidOperationException("DEV5 session missing.");
            return await repo.BuildCheckoutDraftAsync(
                current.Id,
                current.Version,
                new[] { new RestaurantSplitSelection(item.Id, 1000) });
        }

        // PREPARED -> cancelled (no charge) -> table open again.
        var first = await DraftAsync();
        await repo.PreparePaymentReservationAsync(first);
        // A second start for the same table - from the stale draft or a fresh one.
        var staleRefused = "";
        try { await repo.PreparePaymentReservationAsync(first with { OperationId = Guid.NewGuid().ToString("N") }); }
        catch (InvalidOperationException ex) { staleRefused = ex.Message; }
        var freshRefused = "";
        try { await repo.PreparePaymentReservationAsync(await DraftAsync()); }
        catch (InvalidOperationException ex) { freshRefused = ex.Message; }
        assert(
            staleRefused.Length > 0 && freshRefused.Length > 0 &&
            await repo.HasPreparedPaymentReservationAsync(first.OperationId) &&
            (await repo.ListPreparedPaymentReservationOperationIdsAsync()).Count(x => x == first.OperationId) == 1 &&
            (await repo.GetSessionAsync(session.Id))?.State == RestaurantTableSessionState.CheckRequested,
            "DEV5 checkout: while one payment for the table is prepared, a second payment cannot be started and the table stays locked");

        await repo.CancelPaymentReservationAsync(first.OperationId);
        await repo.CancelPaymentReservationAsync(first.OperationId);
        var reopened = await repo.GetSessionAsync(session.Id);
        assert(
            reopened?.State == RestaurantTableSessionState.Open &&
            await ReservationStateAsync(db, first.OperationId) == "CANCELLED" &&
            !await repo.HasPreparedPaymentReservationAsync(first.OperationId),
            "DEV5 checkout: a PREPARED reservation is cleaned up once, a repeated cleanup is a no-op, and the table is open again");

        // PREPARED -> applied by the committed sale -> cleanup must not undo it.
        var paid = await DraftAsync();
        await repo.PreparePaymentReservationAsync(paid);
        await using (var c = db.OpenConnection())
        {
            await using (var fk = c.CreateCommand())
            {
                // The reservation and table state are under test, not the sale row.
                fk.CommandText = "PRAGMA foreign_keys=OFF;";
                await fk.ExecuteNonQueryAsync();
            }
            await using var tx = c.BeginTransaction();
            var snapshot = new CheckoutSnapshot(
                paid.OperationId,
                CheckoutSnapshot.CopyLines(paid.Lines, true),
                0,
                PaymentMethod.Cash,
                "DEV5-TEST",
                null,
                paid.ImHaus);
            await RestaurantPaymentStore.ApplyCommittedSaleAsync(c, tx, snapshot, 950001, CancellationToken.None);
            await tx.CommitAsync();
        }
        SqliteConnection.ClearAllPools();

        var paidCentsBefore = await PaidCentsAsync(db, item.Id);
        await repo.CancelPaymentReservationAsync(paid.OperationId);
        assert(
            await ReservationStateAsync(db, paid.OperationId) == "APPLIED" &&
            await PaidCentsAsync(db, item.Id) == paidCentsBefore &&
            paidCentsBefore > 0 &&
            !(await repo.ListPreparedPaymentReservationOperationIdsAsync()).Contains(paid.OperationId, StringComparer.Ordinal),
            "DEV5 checkout: an APPLIED reservation is never cancelled by a later cleanup - the paid part stays paid and is not offered for payment again");
    }

    private static void CheckoutWiring(Action<bool, string> assert)
    {
        var main = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"));

        string Body(string signature)
        {
            var start = main.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) return "";
            var next = main.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
            return next < 0 ? main[start..] : main[start..next];
        }

        var handle = Body("private async Task HandleRestaurantCheckoutAsync(");
        var open = Body("private async Task OpenPaymentWindowAsync(");
        var discard = Body("private void DiscardRestaurantCheckoutDraft(");
        var checkout = Body("private async Task CheckoutAsync(");

        var cancelDiscard = open.IndexOf("DiscardRestaurantCheckoutDraft(\"TISCH-ZAHLUNG ABGEBROCHEN", StringComparison.Ordinal);
        var shortDiscard = open.IndexOf("DiscardRestaurantCheckoutDraft(\"BARZAHLUNG: Gegebener Betrag ist kleiner", StringComparison.Ordinal);
        var toCheckout = open.IndexOf("await CheckoutAsync(", StringComparison.Ordinal);
        assert(
            cancelDiscard > 0 && shortDiscard > cancelDiscard && toCheckout > shortDiscard &&
            discard.Contains("_restaurantCheckoutDraft = null;", StringComparison.Ordinal) &&
            !discard.Contains("PaymentReservation", StringComparison.Ordinal) &&
            !open.Contains("PreparePaymentReservationAsync", StringComparison.Ordinal),
            "DEV5 checkout: payment-dialog cancel and short cash drop the draft before any reservation exists, so the table stays open and nothing is cancelled twice");

        var finallyAt = handle.IndexOf("finally", StringComparison.Ordinal);
        var guard = handle.IndexOf("_pendingCheckout is null &&", finallyAt < 0 ? 0 : finallyAt, StringComparison.Ordinal);
        var cancel = handle.IndexOf("CancelPaymentReservationAsync(", finallyAt < 0 ? 0 : finallyAt, StringComparison.Ordinal);
        assert(
            finallyAt > 0 && guard > finallyAt && cancel > guard &&
            handle.Contains("sameDraft &&", StringComparison.Ordinal) &&
            handle.Contains("!_recoveryFault", StringComparison.Ordinal) &&
            handle.Contains("HasPreparedPaymentReservationAsync(", StringComparison.Ordinal) &&
            handle.Contains("Tisch bleibt gesperrt", StringComparison.Ordinal),
            "DEV5 checkout: the table cleanup after the payment window only cancels a still-PREPARED reservation of the same draft with no open checkout, and keeps the table locked when cleanup fails");

        var unresolved = checkout.IndexOf("Disposition==CheckoutApplicationDisposition.Unresolved", StringComparison.Ordinal);
        var unresolvedEnd = unresolved < 0 ? -1 : checkout.IndexOf("return;", unresolved, StringComparison.Ordinal);
        var unresolvedBlock = unresolved < 0 || unresolvedEnd < 0 ? "" : checkout[unresolved..unresolvedEnd];
        var pendingSet = checkout.IndexOf("_pendingCheckout=operation;", StringComparison.Ordinal);
        assert(
            unresolvedBlock.Length > 0 && pendingSet > 0 && pendingSet < unresolved &&
            !unresolvedBlock.Contains("CancelPaymentReservationAsync", StringComparison.Ordinal) &&
            !unresolvedBlock.Contains("_restaurantCheckoutDraft = null", StringComparison.Ordinal) &&
            unresolvedBlock.Contains("Nicht erneut kassieren", StringComparison.Ordinal),
            "DEV5 checkout: an unclear terminal/TSE result keeps the open checkout and the reservation, so the table stays locked and is not charged again");

        var commit = checkout.IndexOf("await CommitCheckoutAsync(operation.Snapshot,cash);", StringComparison.Ordinal);
        var state = main.IndexOf("ReceiptFiscalStates.Of(sale.TseOutage, sale.TseTransactionNumber, sale.TseSignature)", StringComparison.Ordinal);
        var withheld = main.IndexOf("await WithholdReceiptNotFiscalAsync(sale);", state < 0 ? 0 : state, StringComparison.Ordinal);
        var saleReceipts = CountOf(main, "BuildReceiptPrintJob(sale,");
        var saleReceiptsAfterPolicy = state < 0 ? 0 : CountOf(main[state..], "BuildReceiptPrintJob(sale,");
        var restaurantPrintsReceipt = Directory
            .GetFiles(Path.GetDirectoryName(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"))!, "Restaurant*.cs")
            .Any(path => File.ReadAllText(path).Contains("PrintReceiptAsync(", StringComparison.Ordinal));
        assert(
            commit > 0 && state > 0 && withheld > state &&
            main.Contains("? null", StringComparison.Ordinal) &&
            main.Contains("fiscalState == ReceiptFiscalState.NotCompleted", StringComparison.Ordinal) &&
            saleReceipts > 0 && saleReceipts == saleReceiptsAfterPolicy &&
            !restaurantPrintsReceipt,
            "DEV5 checkout: a Restaurant payment commits through the shared checkout, whose only sale receipts come after the receipt policy - no normal receipt without a TSE result or documented outage, and no Restaurant window prints its own sale receipt");
    }

    private static (SqliteDatabase Db, SchemaMigrationService Migrator, string Backups) Open(string dir, string name)
    {
        var db = new SqliteDatabase(Path.Combine(dir, name + ".db"));
        var backups = Path.Combine(dir, name + "-backups");
        return (db, new SchemaMigrationService(db, new DatabaseBackupService(db), backups), backups);
    }

    private static async Task<string> RefusalAsync(Func<Task> action)
    {
        try
        {
            await action();
            return "";
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static async Task<List<(int Version, string Name)>> HistoryAsync(SqliteDatabase db)
    {
        var rows = new List<(int, string)>();
        await using var c = db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = "SELECT version,name FROM schema_migrations ORDER BY version;";
        await using var r = await q.ExecuteReaderAsync();
        while (await r.ReadAsync())
            rows.Add((r.GetInt32(0), r.GetString(1)));
        return rows;
    }

    private static async Task<int> VersionAsync(SqliteDatabase db)
    {
        await using var c = db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = "SELECT version FROM schema_version WHERE singleton_id=1;";
        return Convert.ToInt32(await q.ExecuteScalarAsync());
    }

    private static async Task MarkRejectedCloudEventAsync(SqliteDatabase db)
    {
        await using var c = db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            INSERT INTO cloud_outbox_rejected(event_id,event_type,occurred_at,payload,target_url,device_code,rejected_at,verdict,reason)
            VALUES('dev5-parked','sale.created','2026-09-26T00:00:00Z','{}','https://cloud.invalid','KASSE-1','2026-09-26T00:00:01Z','REJECTED','DEV5 marker');
            """;
        await q.ExecuteNonQueryAsync();
    }

    private static async Task<bool> RejectedCloudEventKeptAsync(SqliteConnection c)
    {
        await using var q = c.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM cloud_outbox_rejected WHERE event_id='dev5-parked' AND reason='DEV5 marker';";
        return Convert.ToInt32(await q.ExecuteScalarAsync()) == 1;
    }

    private static async Task<string> ReservationStateAsync(SqliteDatabase db, string operationId)
    {
        await using var c = db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = "SELECT state FROM restaurant_payment_reservations WHERE operation_id=$op;";
        q.Parameters.AddWithValue("$op", operationId);
        return Convert.ToString(await q.ExecuteScalarAsync()) ?? "";
    }

    private static async Task<long> PaidCentsAsync(SqliteDatabase db, long itemId)
    {
        await using var c = db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = "SELECT paid_cents FROM restaurant_session_items WHERE id=$id;";
        q.Parameters.AddWithValue("$id", itemId);
        return Convert.ToInt64(await q.ExecuteScalarAsync());
    }

    private static bool TableExists(SqliteConnection c, string name) =>
        SchemaObjectExists(c, "table", name);

    private static bool IndexExists(SqliteConnection c, string name) =>
        SchemaObjectExists(c, "index", name);

    private static bool SchemaObjectExists(SqliteConnection c, string type, string name)
    {
        using var q = c.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type=$type AND name=$name;";
        q.Parameters.AddWithValue("$type", type);
        q.Parameters.AddWithValue("$name", name);
        return Convert.ToInt32(q.ExecuteScalar()) == 1;
    }

    private static bool ColumnExists(SqliteConnection c, string table, string column)
    {
        using var q = c.CreateCommand();
        q.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$column;";
        q.Parameters.AddWithValue("$column", column);
        return Convert.ToInt32(q.ExecuteScalar()) == 1;
    }

    // SHA-256 over the database file and its WAL (the WAL holds committed pages
    // until a checkpoint); -shm is a reader index, not data.
    private static string DatabaseFileHash(SqliteDatabase db)
    {
        SqliteConnection.ClearAllPools();
        using var sha = System.Security.Cryptography.SHA256.Create();
        foreach (var path in new[] { db.DatabasePath, db.DatabasePath + "-wal" })
        {
            var bytes = File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    private static int CountFiles(string directory) =>
        Directory.Exists(directory) ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length : 0;

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string FindRepoFile(string relativePath)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relativePath);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        throw new FileNotFoundException(relativePath);
    }
}
