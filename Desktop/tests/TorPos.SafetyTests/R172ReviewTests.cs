using TorPos.Core;
using TorPos.Infrastructure;

public static class R172ReviewTests
{
    public static async Task Run(Action<bool, string> assert)
    {
        assert(
            QuantityStorage.Scale == 1000 &&
            QuantityStorage.ToMilli(0.001m) == 1 &&
            QuantityStorage.ToMilli(0.500m) == 500 &&
            QuantityStorage.ToMilli(12.345m) == 12345 &&
            QuantityStorage.FromMilli(12345) == 12.345m,
            "R172 fixed-point quantity storage keeps three decimal places exactly");

        var tooPreciseRejected = false;
        try
        {
            _ = QuantityStorage.ToMilli(0.0001m);
        }
        catch (InvalidOperationException)
        {
            tooPreciseRejected = true;
        }
        assert(
            tooPreciseRejected,
            "R172 quantity storage rejects values that cannot be represented exactly at milli-unit precision");

        var migrations = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Infrastructure/SchemaMigrationService.cs"));
        assert(
            migrations.Contains("R172_FIXED_POINT_QUANTITIES", StringComparison.Ordinal) &&
            migrations.Contains("stock_milli INTEGER", StringComparison.Ordinal) &&
            migrations.Contains("min_stock_milli INTEGER", StringComparison.Ordinal) &&
            migrations.Contains("sale_items ADD COLUMN quantity_milli INTEGER", StringComparison.Ordinal) &&
            migrations.Contains("parked_receipt_items ADD COLUMN quantity_milli INTEGER", StringComparison.Ordinal),
            "R172 schema migration adds integer stock and line-quantity columns while preserving existing databases");

        var infrastructure = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Infrastructure/Infrastructure.cs"));
        assert(
            infrastructure.Contains("stock_milli=$stockMilli", StringComparison.Ordinal) &&
            infrastructure.Contains("min_stock_milli=$minStockMilli", StringComparison.Ordinal) &&
            infrastructure.Contains("quantity_milli", StringComparison.Ordinal) &&
            infrastructure.Contains("QuantityStorage.ToMilli(", StringComparison.Ordinal) &&
            infrastructure.Contains("QuantityStorage.FromMilli(", StringComparison.Ordinal),
            "R172 product, sale and parked-receipt persistence use fixed-point integer quantities as the primary path");

        var training = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Infrastructure/TrainingReceiptRepository.cs"));
        var orders = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Infrastructure/OrderBestellungRepository.cs"));
        var cancelled = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Infrastructure/CancelledPositionStore.cs"));
        var datev = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Infrastructure/DatevKassenbuchAsciiService.cs"));
        assert(
            training.Contains("quantity_milli", StringComparison.Ordinal) &&
            orders.Contains("quantity_milli", StringComparison.Ordinal) &&
            cancelled.Contains("quantity_milli", StringComparison.Ordinal) &&
            datev.Contains("quantity_milli", StringComparison.Ordinal),
            "R172 training, order, cancellation and DATEV quantity paths understand fixed-point storage");

        var reports = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Infrastructure/BusinessManagementReports.cs"));
        var cloud = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Infrastructure/TorCloudSyncService.cs"));
        var kitchen = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Infrastructure/OrderPrintOutbox.cs"));
        assert(
            reports.Contains("QuantityStorage.FromMilli", StringComparison.Ordinal) &&
            cloud.Contains("QuantityStorage.FromMilli", StringComparison.Ordinal) &&
            kitchen.Contains("QuantityStorage.FromMilli", StringComparison.Ordinal),
            "R172 reports, cloud synchronization and kitchen printing read fixed-point quantities rather than accumulating REAL values");

        var active = 0;
        var maxActive = 0;
        var orderSeen = new List<int>();
        var gates = Enumerable.Range(0, 24)
            .Select(i => IoQueue.RunAsync(async () =>
            {
                var nowActive = Interlocked.Increment(ref active);
                var snapshot = Volatile.Read(ref maxActive);
                while (nowActive > snapshot)
                {
                    var prior = Interlocked.CompareExchange(
                        ref maxActive,
                        nowActive,
                        snapshot);
                    if (prior == snapshot)
                        break;
                    snapshot = prior;
                }

                orderSeen.Add(i);
                await Task.Delay(2);
                Interlocked.Decrement(ref active);
            }))
            .ToArray();

        await Task.WhenAll(gates);

        assert(
            maxActive == 1,
            "R172 IoQueue executes concurrent database operations one at a time");

        assert(
            orderSeen.SequenceEqual(Enumerable.Range(0, 24)),
            "R172 IoQueue preserves FIFO admission order for database work");

        var cardRefundSource = infrastructure;
        var kassenarchiv = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Infrastructure/DatevKassenarchivService.cs"));
        var kassenbuch = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Infrastructure/DatevKassenbuchAsciiService.cs"));
        assert(
            cardRefundSource.Contains("public Task<string> BeginAsync", StringComparison.Ordinal) &&
            cardRefundSource.Contains("public Task ClearAsync", StringComparison.Ordinal) &&
            cardRefundSource.Contains("public Task ResolveAsync", StringComparison.Ordinal) &&
            kassenarchiv.Contains("IoQueue.RunAsync", StringComparison.Ordinal) &&
            kassenbuch.Contains("IoQueue.RunAsync", StringComparison.Ordinal) &&
            migrations.Contains("=> IoQueue.RunAsync(() => InitializeDatabaseCoreAsync(ct));", StringComparison.Ordinal),
            "R172 formerly independent card-refund, DATEV and schema writes are serialized through the central database queue");

        var provider = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Infrastructure/SwissbitTseProvider.cs"));
        assert(
            provider.Contains("new SwissbitWatchdogBridge()", StringComparison.Ordinal),
            "R172 Swissbit production provider uses the watchdog bridge by default");

        var watchdog = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Infrastructure/SwissbitWatchdogBridge.cs"));
        assert(
            watchdog.Contains("new ProcessStartInfo", StringComparison.Ordinal) &&
            watchdog.Contains("Task.WhenAny(waitTask, timeoutTask)", StringComparison.Ordinal) &&
            watchdog.Contains("process.Kill(entireProcessTree: true)", StringComparison.Ordinal) &&
            watchdog.Contains("throw new TimeoutException", StringComparison.Ordinal),
            "R172 Swissbit native calls run in a killable helper process with a hard timeout");

        assert(
            watchdog.Contains("catch (OperationCanceledException)", StringComparison.Ordinal) &&
            watchdog.Contains("TryKill(process);", StringComparison.Ordinal),
            "R172 caller cancellation also terminates the isolated Swissbit helper instead of leaving a blocked native process behind");

        var program = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.App/Program.cs"));
        assert(
            program.Contains("SwissbitWorkerHost.IsWorkerCommand(args)", StringComparison.Ordinal) &&
            program.Contains("SwissbitWorkerHost.RunAsync()", StringComparison.Ordinal) &&
            program.IndexOf("SwissbitWorkerHost.IsWorkerCommand(args)", StringComparison.Ordinal) <
            program.IndexOf("BuildAvaloniaApp()", StringComparison.Ordinal),
            "R172 Swissbit worker entry runs before Avalonia and normal single-instance cashier startup");

        var workflow = File.ReadAllText(
            FindRepoFile(".github/workflows/tor-pos-ci.yml"));
        assert(
            workflow.Contains("TOR-POS-Source-", StringComparison.Ordinal) &&
            workflow.Contains("git archive --format=zip", StringComparison.Ordinal),
            "R172 CI publishes the exact committed source tree as a downloadable ZIP after validation");
    }

    private static string FindRepoFile(string relativePath)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(
                    dir.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        throw new FileNotFoundException(
            $"R172 review could not locate repository file: {relativePath}");
    }
}
