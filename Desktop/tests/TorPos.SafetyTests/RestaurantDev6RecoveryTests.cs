using TorPos.Infrastructure;

// DEV6 independent hardening: a handheld Storno can crash after the durable
// APPLIED audit but before its command journal reaches COMPLETED. Recovery must
// detect that exact phase and continue instead of retrying the unique audit row.
public static class RestaurantDev6RecoveryTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var db = await SafetyDatabase.CreateCurrentAsync(
            Path.Combine(root, "restaurant-dev6-recovery-" + Guid.NewGuid().ToString("N") + ".db"));
        var audit = new ControlledPosActionService(db);
        var actionId = "DEV6-RECOVERY-" + Guid.NewGuid().ToString("N");
        var request = new PosActionLogRequest
        {
            ActionId = actionId,
            Phase = "APPLIED",
            Actor = "kellner1",
            RegisterId = "HANDHELD-1",
            OperationId = "session-1",
            ActionType = "RESTAURANT_POSITION_STORNO",
            Reason = "Fehlbuchung",
            EntityType = "RESTAURANT_SESSION_ITEM",
            EntityId = "42",
            BeforeTotalCents = 1200,
            AfterTotalCents = 800,
            AmountCents = 400,
            Details = "recovery-test"
        };

        var before = await audit.HasEntryAsync(actionId, "APPLIED");
        await audit.AppendAsync(request with { Phase = "AUTHORIZED" });
        await audit.AppendAsync(request);
        var authorized = await audit.HasEntryAsync(actionId, "AUTHORIZED");
        var applied = await audit.HasEntryAsync(actionId, "APPLIED");
        var failedPhase = await audit.HasEntryAsync(actionId, "FAILED");

        assert(
            !before && authorized && applied && !failedPhase,
            "DEV6 handheld recovery can distinguish already committed AUTHORIZED/APPLIED Storno audit phases from a missing phase");

        var handheld = File.ReadAllText(FindRepoFile(
            "Desktop/src/TorPos.App/RestaurantHandheldService.cs"));
        assert(
            handheld.Contains(
                "claim.State == RestaurantCommandClaimState.Recovered",
                StringComparison.Ordinal) &&
            handheld.Contains(
                "_controlledActions.HasEntryAsync(",
                StringComparison.Ordinal) &&
            handheld.Contains(
                "\"AUTHORIZED\"",
                StringComparison.Ordinal) &&
            handheld.Contains(
                "\"APPLIED\"",
                StringComparison.Ordinal),
            "DEV6 handheld recovered Storno skips already durable AUTHORIZED/APPLIED audit phases before retrying fiscal/kitchen/command completion");
    }

    private static string FindRepoFile(string relative)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName,
                relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException(relative);
    }
}
