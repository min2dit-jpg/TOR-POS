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

        var desktopRoot = Directory.GetCurrentDirectory();
        var mainWindowSource = await File.ReadAllTextAsync(
            Path.Combine(desktopRoot, "src", "TorPos.App", "MainWindow.axaml.cs"));
        var mainWindowXaml = await File.ReadAllTextAsync(
            Path.Combine(desktopRoot, "src", "TorPos.App", "MainWindow.axaml"));

        assert(
            !mainWindowSource.Contains(
                "ShowDialog<RestaurantCheckoutDraft?>",
                StringComparison.Ordinal),
            "Restaurant startup no longer opens Tischplan as a modal checkout dialog");

        assert(
            mainWindowSource.Contains(
                "ShowRestaurantTableWorkspaceAsync",
                StringComparison.Ordinal) &&
            mainWindowSource.Contains(
                "CreateRestaurantWorkspaceControl",
                StringComparison.Ordinal),
            "MainWindow owns the embedded Restaurant workspace lifecycle");

        assert(
            mainWindowSource.Contains(
                "RecoverOrphanedRestaurantPaymentReservationsAsync",
                StringComparison.Ordinal) &&
            mainWindowSource.Contains(
                "ReleaseRestaurantPaymentReservationIfSafeAsync",
                StringComparison.Ordinal) &&
            mainWindowSource.Contains(
                "\"NOT_CHARGED\"",
                StringComparison.Ordinal) &&
            mainWindowSource.Contains(
                "_pendingCheckout = checkout",
                StringComparison.Ordinal),
            "Restaurant payment recovery only auto-releases proven no-charge reservations and preserves ambiguous payments for reconciliation");

        assert(
            mainWindowXaml.Contains(
                "x:Name=\"RestaurantWorkspaceHost\"",
                StringComparison.Ordinal),
            "MainWindow contains a RestaurantWorkspaceHost");

        assert(
            mainWindowXaml.Contains(
                "x:Name=\"RestaurantCounterButton\"",
                StringComparison.Ordinal) &&
            mainWindowXaml.Contains(
                "Content=\"THEKE\"",
                StringComparison.Ordinal),
            "Restaurant header exposes THEKE inside the same MainWindow");

        var workspaceSource = await File.ReadAllTextAsync(
            Path.Combine(
                desktopRoot,
                "src",
                "TorPos.App",
                "RestaurantWorkspaceControl.cs"));

        assert(
            typeof(RestaurantFiscalOrderService).GetMethod(
                "ReconcileSessionAsync") is not null &&
            typeof(TseVorgangService).GetMethod(
                "TagReferenceAsync") is not null,
            "Restaurant fiscal recovery exposes a session reconciliation path and tags TSE changes before DB mutation");

        assert(
            workspaceSource.Contains(
                "RestaurantFiscalReconcile",
                StringComparison.Ordinal) &&
            workspaceSource.Contains(
                "ListUnsecuredSessionIdsAsync",
                StringComparison.Ordinal) &&
            workspaceSource.Contains(
                "BESTELLUNG/TSE · PRÜFUNG ERFORDERLICH",
                StringComparison.Ordinal) &&
            workspaceSource.Contains(
                "ReconcileFiscalIssuesAsync",
                StringComparison.Ordinal) &&
            workspaceSource.Contains(
                "_fiscalRecoveryOutstanding",
                StringComparison.Ordinal),
            "Restaurant workspace automatically retries all unsecured sessions and exposes a global admin fiscal-reconcile action");

        var zRestaurantRecovery =
            mainWindowSource.IndexOf(
                "ReconcileRestaurantFiscalBeforeClosingAsync",
                StringComparison.Ordinal);
        var zOrphanCleanup =
            mainWindowSource.IndexOf(
                "AbortOrphansAsync(null",
                StringComparison.Ordinal);
        assert(
            zRestaurantRecovery >= 0 &&
            zOrphanCleanup > zRestaurantRecovery,
            "Z-Bericht reconciles Restaurant fiscal/payment locks before generic TSE orphan cleanup");

        assert(
            !workspaceSource.Contains(
                "TISCH ÖFFNEN",
                StringComparison.Ordinal),
            "Restaurant table selection is one tap with no mandatory TISCH ÖFFNEN step");

        assert(
            workspaceSource.Contains(
                "SelectOrOpenTableAsync",
                StringComparison.Ordinal),
            "Restaurant table tiles route through one select-or-open workflow");

        assert(
            new[]
            {
                "RestaurantSendOrder",
                "InterimBill",
                "RestaurantMove",
                "RestaurantSplit",
                "TablePayAll"
            }.All(name =>
                workspaceSource.Contains(
                    $"Name=\"{name}\"",
                    StringComparison.Ordinal) ||
                workspaceSource.Contains(
                    $"Name = \"{name}\"",
                    StringComparison.Ordinal)),
            "Restaurant order workspace exposes the five fixed primary actions");

        assert(
            workspaceSource.Contains(
                "SendSelectedOrderAsync",
                StringComparison.Ordinal) &&
            workspaceSource.Contains(
                "_kitchen.PendingAsync()",
                StringComparison.Ordinal) &&
            workspaceSource.Contains(
                "_kitchenDispatcher.Notify()",
                StringComparison.Ordinal),
            "BESTELLUNG SENDEN flushes existing kitchen outbox work without a second order model");

        assert(
            workspaceSource.Contains(
                "RestaurantTableSession? liveSession",
                StringComparison.Ordinal) &&
            workspaceSource.Contains(
                "bereits geöffnet",
                StringComparison.Ordinal) &&
            workspaceSource.Contains(
                "GetLiveSessionForTableAsync(table.Id)",
                StringComparison.Ordinal),
            "one-tap table opening reloads the existing live session when a concurrent open wins");

        assert(
            workspaceSource.Contains(
                "Bestellung gesendet",
                StringComparison.Ordinal) &&
            workspaceSource.Contains(
                "Bestellung bereits gesendet",
                StringComparison.Ordinal),
            "BESTELLUNG SENDEN reports sent versus already-sent without duplicating kitchen jobs");

        var tablePlanWrapperSource = await File.ReadAllTextAsync(
            Path.Combine(
                desktopRoot,
                "src",
                "TorPos.App",
                "RestaurantTablePlanWindow.cs"));

        assert(
            !tablePlanWrapperSource.Contains(
                "TablePlanClose",
                StringComparison.Ordinal) &&
            !tablePlanWrapperSource.Contains(
                "\"SCHLIESSEN\"",
                StringComparison.Ordinal),
            "legacy Restaurant table-plan wrapper no longer exposes the daily SCHLIESSEN escape button");

        var workflowSource = await File.ReadAllTextAsync(
            Path.GetFullPath(
                Path.Combine(
                    desktopRoot,
                    "..",
                    ".github",
                    "workflows",
                    "tor-pos-ci.yml")));
        assert(
            workflowSource.Contains(
                "TOR-Restaurant-Setup-DEV4-" + "$" + "{{ github.sha }}",
                StringComparison.Ordinal) &&
            !workflowSource.Contains(
                "TOR-Restaurant-Setup-DEV-" + "$" + "{{ github.sha }}",
                StringComparison.Ordinal),
            "fourth Restaurant development installer is uploaded under the DEV4 artifact name");

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
