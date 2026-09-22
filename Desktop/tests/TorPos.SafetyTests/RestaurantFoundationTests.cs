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
                result.ToVersion == 25,
                "Restaurant database reaches schema version 25");

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
                    eventCount == 2 && appendOnly,
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
