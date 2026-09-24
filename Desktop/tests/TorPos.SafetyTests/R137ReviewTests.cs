using System.Text;
using TorPos.Core;
using TorPos.Infrastructure;

// R137: changes and cancellations of an accepted order are secured on their own.
//
// DSFinV-K 4.2.3: orders are Vorgänge of their own; once signed, nothing is
// changed - "Im Falle einer Stornierung einer ganzen Bestellung ... muss für
// eine Stornierung ein neuer Datensatz mit umgekehrtem Vorzeichen erzeugt
// werden, der wiederum abgesichert werden muss". 2.7.2: with secured orders the
// Kassenbeleg may start at payment, if the start of the first order transaction
// is printed on the receipt. Until R137 only the acceptance was signed, a test
// till signed orders too, and the export showed an order as last saved.
public static class R137ReviewTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "r137-bestellungen");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r137.db"));

        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('order_bestellungen','order_bestellung_items');";
            assert(Convert.ToInt64(await q.ExecuteScalarAsync()) == 2 && SchemaMigrationService.TargetSchemaVersion >= 17,
                "R137 schema migration V17 adds the immutable order records");
        }

        // ---------- what a change secures ----------
        CartLine L(long id, string name, decimal qty, long price, decimal vat) =>
            new() { ProductId = id, ProductName = name, Quantity = qty, UnitPriceCents = price, VatRate = vat };
        var accepted = new[] { L(1, "Döner", 1, 700, 7m), L(2, "Cola", 1, 250, 19m), L(1, "Döner", 1, 700, 7m) };
        var annahme = OrderBestellungDelta.Compute(Array.Empty<CartLine>(), accepted);
        var secured = OrderBestellungDelta.Net(annahme);
        var changed = new[] { L(1, "Döner", 3, 700, 7m), L(3, "Ayran", 2, 250, 7m) };
        var change = OrderBestellungDelta.Compute(secured, changed);
        var afterChange = OrderBestellungDelta.Net(annahme.Concat(change));
        var storno = OrderBestellungDelta.Reverse(afterChange);
        assert(annahme.Count == 3 &&
               change.Count == 3 && change[0].ProductName == "Döner" && change[0].Quantity == 1 &&
               change[1].ProductName == "Ayran" && change[1].Quantity == 2 && change[2].ProductName == "Cola" && change[2].Quantity == -1 &&
               afterChange.Sum(l => l.LineTotalCents) == changed.Sum(l => l.LineTotalCents) &&
               OrderBestellungDelta.Net(annahme.Concat(change).Concat(storno)).Count == 0,
            "R137 an acceptance secures the positions as they are, a change only the difference (+ added, - removed), a cancellation reverses everything; the records add up to the order");

        var imHausSwitch = OrderBestellungDelta.Compute(
            CheckoutSnapshot.CopyLines(new[] { L(1, "Döner", 1, 700, 7m) }, imHaus: false),
            CheckoutSnapshot.CopyLines(new[] { L(1, "Döner", 1, 700, 7m) }, imHaus: true));
        assert(imHausSwitch.Count == 0 &&
               FiscalProcessData.BestellungText(new[] { L(2, "Cola \"Zero\"", -1, 250, 19m) }) == "-1;\"Cola \"\"Zero\"\"\";2.50",
            "R137/R2026 switching Im Haus alone creates no food-VAT delta; Anhang I Bestellung-V1 still carries negative quantities correctly");

        // ---------- a recalled order starts no Vorgang until it changes ----------
        var tracker = new TseVorgangCartTracker();
        var recalled = new List<CartLine> { L(1, "Döner", 1, 700, 7m) };
        tracker.SetBaseline(recalled, 0);
        var viewed = tracker.OnCartChanged(recalled, 0, false, true, DateTimeOffset.Now);
        recalled.Add(L(2, "Cola", 1, 250, 19m));
        var firstChange = tracker.OnCartChanged(recalled, 0, false, true, DateTimeOffset.Now);
        tracker.Release();
        tracker.SetBaseline(recalled, 0);
        var emptied = tracker.OnCartChanged(Array.Empty<CartLine>(), 0, false, true, DateTimeOffset.Now);
        var fresh = tracker.OnCartChanged(recalled, 0, false, true, DateTimeOffset.Now);
        assert(viewed.Kind == TseVorgangActionKind.None && firstChange.Kind == TseVorgangActionKind.Start &&
               emptied.Kind == TseVorgangActionKind.None && fresh.Kind == TseVorgangActionKind.Start,
            "R137 looking at a recalled order starts nothing (DSFinV-K 2.7.2); its first change starts the Vorgang, and the baseline is gone once the cart is emptied");

        // ---------- the real flow against a recording TSE ----------
        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["company.name"] = "R137 Imbiss",
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

        // order A: accepted, changed, cancelled
        var begin = DateTimeOffset.Now.AddMinutes(-5);
        await vorgaenge.StartAsync("order-a", false, begin, "kasse1");
        var orderA = await parked.ParkAsync(new[] { L(1, "Döner", 2, 700, 7m), L(2, "Cola", 1, 250, 19m) }, 0, "kasse1", assignPickupNumber: true);
        await signing.SignInVorgangAsync(orderA, "order-a", begin, "kasse1");
        var storedA = await parked.GetOpenByIdAsync(orderA.Id);
        assert(provider.Finishes.Count == 1 && provider.Finishes[0].ProcessType == "Bestellung-V1" &&
               provider.Finishes[0].TransactionNumber == provider.StartNumbers[0] &&
               Encoding.UTF8.GetString(provider.Finishes[0].ProcessData) == "2;\"Döner\";7.00\r1;\"Cola\";2.50" &&
               (await records.SecuredAsync(orderA.Id)).Count == 1 &&
               storedA is { TseTransactionNumber.Length: > 0 } && storedA.VorgangStartedAt == begin,
            "R137 the acceptance is the order's first record, finished on the transaction begun with its first position; the order row keeps it as before");

        var beforeChange = storedA!;
        await vorgaenge.StartAsync("order-a-change", false, DateTimeOffset.Now.AddMinutes(-1), "kasse1");
        await parked.UpdateAsync(orderA.Id, new[] { L(1, "Döner", 3, 700, 7m) }, 0);
        var changedA = (await parked.GetOpenByIdAsync(orderA.Id))!;
        var changeRecord = await signing.SecureChangeAsync(changedA, beforeChange.Lines, "order-a-change", DateTimeOffset.Now.AddMinutes(-1), "kasse1");
        assert(changeRecord is { Sequence: 2, Kind: OrderBestellungKind.Aenderung } &&
               provider.Finishes.Count == 2 && provider.Finishes[1].TransactionNumber == provider.StartNumbers[1] &&
               Encoding.UTF8.GetString(provider.Finishes[1].ProcessData) == "1;\"Döner\";7.00\r-1;\"Cola\";2.50" &&
               (await vorgaenge.GetAsync("order-a-change"))!.State == TseVorgangService.Finished,
            "R137 a change of the accepted order is its own Bestellung-V1 transaction with only the difference (DSFinV-K 4.2.3)");

        await vorgaenge.StartAsync("order-a-noop", false, DateTimeOffset.Now, "kasse1");
        var noop = await signing.SecureChangeAsync(changedA, changedA.Lines, "order-a-noop", DateTimeOffset.Now, "kasse1");
        assert(noop is null && provider.Finishes.Count(f => f.ProcessType == "Bestellung-V1") == 2 &&
               (await vorgaenge.GetAsync("order-a-noop"))!.State == TseVorgangService.Aborted,
            "R137 a change that leaves the order as it was secures nothing; the Vorgang begun for it ends as aborted");

        await parked.CancelAsync(orderA.Id);
        var stornoRecord = await signing.SecureCancellationAsync(changedA, "", null, "kasse1");
        var (countA, netA) = await records.SecuredAsync(orderA.Id);
        assert(stornoRecord is { Sequence: 3, Kind: OrderBestellungKind.Storno } && stornoRecord.Lines.Single().Quantity == -3 &&
               Encoding.UTF8.GetString(provider.Finishes[^1].ProcessData) == "-3;\"Döner\";7.00" &&
               countA == 3 && netA.Count == 0,
            "R137 a cancelled order is a new record with reversed sign, secured on its own; its records now add up to nothing");

        var updateRefused = false;
        try
        {
            await using var c = db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = "UPDATE order_bestellung_items SET quantity=0;";
            await q.ExecuteNonQueryAsync();
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { updateRefused = true; }
        assert(updateRefused, "R137 order records and their positions cannot be changed");

        // order B: Im Haus, accepted without a tracked Vorgang, paid unchanged
        var orderB = await parked.ParkAsync(new[] { L(4, "Lahmacun", 1, 500, 7m) }, 0, "kasse1", assignPickupNumber: true, imHaus: true);
        var acceptedB = await signing.SecureChangeAsync(orderB, orderB.Lines, "", null, "kasse1");
        var saleId = await InsertSaleAsync(db, 137001, 500, 7m);
        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "UPDATE parked_receipts SET status='CASHED',cashed_sale_id=$s WHERE id=$id;";
            q.Parameters.AddWithValue("$s", saleId);
            q.Parameters.AddWithValue("$id", orderB.Id);
            await q.ExecuteNonQueryAsync();
        }
        var sale = (await new SaleRepository(db).GetByIdAsync(saleId))!;
        // R145: the digital receipt is built from the print job, which carries the order start.
        var digital = DigitalReceiptDocument.From(
            new ReceiptPrintJob(sale.ReceiptNumber, sale.CreatedAt, "R137 Imbiss", "Hauptstraße 1, 10115 Berlin", "27/123/45678", "", "", "", "Bar",
                sale.DiscountCents, sale.TotalCents, sale.Lines, FiscalTestMode: false, OrderStart: sale.OrderStartedAt),
            DigitalReceiptDocument.PaymentsFor(PaymentMethod.Cash, sale.TotalCents, 0));
        assert(acceptedB is { Kind: OrderBestellungKind.Annahme } && acceptedB.Lines.Single().VatRate == 7m &&
               sale.OrderStartedAt is not null && sale.OrderStartedAt == acceptedB.Tse!.StartLogTime &&
               digital.Field(DigitalReceiptDocument.OrderStartLabel) == TseReceiptTime.Format(sale.OrderStartedAt.Value),
            "R137/R2026 the paid order carries its first transaction start and keeps food at the reduced 7% Im-Haus rate");

        // order C: signed before R137 (only on the order row), then changed
        var orderC = await parked.ParkAsync(new[] { L(5, "Pide", 1, 900, 7m) }, 0, "kasse1");
        await parked.RecordTseResultAsync(orderC.Id, SaleTseResult.SignedResult("KASSE-1", "77", "5", "FAKE-SERIAL", "b2xk", DateTimeOffset.Now.AddMinutes(-3)));
        var legacyBefore = (await parked.GetOpenByIdAsync(orderC.Id))!;
        await parked.UpdateAsync(orderC.Id, new[] { L(5, "Pide", 1, 900, 7m), L(2, "Cola", 1, 250, 19m) }, 0);
        var legacyChanged = (await parked.GetOpenByIdAsync(orderC.Id))!;
        var legacyChange = await signing.SecureChangeAsync(legacyChanged, legacyBefore.Lines, "", null, "kasse1");
        await parked.CancelAsync(orderC.Id);
        var legacyStorno = await signing.SecureCancellationAsync(legacyChanged, "", null, "kasse1");
        var (countC, netC) = await records.SecuredAsync(orderC.Id);
        assert(legacyChange is { Sequence: 2, Kind: OrderBestellungKind.Aenderung } && legacyChange.Lines.Single().ProductName == "Cola" &&
               legacyStorno is { Sequence: 3 } && legacyStorno.Lines.Count == 2 && countC == 3 && netC.Count == 0,
            "R137 an order signed before R137 gets its acceptance written down with its original signature first, so a later cancellation reverses all of it");

        // ---------- closing guard: a test till's old order signatures do not count ----------
        var guardDb = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r137-guard.db"));
        var guardParked = new ParkedReceiptRepository(guardDb);
        var testOrder = await guardParked.ParkAsync(new[] { L(1, "Döner", 1, 700, 7m) }, 0, "kasse1");
        await guardParked.RecordTseResultAsync(testOrder.Id, SaleTseResult.Outage("TSE ist nicht aktiv"));
        bool testTillOpen;
        await using (var c = guardDb.OpenConnection())
            testTillOpen = await DsfinvkMasterDataStore.HasOpenVorgaengeAsync(c, CancellationToken.None);
        await using (var c = db.OpenConnection())
            assert(!testTillOpen && await DsfinvkMasterDataStore.HasOpenVorgaengeAsync(c, CancellationToken.None),
                "R137 an order a test till signed before R137 (not paid with a real sale) no longer forces a closing; secured order records do");

        // ---------- export ----------
        await Task.Delay(15);
        await new BusinessManagementService(db, settings, audit).CreateZArchiveAsync("chef", "TEST");
        var exporter = new DsfinvkExportService(db, settings);
        var from = DateTimeOffset.Now.AddHours(-1);
        var to = DateTimeOffset.Now.AddMinutes(1);
        var report = await exporter.ValidateAsync(from, to);
        var folder = await exporter.ExportAsync(from, to, Path.Combine(dir, "out"));
        var heads = Csv(Path.Combine(folder, "transactions.csv"));
        var tseRows = Csv(Path.Combine(folder, "transactions_tse.csv"));
        var references = Csv(Path.Combine(folder, "references.csv"));
        var groups = Csv(Path.Combine(folder, "allocation_groups.csv"));
        var headVat = Csv(Path.Combine(folder, "transactions_vat.csv"));
        var cases = File.ReadAllLines(Path.Combine(folder, "businesscases.csv")).Skip(1).ToArray();
        var a = orderA.ParkNumber;

        var head = heads.ToDictionary(h => h["BON_ID"]);
        assert(head.TryGetValue($"BE-{a}-1", out var a1) && a1["BON_NAME"] == "Bestellung" && a1["BON_STORNO"] == "0" && a1["UMS_BRUTTO"] == "16,50" &&
               head.TryGetValue($"BE-{a}-2", out var a2) && a2["BON_NAME"] == "Bestelländerung" && a2["UMS_BRUTTO"] == "4,50" &&
               head.TryGetValue($"BE-{a}-3", out var a3) && a3["BON_NAME"] == "Bestellstorno" && a3["BON_STORNO"] == "1" && a3["UMS_BRUTTO"] == "-21,00" &&
               heads.All(h => h["BON_TYP"] != "AVBestellung" || h["BON_ID"].Count(ch => ch == '-') == 2),
            "R137 every order record is its own AVBestellung - acceptance, change, cancellation with BON_STORNO 1 and reversed amount");
        assert(references.Any(r => r["BON_ID"] == $"BE-{a}-3" && r["REF_TYP"] == "Transaktion" && r["REF_BON_ID"] == $"BE-{a}-1") &&
               tseRows.Single(t => t["BON_ID"] == $"BE-{a}-3")["TSE_VORGANGSDATEN"] == "-3;\"Döner\";7.00" &&
               tseRows.Single(t => t["BON_ID"] == $"BE-{a}-2")["TSE_TA_VORGANGSART"] == "Bestellung-V1",
            "R137 the cancellation refers to the acceptance and each record carries its own Bestellung-V1 transaction");
        var b1 = $"BE-{orderB.ParkNumber}-1";
        assert(groups.Single(g => g["BON_ID"] == b1)["ABRECHNUNGSKREIS"] == groups.Single(g => g["BON_ID"] == "137001")["ABRECHNUNGSKREIS"] &&
               headVat.Single(v => v["BON_ID"] == b1)["UST_SCHLUESSEL"] == "2" &&
               cases.Length == 1 && cases[0].Contains(";5,00;"),
            "R137/R2026 order records and the receipt share the Abrechnungskreis (2.7.1), food uses the reduced VAT key, and orders do not touch the closing totals");
        assert(report.Ready && report.Issues.All(x => x.Code is not ("BESTELLUNG" or "BESTELLSTORNO")),
            "R137 orders recorded under R137 raise no hint about unsecured changes or cancellations");
    }

    private static async Task<long> InsertSaleAsync(SqliteDatabase db, long receipt, long cents, decimal vat)
    {
        await Task.Delay(15);
        await using var c = db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = "INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,cash_portion_cents,im_haus,started_at) VALUES($r,$at,'CASH',$t,$t,'TEST_FIXTURE','SALE',$t,1,$at); SELECT last_insert_rowid();";
        q.Parameters.AddWithValue("$r", receipt);
        q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        q.Parameters.AddWithValue("$t", cents);
        var id = Convert.ToInt64(await q.ExecuteScalarAsync());
        await using var item = c.CreateCommand();
        item.CommandText = "INSERT INTO sale_items(sale_id,product_id,product_name,quantity,unit_price_cents,vat_rate,line_total_cents) VALUES($s,4,'Lahmacun',1,$t,$v,$t);";
        item.Parameters.AddWithValue("$s", id);
        item.Parameters.AddWithValue("$t", cents);
        item.Parameters.AddWithValue("$v", (double)vat);
        await item.ExecuteNonQueryAsync();
        return id;
    }

    /// <summary>Reads a DSFinV-K file: quoted fields may contain ";" and doubled quotes.</summary>
    private static List<Dictionary<string, string>> Csv(string path)
    {
        var records = File.ReadAllText(path, Encoding.UTF8).Split("\r\n").Where(r => r.Length > 0).ToList();
        var header = Fields(records[0]);
        return records.Skip(1)
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
        public readonly List<TseTransactionStartRequest> Starts = new();
        public readonly List<ulong> StartNumbers = new();
        public readonly List<TseTransactionFinishRequest> Finishes = new();
        private ulong _next = 500;
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
            Starts.Add(request);
            var number = _next++;
            StartNumbers.Add(number);
            var log = DateTimeOffset.UtcNow.AddSeconds(-20);
            return Task.FromResult(new TseTransactionResult(true, "OK", number, _counter++, new DateTimeOffset(log.Ticks - log.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero), "FAKE-SERIAL", "c3RhcnQ="));
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
