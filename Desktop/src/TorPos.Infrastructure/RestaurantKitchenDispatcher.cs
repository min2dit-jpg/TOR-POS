using System.Text.Json;
using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed class RestaurantKitchenDispatcher : IAsyncDisposable
{
    private readonly RestaurantKitchenOutbox _outbox;
    private readonly SettingsRepository _settings;
    private readonly RestaurantKitchenPrinterRouter _printer;
    private readonly PrintJobJournal _journal;
    private readonly Action<Exception>? _onError;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;

    public RestaurantKitchenDispatcher(
        RestaurantKitchenOutbox outbox,
        SettingsRepository settings,
        RestaurantKitchenPrinterRouter printer,
        PrintJobJournal journal,
        Action<Exception>? onError = null)
    {
        _outbox = outbox;
        _settings = settings;
        _printer = printer;
        _journal = journal;
        _onError = onError;
    }

    public void Start() =>
        _worker ??= Task.Run(WorkerAsync);

    public void Notify()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    public async Task DispatchOnceAsync(
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var resolved = new List<ResolvedKitchenJob>();

            foreach (var job in await _outbox.PendingAsync(ct))
            {
                ct.ThrowIfCancellationRequested();

                if (await _journal.GetAsync(job.Id) is not null)
                {
                    await _outbox.MarkHandedOverAsync(
                        job.Id,
                        ct);
                    continue;
                }

                try
                {
                    var route = await ResolvePrinterAsync(
                        job,
                        ct);

                    if (route is not null)
                        resolved.Add(route);
                }
                catch (Exception ex)
                {
                    _onError?.Invoke(ex);

                    await _outbox.MarkFailedAttemptAsync(
                        job.Id,
                        ex.Message,
                        ct);
                }
            }

            // One slow/offline printer must never delay an unrelated kitchen
            // station. Jobs remain ordered inside each physical printer lane,
            // while separate printers are dispatched concurrently.
            var lanes = resolved
                .GroupBy(
                    x => x.PrinterName,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                    DispatchPrinterLaneAsync(
                        group
                            .OrderBy(x => x.Job.CreatedAt)
                            .ThenBy(x => x.Job.Id)
                            .ToArray(),
                        ct))
                .ToArray();

            if (lanes.Length > 0)
                await Task.WhenAll(lanes);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ResolvedKitchenJob?> ResolvePrinterAsync(
        RestaurantKitchenJob job,
        CancellationToken ct)
    {
        var printerName = job.PrinterName;

        var defaultPrinter = await _settings.GetAsync(
            "device.kitchen_printer.name",
            "",
            ct);

        var enabled = false;

        if (!string.IsNullOrWhiteSpace(job.Station))
        {
            var stationPrefix =
                KitchenStations.SettingsPrefix(
                    job.Station);

            var stationEnabled = bool.TryParse(
                await _settings.GetAsync(
                    stationPrefix + ".enabled",
                    "false",
                    ct),
                out var stationIsEnabled) &&
                stationIsEnabled;

            var stationPrinter = await _settings.GetAsync(
                stationPrefix + ".name",
                "",
                ct);

            if (stationEnabled &&
                !string.IsNullOrWhiteSpace(stationPrinter))
            {
                enabled = true;
                printerName = stationPrinter;
            }
        }

        if (!enabled)
        {
            enabled = bool.TryParse(
                await _settings.GetAsync(
                    "device.kitchen_printer.enabled",
                    "false",
                    ct),
                out var defaultEnabled) &&
                defaultEnabled;

            if (string.IsNullOrWhiteSpace(printerName))
                printerName = defaultPrinter;
        }

        if (!enabled)
        {
            // Keep disabled routes pending. Other enabled printer lanes are
            // still free to dispatch during the same pass.
            return null;
        }

        if (string.IsNullOrWhiteSpace(printerName))
        {
            throw new InvalidOperationException(
                "Küchendrucker ist aktiviert, aber kein Drucker ausgewählt.");
        }

        var payload = JsonSerializer.Deserialize<KitchenPayload>(
            job.PayloadJson)
            ?? throw new InvalidDataException(
                "Restaurant-Küchenauftrag ist beschädigt.");

        return new ResolvedKitchenJob(
            job,
            printerName.Trim(),
            payload);
    }

    private async Task DispatchPrinterLaneAsync(
        IReadOnlyList<ResolvedKitchenJob> lane,
        CancellationToken ct)
    {
        foreach (var resolved in lane)
        {
            ct.ThrowIfCancellationRequested();

            var job = resolved.Job;

            try
            {
                var prefix = job.Action switch
                {
                    "CANCEL" => "STORNO · NICHT ZUBEREITEN",
                    "MOVE" => "TISCHWECHSEL",
                    "NOTE" => "TISCHNOTIZ",
                    _ => "NEUE BESTELLUNG"
                };

                var noteParts = new[]
                {
                    prefix,
                    resolved.Payload.tableName ?? "Tisch",
                    string.IsNullOrWhiteSpace(
                        resolved.Payload.note)
                        ? ""
                        : "HINWEIS: " +
                          resolved.Payload.note
                };

                var print = new KitchenPrintJob(
                    job.CreatedAt,
                    0,
                    0,
                    resolved.Payload.waiter ?? "",
                    new[]
                    {
                        new KitchenPrintLine(
                            BuildLineName(
                                resolved.Payload),
                            resolved.Payload.QuantityMilli /
                                1000m)
                    },
                    string.Join(
                        " · ",
                        noteParts.Where(x =>
                            !string.IsNullOrWhiteSpace(x))));

                var record = new PrintJobRecord(
                    job.Id,
                    "QUEUED",
                    resolved.PrinterName,
                    null,
                    null,
                    "",
                    Kitchen: print,
                    PickupSlip: null);

                await _printer.SubmitOrderAsync(
                    record);

                await _outbox.MarkHandedOverAsync(
                    job.Id,
                    ct);
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);

                // The persistent print journal is authoritative. If printer
                // ownership was already recorded, never submit this logical
                // ticket again.
                if (await _journal.GetAsync(job.Id) is not null)
                {
                    await _outbox.MarkHandedOverAsync(
                        job.Id,
                        ct);
                }
                else
                {
                    await _outbox.MarkFailedAttemptAsync(
                        job.Id,
                        ex.Message,
                        ct);
                }

                // Preserve strict order inside one physical printer lane after
                // a failure/timeout. Other printer lanes keep running.
                break;
            }
        }
    }

    private async Task WorkerAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await DispatchOnceAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
            }

            try
            {
                await _wake.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    _stop.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static string BuildLineName(
        KitchenPayload payload)
    {
        var name = payload.ProductName ?? "Artikel";
        return string.IsNullOrWhiteSpace(payload.VariantName)
            ? name
            : $"{name} · {payload.VariantName}";
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();

        if (_worker is not null)
        {
            try
            {
                await _worker.WaitAsync(
                    TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException) { }
        }

        _wake.Dispose();
        _gate.Dispose();
        _stop.Dispose();
    }

    private sealed record ResolvedKitchenJob(
        RestaurantKitchenJob Job,
        string PrinterName,
        KitchenPayload Payload);

    private sealed record KitchenPayload(
        string? action,
        string? sessionId,
        long tableId,
        string? tableName,
        string? waiter,
        int guestCount,
        string? note,
        long itemId,
        string? ProductName,
        string? VariantName,
        long QuantityMilli,
        long UnitPriceCents,
        string? actor);
}
