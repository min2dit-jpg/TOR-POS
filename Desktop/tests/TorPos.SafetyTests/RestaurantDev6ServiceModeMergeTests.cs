using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

// DEV6: session-level service mode must not be silently rewritten when active
// positions are merged into another table with a different mode.
public static class RestaurantDev6ServiceModeMergeTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var previousEdition = Environment.GetEnvironmentVariable("TOR_POS_PRODUCT_EDITION");
        try
        {
            Environment.SetEnvironmentVariable(
                "TOR_POS_PRODUCT_EDITION",
                "RESTAURANT",
                EnvironmentVariableTarget.Process);

            var db = await SafetyDatabase.CreateCurrentAsync(
                Path.Combine(root, "restaurant-dev6-service-merge-" + Guid.NewGuid().ToString("N") + ".db"));
            var repo = new RestaurantRepository(db);
            var area = await repo.SaveAreaAsync("DEV6 Service", 1);
            var sourceTable = await repo.SaveTableAsync(area, "SVC-S", "Quelle", 4, 1);
            var targetTable = await repo.SaveTableAsync(area, "SVC-T", "Ziel", 4, 2);

            var source = await repo.OpenTableAsync(sourceTable, "DEV6", 2, deviceId: "KASSE-DEV6");
            source = await repo.UpdateServiceModeAsync(
                source.Id,
                source.Version,
                RestaurantServiceModes.Takeaway,
                "DEV6",
                "KASSE-DEV6");

            var product = new Product
            {
                Id = 970001,
                Name = "Service-Artikel",
                BasePriceCents = 900,
                VatRate = 7m,
                Unit = "Stück",
                IsActive = true
            };
            await repo.AddItemAsync(
                source.Id,
                source.Version,
                product,
                1m,
                "DEV6",
                "KASSE-DEV6");

            source = await repo.GetSessionAsync(source.Id)
                ?? throw new InvalidOperationException("source missing");
            var target = await repo.OpenTableAsync(targetTable, "DEV6", 1, deviceId: "KASSE-DEV6");

            var refused = false;
            try
            {
                await repo.MergeSessionsAsync(
                    source.Id,
                    source.Version,
                    target.Id,
                    target.Version,
                    "DEV6",
                    "KASSE-DEV6");
            }
            catch (InvalidOperationException ex)
            {
                refused = ex.Message.Contains(
                    "Servicemodus",
                    StringComparison.OrdinalIgnoreCase);
            }

            var sourceAfter = await repo.GetSessionAsync(source.Id);
            var targetAfter = await repo.GetSessionAsync(target.Id);
            var sourceItems = await repo.ListActiveItemsAsync(source.Id);
            var targetItems = await repo.ListActiveItemsAsync(target.Id);

            assert(
                refused &&
                sourceAfter?.State == RestaurantTableSessionState.Open &&
                targetAfter?.State == RestaurantTableSessionState.Open &&
                sourceItems.Count == 1 &&
                sourceItems[0].Id > 0 &&
                targetItems.Count == 0 &&
                await repo.GetServiceModeAsync(source.Id) == RestaurantServiceModes.Takeaway &&
                await repo.GetServiceModeAsync(target.Id) == RestaurantServiceModes.InHouse,
                "DEV6 merge refuses active positions across different Restaurant service modes and leaves both tables unchanged");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "TOR_POS_PRODUCT_EDITION",
                previousEdition,
                EnvironmentVariableTarget.Process);
            SqliteConnection.ClearAllPools();
        }
    }
}
