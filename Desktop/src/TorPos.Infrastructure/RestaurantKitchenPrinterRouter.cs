using System.Collections.Concurrent;

namespace TorPos.Infrastructure;

public sealed class RestaurantKitchenPrinterRouter : IAsyncDisposable
{
    private readonly PrintJobJournal _journal;
    private readonly TimeSpan _printTimeout;
    private readonly ConcurrentDictionary<string, Lazy<StarMcPrint3PrinterService>> _lanes =
        new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _disposed;

    public RestaurantKitchenPrinterRouter(
        PrintJobJournal journal,
        TimeSpan? printTimeout = null)
    {
        _journal = journal;
        _printTimeout = printTimeout ?? TimeSpan.FromSeconds(15);
    }

    public Task SubmitOrderAsync(PrintJobRecord record)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RestaurantKitchenPrinterRouter));

        var printer = (record.Printer ?? "").Trim();
        if (printer.Length == 0)
            throw new InvalidOperationException(
                "Restaurant-Küchendrucker wurde nicht ausgewählt.");

        var lane = _lanes.GetOrAdd(
            printer,
            name => new Lazy<StarMcPrint3PrinterService>(
                () => new StarMcPrint3PrinterService(
                    _journal,
                    _printTimeout,
                    printOverride: null,
                    isolatedPrinter: name),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return lane.Value.SubmitOrderAsync(
            record with { Printer = printer });
    }

    public IReadOnlyList<string> ActivePrinterLanes() =>
        _lanes
            .Where(x => x.Value.IsValueCreated)
            .Select(x => x.Key)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async ValueTask DisposeAsync()
    {
        _disposed = true;

        foreach (var lane in _lanes.Values)
        {
            if (!lane.IsValueCreated)
                continue;

            try
            {
                await lane.Value.DisposeAsync();
            }
            catch
            {
                // Application shutdown must continue even if one printer lane
                // has a stuck native driver call.
            }
        }

        _lanes.Clear();
    }
}
