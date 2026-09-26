// R-8: TOR Restaurant load test (manual / nightly, not per commit).
//
// The plan (Dokumentation/TOR-RESTAURANT-PRODUKTPLAN.md §11) demands at least
// 20 connected devices and 100 tables while the main till stays responsive.
// This tool drives the same service layer the Restaurant device API calls -
// RestaurantRepository, RestaurantKitchenOutbox, the shared IoQueue and SQLite
// in WAL mode - concurrently, and measures what the till feels:
//
//   * waiters: add positions with a line token; 10 % of the adds are replayed
//     with the same token (a lost HTTP reply) and must not create a second line;
//     each new line queues one kitchen job, 10 % replayed with the same job id;
//   * table plan polling per device, KDS board polling;
//   * the till: every second a checkout preparation on its own table
//     (BuildCheckoutDraft -> PreparePaymentReservation -> Cancel), and every
//     100 ms a trivial IoQueue read ("how long does the UI wait for the DB").
//
// Not included, on purpose: HTTP/TLS and the per-IP rate limiter of the device
// API (all simulated devices would share one loopback IP), and the TSE (its
// latency is hardware-bound; see O-2/O-3). The numbers are therefore the
// database/queue floor under Restaurant load, not the end-to-end device latency.
//
//   dotnet run --project tools/TorPos.RestaurantStress -c Release -- \
//       [--devices 20] [--tables 100] [--seconds 60] [--report verification]
//       [--kitchen-item-index]
//
// --kitchen-item-index adds, to the throw-away test database only, an index on
// restaurant_kitchen_jobs(session_item_id,action,created_at). The KDS board looks
// up the station of every open position with a correlated sub-select on that
// column, which has no index in the product schema; the flag shows the effect
// of the proposed index before anyone adds it to a real migration.
//
// Exit code 0 = all thresholds met, 1 = a threshold failed, 2 = error.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using TorPos.Core;
using TorPos.Infrastructure;

var devices = IntArg("--devices", 20, 1, 200);
var tables = IntArg("--tables", 100, 2, 1000);
var seconds = IntArg("--seconds", 60, 5, 3600);
var reportDir = StringArg("--report") ?? Path.Combine(Directory.GetCurrentDirectory(), "verification");
var kitchenItemIndex = args.Contains("--kitchen-item-index");

// Thresholds from the Restaurant proposal (R-8).
const double CheckoutP95Ms = 1000, CheckoutP99Ms = 2000, IoQueueP99Ms = 500, KdsP95Ms = 1000;

var root = Path.Combine(Path.GetTempPath(), "TOR-RESTAURANT-STRESS-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
Directory.CreateDirectory(root);
var previousEdition = Environment.GetEnvironmentVariable("TOR_POS_PRODUCT_EDITION");
Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", "RESTAURANT", EnvironmentVariableTarget.Process);

try
{
    var db = new SqliteDatabase(Path.Combine(root, "restaurant-stress.db"));
    await new SchemaMigrationService(db, new DatabaseBackupService(db), Path.Combine(root, "migration-backups"))
        .InitializeDatabaseAsync();
    var repo = new RestaurantRepository(db);
    var kitchen = new RestaurantKitchenOutbox(db);
    if (kitchenItemIndex)
    {
        await using var c = db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = "CREATE INDEX IF NOT EXISTS ix_stress_kitchen_jobs_item ON restaurant_kitchen_jobs(session_item_id,action,created_at);";
        await q.ExecuteNonQueryAsync();
    }

    var products = Enumerable.Range(1, 30)
        .Select(i => new Product { Id = 700000 + i, Name = $"Stress Artikel {i}", BasePriceCents = 250 + i * 37, VatRate = i % 3 == 0 ? 19m : 7m, Unit = "Stück", IsActive = true })
        .ToArray();

    // Tables: one per device-slot for waiters, plus 2 reserved for the till.
    var area = await repo.SaveAreaAsync("Stress");
    var waiterSessions = new List<(RestaurantTableSession Session, string Name, int Device)>();
    for (var t = 0; t < tables; t++)
    {
        var tableId = await repo.SaveTableAsync(area, $"S{t + 1:000}", $"Tisch {t + 1}", seats: 4);
        var device = t % devices;
        var session = await repo.OpenTableAsync(tableId, $"Kellner {device + 1}", guestCount: 2, deviceId: $"HANDHELD-{device + 1:00}");
        waiterSessions.Add((session, $"Tisch {t + 1}", device));
    }
    var tillTable = await repo.SaveTableAsync(area, "KASSE", "Kassentisch", seats: 2);
    var tillSession = await repo.OpenTableAsync(tillTable, "Kasse", guestCount: 1, deviceId: "KASSE-1");
    for (var i = 0; i < 3; i++)
    {
        var s = await repo.GetSessionAsync(tillSession.Id) ?? throw new InvalidOperationException("Kassentisch fehlt.");
        await repo.AddItemAsync(s.Id, s.Version, products[i], 1m, "Kasse", "KASSE-1");
    }

    var add = new ConcurrentBag<double>();
    var replayAdd = new ConcurrentBag<double>();
    var poll = new ConcurrentBag<double>();
    var kds = new ConcurrentBag<double>();
    var checkout = new ConcurrentBag<double>();
    var ioProbe = new ConcurrentBag<double>();
    var created = new ConcurrentDictionary<string, long>();   // line token -> item id
    var kitchenJobs = new ConcurrentDictionary<string, byte>();
    long conflicts = 0, errors = 0, duplicateReplies = 0;
    var errorSamples = new ConcurrentQueue<string>();

    void Error(Exception ex)
    {
        Interlocked.Increment(ref errors);
        if (errorSamples.Count < 10) errorSamples.Enqueue(ex.GetType().Name + ": " + ex.Message);
    }

    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    var token = stop.Token;
    var tasks = new List<Task>();

    for (var d = 0; d < devices; d++)
    {
        var device = d;
        var mine = waiterSessions.Where(x => x.Device == device).ToArray();
        if (mine.Length == 0) continue;
        var rng = new Random(1000 + device);
        var deviceId = $"HANDHELD-{device + 1:00}";
        var waiter = $"Kellner {device + 1}";

        tasks.Add(Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                var target = mine[rng.Next(mine.Length)];
                var product = products[rng.Next(products.Length)];
                var lineToken = Guid.NewGuid().ToString("N");
                try
                {
                    var current = await repo.GetSessionAsync(target.Session.Id) ?? throw new InvalidOperationException("Tisch fehlt.");
                    var sw = Stopwatch.StartNew();
                    RestaurantItemMutationResult result;
                    try
                    {
                        result = await repo.AddItemWithLineTokenAsync(current.Id, current.Version, product, 1m, waiter, lineToken, deviceId);
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("geändert", StringComparison.OrdinalIgnoreCase) ||
                                                               ex.Message.Contains("Version", StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(ref conflicts);
                        continue;
                    }
                    add.Add(sw.Elapsed.TotalMilliseconds);
                    if (!result.Created) Interlocked.Increment(ref duplicateReplies);
                    created[lineToken] = result.Item.Id;

                    var jobId = Guid.NewGuid().ToString("N");
                    await kitchen.EnqueueNewItemIdempotentAsync(current, result.Item, target.Name, waiter, jobId);
                    kitchenJobs[jobId] = 0;

                    if (rng.Next(10) == 0)
                    {
                        // Lost reply: the device sends the very same command again.
                        var after = await repo.GetSessionAsync(current.Id) ?? current;
                        var rsw = Stopwatch.StartNew();
                        var replay = await repo.AddItemWithLineTokenAsync(after.Id, after.Version, product, 1m, waiter, lineToken, deviceId);
                        replayAdd.Add(rsw.Elapsed.TotalMilliseconds);
                        if (replay.Created || replay.Item.Id != result.Item.Id) Interlocked.Increment(ref duplicateReplies);
                        await kitchen.EnqueueNewItemIdempotentAsync(after, replay.Item, target.Name, waiter, jobId);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) { Error(ex); }

                try { await Task.Delay(150 + rng.Next(150), token); } catch (OperationCanceledException) { break; }
            }
        }));

        // Table plan polling of this device.
        tasks.Add(Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    await repo.ListLiveTableSummariesAsync();
                    poll.Add(sw.Elapsed.TotalMilliseconds);
                    await Task.Delay(2000, token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Error(ex); }
            }
        }));
    }

    // KDS board.
    tasks.Add(Task.Run(async () =>
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                await kitchen.BoardAsync();
                kds.Add(sw.Elapsed.TotalMilliseconds);
                await Task.Delay(500, token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Error(ex); }
        }
    }));

    // The till: checkout preparation on its own table, reserved and released.
    tasks.Add(Task.Run(async () =>
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var s = await repo.GetSessionAsync(tillSession.Id) ?? throw new InvalidOperationException("Kassentisch fehlt.");
                var items = await repo.ListActiveItemsAsync(s.Id);
                var draft = await repo.BuildCheckoutDraftAsync(s.Id, s.Version,
                    items.Select(x => new RestaurantSplitSelection(x.Id, x.QuantityMilli)).ToArray());
                await repo.PreparePaymentReservationAsync(draft);
                await repo.CancelPaymentReservationAsync(draft.OperationId);
                checkout.Add(sw.Elapsed.TotalMilliseconds);
                await Task.Delay(1000, token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Error(ex); }
        }
    }));

    // The till's UI waiting on the shared IoQueue.
    tasks.Add(Task.Run(async () =>
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                await IoQueue.RunAsync(async () =>
                {
                    await using var c = db.OpenReadConnection();
                    await using var q = c.CreateCommand();
                    q.CommandText = "SELECT COUNT(*) FROM app_settings;";
                    return await q.ExecuteScalarAsync();
                });
                ioProbe.Add(sw.Elapsed.TotalMilliseconds);
                await Task.Delay(100, token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Error(ex); }
        }
    }));

    await Task.WhenAll(tasks);

    // Integrity: one line per line token, one kitchen job per job id.
    long lines, distinctTokens, jobs, distinctJobs;
    await using (var c = db.OpenReadConnection())
    await using (var q = c.CreateCommand())
    {
        q.CommandText = """
            SELECT COUNT(*), COUNT(DISTINCT line_token) FROM restaurant_session_items
            WHERE product_id BETWEEN 700001 AND 700030 AND added_by LIKE 'Kellner %';
            """;
        await using (var r = await q.ExecuteReaderAsync()) { await r.ReadAsync(); lines = r.GetInt64(0); distinctTokens = r.GetInt64(1); }
        // One NEW kitchen job per position: a replayed job id must not add a second.
        q.CommandText = """
            SELECT COUNT(*),
                   (SELECT COUNT(*) FROM (SELECT session_item_id FROM restaurant_kitchen_jobs
                                          WHERE action='NEW' GROUP BY session_item_id HAVING COUNT(*)>1))
            FROM restaurant_kitchen_jobs WHERE action='NEW';
            """;
        await using (var r = await q.ExecuteReaderAsync()) { await r.ReadAsync(); jobs = r.GetInt64(0); distinctJobs = r.GetInt64(1); }
    }

    var duplicateLines = lines - distinctTokens + Math.Max(0, lines - created.Count);
    var duplicateKitchen = Math.Max(0, jobs - kitchenJobs.Count) + distinctJobs;

    var checks = new List<(bool Ok, string Text)>
    {
        (Percentile(checkout, 95) < CheckoutP95Ms, $"Kasse Checkout-Vorbereitung p95 < {CheckoutP95Ms} ms"),
        (Percentile(checkout, 99) < CheckoutP99Ms, $"Kasse Checkout-Vorbereitung p99 < {CheckoutP99Ms} ms"),
        (Percentile(ioProbe, 99) < IoQueueP99Ms, $"IoQueue-Wartezeit der Kasse p99 < {IoQueueP99Ms} ms"),
        (Percentile(kds, 95) < KdsP95Ms, $"KDS-Board p95 < {KdsP95Ms} ms"),
        (duplicateReplies == 0 && duplicateLines == 0, "keine doppelte Tischposition bei wiederholtem Befehl"),
        (duplicateKitchen == 0, "kein doppelter Küchenauftrag bei wiederholtem Befehl"),
        (errors == 0, "keine unerwarteten Fehler"),
        (checkout.Count > 0 && add.Count > 0, "Last wurde tatsächlich erzeugt")
    };

    var report = new StringBuilder();
    report.AppendLine($"# TOR Restaurant Lasttest · {DateTime.Now:dd.MM.yyyy HH:mm}");
    report.AppendLine();
    report.AppendLine($"Geräte: {devices} · Tische: {tables} · Dauer: {seconds} s · Küchen-Index (nur Testdatenbank): {(kitchenItemIndex ? "ja" : "nein")} · Rechner: {Environment.MachineName} · {Environment.ProcessorCount} CPU · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
    report.AppendLine();
    report.AppendLine("Gemessen wird die Service-/Datenbankschicht (IoQueue, SQLite WAL) unter Restaurant-Last - ohne HTTP/TLS und ohne TSE (siehe Kopf von Program.cs).");
    report.AppendLine();
    report.AppendLine("| Messung | Anzahl | p50 ms | p95 ms | p99 ms | max ms |");
    report.AppendLine("|---|---:|---:|---:|---:|---:|");
    foreach (var (name, bag) in new[] { ("Kellner: Position hinzufügen", add), ("Kellner: Wiederholung (verlorene Antwort)", replayAdd), ("Tischplan abrufen", poll), ("KDS-Board", kds), ("Kasse: Checkout-Vorbereitung", checkout), ("Kasse: IoQueue-Wartezeit", ioProbe) })
        report.AppendLine($"| {name} | {bag.Count} | {Percentile(bag, 50):0.0} | {Percentile(bag, 95):0.0} | {Percentile(bag, 99):0.0} | {(bag.IsEmpty ? 0 : bag.Max()):0.0} |");
    report.AppendLine();
    report.AppendLine($"Versionskonflikte (erwartbar bei gleichzeitigen Änderungen): {conflicts} · Fehler: {errors} · Tischpositionen: {lines} · Küchenaufträge: {jobs}");
    foreach (var sample in errorSamples) report.AppendLine($"- Fehlerbeispiel: {sample}");
    report.AppendLine();
    report.AppendLine("| Prüfung | Ergebnis |");
    report.AppendLine("|---|---|");
    foreach (var (ok, text) in checks) report.AppendLine($"| {text} | {(ok ? "BESTANDEN" : "NICHT BESTANDEN")} |");

    Directory.CreateDirectory(reportDir);
    var reportPath = Path.Combine(reportDir, $"RESTAURANT-STRESS-{DateTime.Now:yyyyMMdd-HHmmss}.md");
    await File.WriteAllTextAsync(reportPath, report.ToString());
    Console.WriteLine(report.ToString());
    Console.WriteLine("Bericht: " + reportPath);
    return checks.All(x => x.Ok) ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Lasttest abgebrochen: " + ex);
    return 2;
}
finally
{
    Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", previousEdition, EnvironmentVariableTarget.Process);
    try { Directory.Delete(root, recursive: true); } catch { }
}

static double Percentile(IEnumerable<double> values, int p)
{
    var sorted = values.OrderBy(x => x).ToArray();
    if (sorted.Length == 0) return 0;
    var index = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
    return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
}

int IntArg(string name, int fallback, int min, int max)
{
    var raw = StringArg(name);
    return raw is not null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, min, max) : fallback;
}

string? StringArg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
