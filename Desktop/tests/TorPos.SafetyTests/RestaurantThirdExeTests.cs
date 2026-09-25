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
        await repo.SaveAreaSettingsAsync(area,"Außenbereich",true);
        var table=(await repo.ListTablesAsync()).Single();
        var session=await repo.OpenTableAsync(table.Id,"Kellner");
        var refused=false;
        try { await repo.SaveAreaSettingsAsync(area,"Außenbereich",false); }
        catch(InvalidOperationException){refused=true;}
        assert(refused,"Area settings cannot hide an open table");
        using(var c=db.OpenConnection())using(var q=c.CreateCommand())
        {q.CommandText="UPDATE restaurant_areas SET is_active=0;";await q.ExecuteNonQueryAsync();}
        assert((await repo.ListTablesAsync()).Single().Id==table.Id,
            "Existing order remains accessible even if its area was disabled externally");
        await repo.CloseEmptySessionAsync(session.Id,session.Version,"Kellner");
        await repo.SaveAreaSettingsAsync(area,"Gastraum",true);
        await repo.SaveTableSettingsAsync(table with { DisplayName="Fenster 1",Seats=6 });
        var reloaded=(await new RestaurantRepository(db).ListAllTablesAsync()).Single();
        assert(reloaded.DisplayName=="Fenster 1" && reloaded.Seats==6 && reloaded.Version==table.Version+1,
            "Restaurant table settings persist and increment their concurrency version");

        }
        finally { Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", oldEdition); }
    }
}
