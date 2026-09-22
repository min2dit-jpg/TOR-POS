using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

internal static class RestaurantFoundationTests
{
    public static async Task Run(Action<bool,string> assert)
    {
        assert(
            RestaurantProductFeatures.Includes(
                RestaurantProductTier.Restaurant,
                RestaurantFeature.Tischplan),
            "Restaurant includes the essential Tischplan");

        assert(
            RestaurantProductFeatures.Includes(
                RestaurantProductTier.Restaurant,
                RestaurantFeature.SplitrechnungNachArtikel),
            "Restaurant includes essential split billing");

        assert(
            !RestaurantProductFeatures.Includes(
                RestaurantProductTier.Restaurant,
                RestaurantFeature.KitchenDisplaySystem),
            "Restaurant standard does not silently unlock Plus KDS");

        assert(
            RestaurantProductFeatures.Includes(
                RestaurantProductTier.RestaurantPlus,
                RestaurantFeature.KitchenDisplaySystem) &&
            RestaurantProductFeatures.Includes(
                RestaurantProductTier.RestaurantPlus,
                RestaurantFeature.Tischplan),
            "Restaurant Plus contains Plus modules and all standard essentials");

        var oldEdition = Environment.GetEnvironmentVariable("TOR_POS_PRODUCT_EDITION");
        var root = Path.Combine(
            Path.GetTempPath(),
            "tor-restaurant-foundation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            Environment.SetEnvironmentVariable(
                "TOR_POS_PRODUCT_EDITION",
                "RESTAURANT",
                EnvironmentVariableTarget.Process);

            var restaurantPath = Path.Combine(root, "restaurant.db");
            var db = new SqliteDatabase(restaurantPath);
            var backup = new DatabaseBackupService(db);
            var migration = new SchemaMigrationService(
                db,
                backup,
                Path.Combine(root, "migration-backups"));

            var result = await migration.InitializeDatabaseAsync();

            assert(
                result.ToVersion == SchemaMigrationService.TargetSchemaVersion &&
                result.ToVersion == 26,
                "Restaurant database reaches schema version 26");

            await using (var c = db.OpenConnection())
            {
                assert(
                    TableExists(c, "restaurant_tables") &&
                    TableExists(c, "restaurant_sessions") &&
                    TableExists(c, "restaurant_session_events"),
                    "Restaurant-only tables are created for the Restaurant product");
            }

            var repo = new RestaurantRepository(db);
            var areaId = await repo.SaveAreaAsync("Innenbereich");
            var tableId = await repo.SaveTableAsync(
                areaId,
                "T01",
                "Tisch 1",
                seats: 4);

            var session = await repo.OpenTableAsync(
                tableId,
                "KELLNER-1",
                guestCount: 3,
                deviceId: "KASSE-1");

            assert(
                session.TableId == tableId &&
                session.State == RestaurantTableSessionState.Open &&
                session.GuestCount == 3 &&
                session.Version == 1,
                "Opening a table creates one versioned live Tischvorgang");

            var duplicateRejected = false;
            try
            {
                await repo.OpenTableAsync(
                    tableId,
                    "KELLNER-2",
                    guestCount: 2);
            }
            catch (InvalidOperationException ex)
            {
                duplicateRejected = ex.Message.Contains(
                    "bereits geöffnet",
                    StringComparison.OrdinalIgnoreCase);
            }

            assert(
                duplicateRejected,
                "Second live Tischvorgang for the same table is rejected");

            var reassigned = await repo.ReassignWaiterAsync(
                session.Id,
                expectedVersion: 1,
                newWaiter: "KELLNER-2",
                actor: "ADMIN",
                deviceId: "KASSE-1");

            assert(
                reassigned.AssignedWaiter == "KELLNER-2" &&
                reassigned.Version == 2,
                "Kellner reassignment increments the optimistic concurrency version");

            var staleRejected = false;
            try
            {
                await repo.ReassignWaiterAsync(
                    session.Id,
                    expectedVersion: 1,
                    newWaiter: "KELLNER-3",
                    actor: "ADMIN");
            }
            catch (InvalidOperationException ex)
            {
                staleRejected = ex.Message.Contains(
                    "zwischenzeitlich geändert",
                    StringComparison.OrdinalIgnoreCase);
            }

            assert(
                staleRejected,
                "Stale table-session writes are rejected instead of overwriting newer data");

            var product = new Product
            {
                Id = 900001,
                Name = "Restaurant Testartikel",
                BasePriceCents = 1290,
                VatRate = 19m
            };

            var item = await repo.AddItemAsync(
                session.Id,
                expectedSessionVersion: reassigned.Version,
                product,
                quantity: 2m,
                operatorName: "KELLNER-2",
                deviceId: "HANDHELD-TEST");

            var afterItem = await repo.GetSessionAsync(session.Id);
            assert(
                item.QuantityMilli == 2000 &&
                item.LineTotalCents == 2580 &&
                afterItem is not null &&
                afterItem.Version == reassigned.Version + 1,
                "Adding a table item is cent-exact and advances the session version");

            var staleItemRejected = false;
            try
            {
                await repo.AddItemAsync(
                    session.Id,
                    expectedSessionVersion: reassigned.Version,
                    product,
                    quantity: 1m,
                    operatorName: "KELLNER-2");
            }
            catch (InvalidOperationException ex)
            {
                staleItemRejected = ex.Message.Contains(
                    "zwischenzeitlich geändert",
                    StringComparison.OrdinalIgnoreCase);
            }

            assert(
                staleItemRejected,
                "Stale concurrent item add is rejected instead of duplicating a table position");

            var splitItems = await repo.ListActiveItemsAsync(session.Id);
            var splitQuote = RestaurantSplitCalculator.ByItems(
                splitItems,
                new[]
                {
                    new RestaurantSplitSelection(
                        splitItems.Single().Id,
                        1000)
                });

            assert(
                splitQuote.TotalCents == 1290 &&
                splitQuote.Lines.Single().QuantityMilli == 1000,
                "Item split selects a partial quantity with cent-exact amount");

            var equalShares = RestaurantSplitCalculator.EqualShares(1000, 3);
            assert(
                equalShares.SequenceEqual(new long[] { 334, 333, 333 }) &&
                equalShares.Sum() == 1000,
                "Equal-person split assigns remainder cents deterministically");

            var secondTableId = await repo.SaveTableAsync(
                areaId,
                "T02",
                "Tisch 2",
                seats: 4,
                sortOrder: 2);

            var moved = await repo.MoveSessionToTableAsync(
                session.Id,
                afterItem!.Version,
                secondTableId,
                "KELLNER-2",
                "KASSE-1");

            assert(
                moved.TableId == secondTableId &&
                moved.Version == afterItem.Version + 1 &&
                await repo.GetLiveSessionForTableAsync(tableId) is null,
                "Tisch umbuchen frees the source table and preserves the live session");

            var thirdTableId = await repo.SaveTableAsync(
                areaId,
                "T03",
                "Tisch 3",
                seats: 4,
                sortOrder: 3);

            var targetSession = await repo.OpenTableAsync(
                thirdTableId,
                "KELLNER-3",
                guestCount: 2,
                deviceId: "KASSE-1");

            var merged = await repo.MergeSessionsAsync(
                moved.Id,
                moved.Version,
                targetSession.Id,
                targetSession.Version,
                "ADMIN",
                "KASSE-1");

            var mergedItems = await repo.ListActiveItemsAsync(merged.Id);
            assert(
                merged.GuestCount == moved.GuestCount + targetSession.GuestCount &&
                mergedItems.Count == 1 &&
                mergedItems.Single().LineTotalCents == 2580 &&
                await repo.GetLiveSessionForTableAsync(secondTableId) is null,
                "Tische zusammenlegen moves open positions and releases the source table");

            var payableItems = await repo.ListActiveItemsAsync(merged.Id);
            var payable = payableItems.Single();
            var paymentDraft = await repo.BuildCheckoutDraftAsync(
                merged.Id,
                merged.Version,
                new[]
                {
                    new RestaurantSplitSelection(
                        payable.Id,
                        payable.QuantityMilli)
                });

            var lockedVersion = await repo.PreparePaymentReservationAsync(
                paymentDraft);

            var lockedSession = await repo.GetSessionAsync(merged.Id);
            assert(
                lockedSession is not null &&
                lockedSession.State == RestaurantTableSessionState.CheckRequested &&
                lockedSession.Version == lockedVersion &&
                await repo.HasPreparedPaymentReservationAsync(paymentDraft.OperationId),
                "Prepared Restaurant payment durably locks the table before external payment");

            var writeWhilePaymentRejected = false;
            try
            {
                await repo.AddItemAsync(
                    merged.Id,
                    lockedVersion,
                    product,
                    1m,
                    "KELLNER-3");
            }
            catch (InvalidOperationException)
            {
                writeWhilePaymentRejected = true;
            }

            assert(
                writeWhilePaymentRejected,
                "Table edits are rejected while a Restaurant payment is unresolved");

            await repo.CancelPaymentReservationAsync(
                paymentDraft.OperationId);

            var reopenedAfterCancel = await repo.GetSessionAsync(merged.Id);
            assert(
                reopenedAfterCancel is not null &&
                reopenedAfterCancel.State == RestaurantTableSessionState.Open &&
                !await repo.HasPreparedPaymentReservationAsync(paymentDraft.OperationId),
                "No-charge cancellation reopens the Restaurant table and clears the payment lock");

            var emptyTableId = await repo.SaveTableAsync(
                areaId,
                "T04",
                "Tisch 4",
                seats: 2,
                sortOrder: 4);
            var emptySession = await repo.OpenTableAsync(
                emptyTableId,
                "KELLNER-1",
                guestCount: 1);

            var closedEmpty = await repo.CloseEmptySessionAsync(
                emptySession.Id,
                emptySession.Version,
                "KELLNER-1");

            assert(
                closedEmpty.State == RestaurantTableSessionState.Closed &&
                closedEmpty.ClosedAt is not null &&
                await repo.GetLiveSessionForTableAsync(emptyTableId) is null,
                "An empty Tischvorgang can be closed and releases the table");

            var guardedCloseRejected = false;
            try
            {
                await repo.CloseEmptySessionAsync(
                    merged.Id,
                    merged.Version,
                    "KELLNER-3");
            }
            catch (InvalidOperationException ex)
            {
                guardedCloseRejected = ex.Message.Contains(
                    "Zahlungsweg",
                    StringComparison.OrdinalIgnoreCase);
            }

            assert(
                guardedCloseRejected,
                "A table with open positions cannot bypass checkout by closing directly");

            await using (var c = db.OpenConnection())
            {
                using var count = c.CreateCommand();
                count.CommandText =
                    "SELECT COUNT(*) FROM restaurant_session_events WHERE session_id=$id;";
                count.Parameters.AddWithValue("$id", session.Id);
                var eventCount = Convert.ToInt32(count.ExecuteScalar());

                var appendOnly = false;
                try
                {
                    using var tamper = c.CreateCommand();
                    tamper.CommandText =
                        "UPDATE restaurant_session_events SET actor='X' WHERE session_id=$id;";
                    tamper.Parameters.AddWithValue("$id", session.Id);
                    tamper.ExecuteNonQuery();
                }
                catch (SqliteException)
                {
                    appendOnly = true;
                }

                assert(
                    eventCount >= 3 && appendOnly,
                    "Restaurant session events record changes and are append-only");
            }

            Environment.SetEnvironmentVariable(
                "TOR_POS_PRODUCT_EDITION",
                "KIOSK",
                EnvironmentVariableTarget.Process);

            var kioskPath = Path.Combine(root, "kiosk.db");
            var kioskDb = new SqliteDatabase(kioskPath);
            var kioskMigration = new SchemaMigrationService(
                kioskDb,
                new DatabaseBackupService(kioskDb),
                Path.Combine(root, "kiosk-migration-backups"));
            await kioskMigration.InitializeDatabaseAsync();

            await using (var c = kioskDb.OpenConnection())
            {
                assert(
                    !TableExists(c, "restaurant_tables") &&
                    !TableExists(c, "restaurant_sessions"),
                    "Einzelhandel database does not receive Restaurant-only tables");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "TOR_POS_PRODUCT_EDITION",
                oldEdition,
                EnvironmentVariableTarget.Process);
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static bool TableExists(SqliteConnection c, string name)
    {
        using var q = c.CreateCommand();
        q.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
        q.Parameters.AddWithValue("$name", name);
        return Convert.ToInt32(q.ExecuteScalar()) == 1;
    }
}
