using TorPos.Core;
using TorPos.Infrastructure;

internal static class RestaurantThirdExeTests
{
    public static async Task Run(Action<bool,string> assert)
    {
        var oldEdition = Environment.GetEnvironmentVariable("TOR_POS_PRODUCT_EDITION");
        Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", "RESTAURANT");
        try
        {
        var path = Path.Combine(Path.GetTempPath(), "restaurant-third-" + Guid.NewGuid().ToString("N") + ".db");
        var db = await SafetyDatabase.CreateCurrentAsync(path);
        var repo = new RestaurantRepository(db);
        var area = await repo.SaveAreaAsync("Außenbereich");
        await repo.SaveTableAsync(area, "A1", "Außen 1", 4);
        using (var c = db.OpenConnection())
        using (var q = c.CreateCommand())
        {
            q.CommandText = "UPDATE restaurant_areas SET is_active=0;";
            await q.ExecuteNonQueryAsync();
        }
        assert((await repo.ListTablesAsync()).Count == 0,
            "Restaurant table plan hides tables in inactive areas");
        }
        finally { Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", oldEdition); }
    }
}
