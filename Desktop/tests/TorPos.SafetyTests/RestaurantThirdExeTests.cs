using TorPos.Core;
using TorPos.Infrastructure;

internal static class RestaurantThirdExeTests
{
    public static async Task Run(Action<bool,string> assert)
    {
        var desktopRoot = Directory.GetCurrentDirectory();
        var workspacePath = Path.Combine(
            desktopRoot,
            "src",
            "TorPos.App",
            "RestaurantWorkspaceControl.cs");
        assert(
            File.Exists(workspacePath),
            "Restaurant main flow has reusable RestaurantWorkspaceControl");

        if (File.Exists(workspacePath))
        {
            var workspaceSource = await File.ReadAllTextAsync(workspacePath);
            assert(
                workspaceSource.Contains("public sealed class RestaurantWorkspaceControl", StringComparison.Ordinal) &&
                workspaceSource.Contains("Task InitializeAsync()", StringComparison.Ordinal) &&
                workspaceSource.Contains("Task RefreshAsync()", StringComparison.Ordinal) &&
                workspaceSource.Contains("bool IsBusy", StringComparison.Ordinal) &&
                workspaceSource.Contains("CheckoutRequestedAsync", StringComparison.Ordinal) &&
                workspaceSource.Contains("CounterRequestedAsync", StringComparison.Ordinal) &&
                workspaceSource.Contains("OpenMasterDataAsync", StringComparison.Ordinal),
                "Restaurant workspace exposes the embedded-main-flow contract");
        }

        var oldEdition = Environment.GetEnvironmentVariable("TOR_POS_PRODUCT_EDITION");
        Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", "RESTAURANT");
        try
        {
        var workspaceType = typeof(TorPos.App.MainWindow).Assembly
            .GetType("TorPos.App.RestaurantWorkspaceControl");
        assert(workspaceType is not null,
            "Restaurant edition exposes an embedded RestaurantWorkspaceControl");
        if (workspaceType is not null)
        {
            assert(workspaceType.GetMethod("InitializeAsync") is not null,
                "Restaurant workspace exposes InitializeAsync");
            assert(workspaceType.GetMethod("RefreshAsync") is not null,
                "Restaurant workspace exposes RefreshAsync");
            assert(workspaceType.GetProperty("IsBusy") is not null,
                "Restaurant workspace exposes busy state");
            assert(workspaceType.GetProperty("CheckoutRequestedAsync") is not null,
                "Restaurant workspace exposes checkout callback");
            assert(workspaceType.GetProperty("CounterRequestedAsync") is not null,
                "Restaurant workspace exposes counter-mode callback");
            assert(workspaceType.GetProperty("OpenMasterDataAsync") is not null,
                "Restaurant workspace exposes master-data callback");
        }

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
