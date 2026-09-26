using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

// Restaurant order snapshots and the kitchen queue:
// - an ordered position keeps its customer option text and its price when the
//   Stammdaten change afterwards;
// - BESTELLUNG SENDEN only wakes the dispatcher: kitchen jobs are created once,
//   durably, when a position is added, so pressing it again cannot send a
//   position twice, and after a hand-over only the new positions are pending.
public static class RestaurantDev5SnapshotKitchenTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var previousEdition = Environment.GetEnvironmentVariable("TOR_POS_PRODUCT_EDITION");
        try
        {
            Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", "RESTAURANT", EnvironmentVariableTarget.Process);
            var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(root, "restaurant-dev5-snapshot-" + Guid.NewGuid().ToString("N") + ".db"));
            var repo = new RestaurantRepository(db);
            var recipes = new RestaurantRecipeRepository(db);
            var kitchen = new RestaurantKitchenOutbox(db);
            var areaId = await repo.SaveAreaAsync("DEV5 Küche", 1);
            var tableId = await repo.SaveTableAsync(areaId, "K1", "Küche 1", 4, 1);
            var session = await repo.OpenTableAsync(tableId, "DEV5-TEST", 2, "KASSE-DEV5");

            // ---------------------------------------------------- snapshots
            await recipes.SaveOptionAsync(new RestaurantOrderOption(0, "ohne Zwiebeln", true));
            var option = (await recipes.ListOptionsAsync()).Single(o => o.Name == "ohne Zwiebeln");
            var product = new Product { Id = 970001, Name = "Döner Teller", BasePriceCents = 1290, VatRate = 7m, Unit = "Stück", IsActive = true };
            var ordered = (await repo.AddItemWithLineTokenAsync(session.Id, session.Version, product, 1m, "DEV5-TEST", "dev5-snapshot-line", orderOptions: "ohne Zwiebeln")).Item;

            await recipes.SaveOptionAsync(option with { Name = "ohne rote Zwiebeln", IsActive = false });
            product.BasePriceCents = 1590;
            product.Name = "Döner Teller groß";
            var reloaded = (await repo.ListActiveItemsAsync(session.Id)).Single(i => i.Id == ordered.Id);
            string storedOptions;
            await using (var c = db.OpenConnection())
            await using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT order_options FROM restaurant_session_items WHERE id=$id;";
                q.Parameters.AddWithValue("$id", ordered.Id);
                storedOptions = Convert.ToString(await q.ExecuteScalarAsync()) ?? "";
            }

            assert(
                storedOptions == "ohne Zwiebeln" &&
                reloaded.UnitPriceCents == 1290 && reloaded.ProductName == "Döner Teller (ohne Zwiebeln)" &&
                reloaded.LineTotalCents == 1290,
                "DEV5 snapshot: an ordered position keeps its option text, name and price when the option is renamed/deactivated and the article price and name change afterwards");

            // ---------------------------------------------------- kitchen queue
            var current = await repo.GetSessionAsync(session.Id) ?? throw new InvalidOperationException("session missing");
            var jobOne = Guid.NewGuid().ToString("N");
            var first = await kitchen.EnqueueNewItemIdempotentAsync(current, reloaded, "Küche 1", "DEV5-TEST", jobOne);
            var retry = await kitchen.EnqueueNewItemIdempotentAsync(current, reloaded, "Küche 1", "DEV5-TEST", jobOne);
            var pendingForTable = (await kitchen.PendingAsync()).Where(j => j.SessionId == session.Id).ToList();
            assert(
                first == retry && pendingForTable.Count == 1,
                "DEV5 kitchen: the same position queued twice (retry, second BESTELLUNG SENDEN) is one kitchen job");

            await kitchen.MarkHandedOverAsync(first);
            var afterHandOver = (await kitchen.PendingAsync()).Where(j => j.SessionId == session.Id).ToList();
            current = await repo.GetSessionAsync(session.Id) ?? throw new InvalidOperationException("session missing");
            var drink = new Product { Id = 970002, Name = "Ayran", BasePriceCents = 290, VatRate = 7m, Unit = "Stück", IsActive = true };
            var added = await repo.AddItemAsync(current.Id, current.Version, drink, 1m, "DEV5-TEST", "KASSE-DEV5");
            current = await repo.GetSessionAsync(session.Id) ?? throw new InvalidOperationException("session missing");
            await kitchen.EnqueueNewItemIdempotentAsync(current, added, "Küche 1", "DEV5-TEST", Guid.NewGuid().ToString("N"));
            var resend = (await kitchen.PendingAsync()).Where(j => j.SessionId == session.Id).ToList();
            assert(
                afterHandOver.Count == 0 &&
                resend.Count == 1 && resend[0].SessionItemId == added.Id,
                "DEV5 kitchen: after the first order was handed over, sending again queues only the new position, not the one already in the kitchen");

            var workspace = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/RestaurantWorkspaceControl.cs"));
            var start = workspace.IndexOf("private async Task SendSelectedOrderAsync()", StringComparison.Ordinal);
            var end = start < 0 ? -1 : workspace.IndexOf("\n    private ", start + 10, StringComparison.Ordinal);
            var send = start < 0 ? "" : end < 0 ? workspace[start..] : workspace[start..end];
            assert(
                send.Contains("_kitchen.PendingAsync()", StringComparison.Ordinal) &&
                send.Contains("_kitchenDispatcher.Notify()", StringComparison.Ordinal) &&
                !send.Contains("Enqueue", StringComparison.Ordinal) &&
                send.Contains("_sendingOrder", StringComparison.Ordinal),
                "DEV5 kitchen: BESTELLUNG SENDEN only wakes the dispatcher for jobs already queued, creates none itself and ignores a double click while sending");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", previousEdition, EnvironmentVariableTarget.Process);
            SqliteConnection.ClearAllPools();
        }
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
