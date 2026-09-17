using System.Text;
using TorPos.Core;
using TorPos.Infrastructure;

// R146: positions cancelled before an order is accepted, or while it is changed
// or cancelled, belong to that order record.
//
// DSFinV-K 4.2.3: a position cancelled during capture is documented by "ein
// zusätzlicher Positionsdatensatz …, bei dem MENGE mit negiertem Vorzeichen
// dargestellt wird"; orders are "eigenständige Vorgänge" secured on their own.
// R143 kept the cancelled positions with receipts and aborts; an order record
// (R137/R138) showed only what the order held when it was parked.
public static class R146ReviewTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        CartLine L(long id, string name, decimal qty, long price, decimal vat) =>
            new() { ProductId = id, ProductName = name, Quantity = qty, UnitPriceCents = price, VatRate = vat };

        var dir = Path.Combine(root, "r146-order-cancellations");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r146.db"));
        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["company.name"] = "R146 Imbiss",
            ["company.street"] = "Hauptstraße 1",
            ["company.zip"] = "10115",
            ["company.city"] = "Berlin",
            ["company.tax_no"] = "27/123/45678",
            ["tse.status"] = "AKTIV",
            ["tse.client_id"] = "KASSE-1",
        });
        var provider = new RecordingTseProvider();
        var tse = new TseFailSafeService(provider, new TseOutageRepository(db, audit), audit);
        var vorgaenge = new TseVorgangService(db, tse, settings);
        var parked = new ParkedReceiptRepository(db);
        var records = new OrderBestellungRepository(db);
        var signing = new OrderFiscalSigningService(tse, settings, parked) { Vorgaenge = vorgaenge, Bestellungen = records };
        string LastProcessData() => Encoding.UTF8.GetString(provider.Finishes[^1].ProcessData);

        // ---------- acceptance ----------
        var now = DateTimeOffset.Now;
        var capture = new TseVorgangCartTracker();
        capture.OnCartChanged(new[] { L(1, "Döner", 2, 700, 7m) }, 0, false, true, now);
        capture.OnCartChanged(new[] { L(1, "Döner", 1, 700, 7m) }, 0, false, true, now);                        // -1
        capture.OnCartChanged(new[] { L(1, "Döner", 1, 700, 7m), L(2, "Cola", 1, 250, 19m) }, 0, false, true, now);
        capture.OnCartChanged(new[] { L(1, "Döner", 1, 700, 7m) }, 0, false, true, now);                        // SOFORT STORNO
        var beforeAcceptance = capture.CancelledLines.ToArray();
        capture.Release();

        await vorgaenge.StartAsync("order-146", false, now.AddMinutes(-3), "kasse1");
        var order = await parked.ParkAsync(new[] { L(1, "Döner", 1, 700, 7m) }, 0, "kasse1", assignPickupNumber: true);
        await signing.SignInVorgangAsync(order, "order-146", now.AddMinutes(-3), "kasse1", cancelledLines: beforeAcceptance);
        var (_, securedAfterAcceptance) = await records.SecuredAsync(order.Id);
        assert(LastProcessData() == "1;\"Döner\";7.00\r1;\"Döner\";7.00\r-1;\"Döner\";7.00\r1;\"Cola\";2.50\r-1;\"Cola\";2.50" &&
               securedAfterAcceptance.Single() is { ProductName: "Döner", Quantity: 1 },
            "R146 the acceptance record carries the positions cancelled before it as captured/negated pairs, secured in Bestellung-V1; the order still adds up to its positions");

        // ---------- a change that ends where it began ----------
        var recalled = (await parked.GetOpenByIdAsync(order.Id))!;
        var look = new TseVorgangCartTracker();
        look.SetBaseline(recalled.Lines, 0);
        look.OnCartChanged(new[] { L(1, "Döner", 1, 700, 7m), L(3, "Ayran", 1, 250, 7m) }, 0, false, true, now);
        look.OnCartChanged(new[] { L(1, "Döner", 1, 700, 7m) }, 0, false, true, now);
        var noopCancelled = look.CancelledLines.ToArray();
        var finishesBefore = provider.Finishes.Count;
        await vorgaenge.StartAsync("change-146-noop", false, now, "kasse1");
        var noop = await signing.SecureChangeAsync(recalled, recalled.Lines, "change-146-noop", now, "kasse1", cancelledLines: noopCancelled);
        long abortedItems, abortedTotal;
        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "SELECT (SELECT COUNT(*) FROM aborted_vorgang_items i JOIN aborted_vorgaenge a ON a.id=i.aborted_id WHERE a.vorgang_id='change-146-noop'), (SELECT total_cents FROM aborted_vorgaenge WHERE vorgang_id='change-146-noop');";
            await using var r = await q.ExecuteReaderAsync();
            await r.ReadAsync();
            abortedItems = r.GetInt64(0);
            abortedTotal = r.GetInt64(1);
        }
        assert(noop is null && (await vorgaenge.GetAsync("change-146-noop"))!.State == TseVorgangService.Aborted &&
               provider.Finishes.Count == finishesBefore + 1 && abortedItems == 2 && abortedTotal == 0,
            "R146 a change that leaves the order as it was secures no record; its aborted Vorgang keeps the position cancelled in it (R143)");

        // ---------- a change ----------
        await vorgaenge.StartAsync("change-146", false, now, "kasse1");
        await parked.UpdateAsync(order.Id, new[] { L(1, "Döner", 1, 700, 7m), L(2, "Cola", 1, 250, 19m) }, 0);
        var changed = (await parked.GetOpenByIdAsync(order.Id))!;
        var change = await signing.SecureChangeAsync(changed, recalled.Lines, "change-146", now, "kasse1",
            cancelledLines: new[] { L(2, "Cola", 1, 250, 19m) });
        var (_, securedAfterChange) = await records.SecuredAsync(order.Id);
        assert(change is { Kind: OrderBestellungKind.Aenderung } && change.Lines.Sum(l => l.LineTotalCents) == 250 &&
               LastProcessData() == "1;\"Cola\";2.50\r1;\"Cola\";2.50\r-1;\"Cola\";2.50" &&
               securedAfterChange.Count == 2 && securedAfterChange.Sum(l => l.LineTotalCents) == 950,
            "R146 a change record holds the difference and, behind it, the positions cancelled while the change was captured");

        // ---------- cancellation with C ----------
        await parked.CancelAsync(order.Id);
        await vorgaenge.StartAsync("cancel-146", false, now, "kasse1");
        var storno = await signing.SecureCancellationAsync(changed, "cancel-146", now, "kasse1",
            cancelledLines: new[] { L(3, "Ayran", 1, 250, 7m) });
        var (count, securedAfterStorno) = await records.SecuredAsync(order.Id);
        assert(storno is { Kind: OrderBestellungKind.Storno } &&
               LastProcessData() == "-1;\"Döner\";7.00\r-1;\"Cola\";2.50\r1;\"Ayran\";2.50\r-1;\"Ayran\";2.50" &&
               count == 3 && securedAfterStorno.Count == 0,
            "R146 the cancellation record reverses everything secured and documents what was cancelled before; the records add up to nothing");

        // ---------- export ----------
        await Task.Delay(15);
        await new BusinessManagementService(db, settings, audit).CreateZArchiveAsync("chef", "TEST");
        var folder = await new DsfinvkExportService(db, settings).ExportAsync(DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddMinutes(1), Path.Combine(dir, "out"));
        var positions = Csv(Path.Combine(folder, "lines.csv"));
        var heads = Csv(Path.Combine(folder, "transactions.csv")).ToDictionary(h => h["BON_ID"]);
        var acceptanceRows = positions.Where(p => p["BON_ID"] == $"BE-{order.ParkNumber}-1").ToList();
        var stornoRows = positions.Where(p => p["BON_ID"] == $"BE-{order.ParkNumber}-3").ToList();
        assert(acceptanceRows.Select(p => p["MENGE"]).SequenceEqual(new[] { "1,000", "1,000", "-1,000", "1,000", "-1,000" }) &&
               acceptanceRows.All(p => p["P_STORNO"] == "0") &&
               heads[$"BE-{order.ParkNumber}-1"]["UMS_BRUTTO"] == "7,00" &&
               stornoRows.Select(p => p["MENGE"]).SequenceEqual(new[] { "-1,000", "-1,000", "1,000", "-1,000" }) &&
               heads[$"BE-{order.ParkNumber}-3"]["UMS_BRUTTO"] == "-9,50",
            "R146 the export lists the cancelled positions with their order record as position plus negated position (DSFinV-K 4.2.3); the record amounts are unchanged");
    }

    /// <summary>Reads a DSFinV-K file: quoted fields may contain ";" and doubled quotes.</summary>
    private static List<Dictionary<string, string>> Csv(string path)
    {
        var rows = File.ReadAllText(path, Encoding.UTF8).Split("\r\n").Where(r => r.Length > 0).ToList();
        var header = Fields(rows[0]);
        return rows.Skip(1)
            .Select(Fields)
            .Select(cells => header.Select((name, i) => (name, value: i < cells.Count ? cells[i] : "")).ToDictionary(x => x.name, x => x.value))
            .ToList();
    }

    private static List<string> Fields(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ';') { fields.Add(field.ToString()); field.Clear(); }
            else field.Append(ch);
        }

        fields.Add(field.ToString());
        return fields;
    }

    private sealed class RecordingTseProvider : ITseProvider
    {
        public readonly List<TseTransactionFinishRequest> Finishes = new();
        private ulong _next = 700;
        private ulong _counter = 1;

        public string ProviderId => "FAKE";
        public string DisplayName => "Fake TSE";
        public string PreferredProduct => "Fake";
        public bool SdkAvailable => true;
        public bool ActivationAvailable => true;
        public bool TransactionAvailable => true;
        public bool ExportAvailable => true;

        public TseRuntimeStatus GetRuntimeStatus() => new(true, true, "1.0", "", "OK");

        public Task<IReadOnlyList<string>> FindSdkLibrariesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public TseRuntimeStatus ConfigureSdkLibrary(string libraryPath) => GetRuntimeStatus();

        public Task<TseProbeResult> ProbeAsync(CancellationToken ct = default) =>
            Task.FromResult(new TseProbeResult(TseConnectionState.Ready, "OK"));

        public Task<TseActivationResult> ActivateAsync(TseActivationRequest request, CancellationToken ct = default) =>
            Task.FromResult(new TseActivationResult(true, "OK"));

        public Task<TseTransactionResult> StartTransactionAsync(TseTransactionStartRequest request, CancellationToken ct = default)
        {
            var log = DateTimeOffset.UtcNow.AddSeconds(-20);
            return Task.FromResult(new TseTransactionResult(true, "OK", _next++, _counter++, new DateTimeOffset(log.Ticks - log.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero), "FAKE-SERIAL", "c3RhcnQ="));
        }

        public Task<TseTransactionResult> UpdateTransactionAsync(TseTransactionUpdateRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("Bestellung-V1 uses no UpdateTransaction (Anhang I).");

        public Task<TseTransactionResult> FinishTransactionAsync(TseTransactionFinishRequest request, CancellationToken ct = default)
        {
            Finishes.Add(request);
            return Task.FromResult(new TseTransactionResult(true, "OK", request.TransactionNumber, _counter++, DateTimeOffset.UtcNow, "FAKE-SERIAL", "ZmluaXNo"));
        }

        public Task<TseExportResult> ExportTarAsync(string targetPath, CancellationToken ct = default) =>
            Task.FromResult(new TseExportResult(false, "Fake export"));
    }
}
