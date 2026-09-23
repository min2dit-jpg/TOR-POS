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
            var blockedPrinters =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

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

                var printerName = job.PrinterName;

                try
                {
                    var defaultPrinter = await _settings.GetAsync(
                        "device.kitchen_printer.name",
                        "",
                        ct);

                    var enabled = false;

                    if (!string.IsNullOrWhiteSpace(job.Station))
                    {
                        var stationPrefix = KitchenStations.SettingsPrefix(job.Station);
                        var stationEnabled = bool.TryParse(
                            await _settings.GetAsync(
                                stationPrefix + ".enabled",
                                "false",
                                ct),
                            out var stationIsEnabled) && stationIsEnabled;

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
                            out var defaultEnabled) && defaultEnabled;

                        if (string.IsNullOrWhiteSpace(printerName))
                            printerName = defaultPrinter;
                    }

                    if (!enabled)
                    {
                        // This route stays pending, but it must not block
                        // another station/printer from receiving its jobs.
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(printerName))
                        throw new InvalidOperationException(
                            "Küchendrucker ist aktiviert, aber kein Drucker ausgewählt.");

                    if (blockedPrinters.Contains(printerName))
                        continue;

                    var payload = JsonSerializer.Deserialize<KitchenPayload>(
                        job.PayloadJson)
                        ?? throw new InvalidDataException(
                            "Restaurant-Küchenauftrag ist beschädigt.");

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
                        payload.tableName ?? "Tisch",
                        string.IsNullOrWhiteSpace(payload.note)
                            ? ""
                            : "HINWEIS: " + payload.note
                    };

                    var print = new KitchenPrintJob(
                        job.CreatedAt,
                        0,
                        0,
                        payload.waiter ?? "",
                        new[]
                        {
                            new KitchenPrintLine(
                                BuildLineName(payload),
                                payload.QuantityMilli / 1000m)
                        },
                        string.Join(
                            " · ",
                            noteParts.Where(x => !string.IsNullOrWhiteSpace(x))));

                    // The Restaurant outbox id is reused as the persistent
                    // printer journal id. A retry therefore refers to the same
                    // physical print operation instead of silently creating a
                    // second logical ticket.
                    var record = new PrintJobRecord(
                        job.Id,
                        "QUEUED",
                        printerName,
                        null,
                        null,
                        "",
                        Kitchen: print,
                        PickupSlip: null);

                    await _printer.SubmitOrderAsync(record);

                    await _outbox.MarkHandedOverAsync(
                        job.Id,
                        ct);
                }
                catch (Exception ex)
                {
                    _onError?.Invoke(ex);

                    // The printer may have persisted ownership and then thrown
                    // (for example timeout/uncertain spooler outcome). In that
                    // case never resubmit: the journal is authoritative.
                    if (await _journal.GetAsync(job.Id) is not null)
                    {
                        await _outbox.MarkHandedOverAsync(
                            job.Id,
                            ct);
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(printerName))
                        blockedPrinters.Add(printerName);

                    await _outbox.MarkFailedAttemptAsync(
                        job.Id,
                        ex.Message,
                        ct);

                    // A broken station/printer must not stop unrelated
                    // kitchen routes during the same dispatch pass.
                    continue;
                }
            }
        }
        finally
        {
            _gate.Release();
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
