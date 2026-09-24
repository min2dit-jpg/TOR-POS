using System.Text.Json;
using TorPos.Core;
using TorPos.Infrastructure;

// R143: positions cancelled during capture are part of the receipt's
// Einzelaufzeichnung.
//
// DSFinV-K 4.2.3: "Vorzunehmende Stornierungen auf Positionsebene finden im
// Bereich der Bonpos statt" - either P_STORNO = 1 on the position or "ein
// zusätzlicher Positionsdatensatz …, bei dem MENGE mit negiertem Vorzeichen
// dargestellt wird". AEAO zu § 146a Nr. 1.11.1 names the Sofort-Stornierung among
// the Vorgänge to document. Until R143 a SOFORT STORNO only reached the action
// log, a lowered quantity (-1, MENGE ×) left no trace at all, and the receipt
// showed only what was paid.
public static class R143ReviewTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        CartLine L(long id, string name, decimal qty, long price, decimal vat) =>
            new() { ProductId = id, ProductName = name, Quantity = qty, UnitPriceCents = price, VatRate = vat };

        // ---------- the tracker collects what is cancelled ----------
        var tracker = new TseVorgangCartTracker();
        var now = DateTimeOffset.Now;
        tracker.OnCartChanged(new[] { L(1, "Döner", 3, 700, 7m), L(2, "Cola", 1, 250, 19m) }, 0, imHaus: true, fiscal: true, now);
        tracker.OnCartChanged(new[] { L(1, "Döner", 1, 700, 7m), L(2, "Cola", 1, 250, 19m) }, 0, imHaus: true, fiscal: true, now);
        tracker.OnCartChanged(new[] { L(1, "Döner", 1, 700, 7m) }, 0, imHaus: true, fiscal: true, now);
        tracker.OnCartChanged(new[] { L(1, "Döner", 1, 700, 7m), L(3, "Ayran", 1, 250, 7m) }, 0, imHaus: true, fiscal: true, now);
        var cancelled = tracker.CancelledLines.ToList();
        tracker.Release();
        assert(cancelled.Count == 2 &&
               cancelled[0] is { ProductName: "Döner", Quantity: 2, VatRate: 7m } &&
               cancelled[1] is { ProductName: "Cola", Quantity: 1 } &&
               tracker.CancelledLines.Count == 0,
            "R143/R2026 lowered quantities and removed lines keep the current effective VAT snapshot; food remains 7% while adding is no cancellation");

        var abortTracker = new TseVorgangCartTracker();
        abortTracker.OnCartChanged(new[] { L(1, "Döner", 2, 700, 7m) }, 0, false, true, now);
        abortTracker.OnCartChanged(new[] { L(1, "Döner", 1, 700, 7m) }, 0, false, true, now);
        var abort = abortTracker.OnCartChanged(Array.Empty<CartLine>(), 0, false, true, now);
        var pairs = TseVorgangCartTracker.CancellationPairs(cancelled);
        assert(abort.Kind == TseVorgangActionKind.Abort && abort.CancelledLines!.Single().Quantity == 1 && abort.Lines.Single().Quantity == 1 &&
               pairs.Count == 4 && pairs.Sum(p => p.LineTotalCents) == 0 && pairs[0].Quantity == 2 && pairs[1].Quantity == -2,
            "R143 an abort carries the positions cancelled before it; each cancelled position becomes +quantity/-quantity, adding up to nothing (DSFinV-K 4.2.3)");

        var snapshot = new CheckoutSnapshot("op-143", new[] { L(1, "Döner", 1, 700, 7m) }, 0, PaymentMethod.Cash, "kasse1", null,
            CancelledLines: CheckoutSnapshot.CopyLines(cancelled));
        var roundTrip = JsonSerializer.Deserialize<CheckoutSnapshot>(JsonSerializer.Serialize(snapshot))!;
        assert(JsonSerializer.Serialize(roundTrip) == JsonSerializer.Serialize(snapshot) && roundTrip.CancelledLines!.Length == 2,
            "R143 the cancelled positions travel with the payment journal and survive a restart");

        // ---------- stored with the receipt and exported ----------
        var dir = Path.Combine(root, "r143-cancelled-positions");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r143.db"));
        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["company.name"] = "R143 Imbiss",
            ["company.street"] = "Hauptstraße 1",
            ["company.zip"] = "10115",
            ["company.city"] = "Berlin",
            ["company.tax_no"] = "27/123/45678",
        });

        long saleId;
        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,cash_portion_cents,im_haus) VALUES(143001,$at,'CASH',700,700,'TEST_FIXTURE','SALE',700,0); SELECT last_insert_rowid();";
            q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
            saleId = Convert.ToInt64(await q.ExecuteScalarAsync());
            q.CommandText = "INSERT INTO sale_items(sale_id,product_id,product_name,quantity,unit_price_cents,vat_rate,line_total_cents) VALUES($s,1,'Döner',1,700,7,700);";
            q.Parameters.AddWithValue("$s", saleId);
            await q.ExecuteNonQueryAsync();
            q.CommandText = """
                INSERT INTO sale_cancelled_items(sale_id,product_id,product_name,variant_name,barcode,quantity,unit_price_cents,vat_rate,pfand_cents,list_unit_price_cents,promotion_id,promotion_name,promotion_percent,promotion_discount_unit_cents)
                VALUES($s,1,'Döner','','',2,700,7,0,700,0,'',0,0),($s,2,'Cola','','',1,250,19,0,250,0,'',0,0);
                """;
            await q.ExecuteNonQueryAsync();

            q.CommandText = "INSERT INTO training_receipts(training_number,created_at,operator_name,payment_method,discount_cents,total_cents,cash_portion_cents,card_portion_cents,im_haus) VALUES(1,$at,'azubi','CASH',0,250,250,0,0); SELECT last_insert_rowid();";
            var trainingId = Convert.ToInt64(await q.ExecuteScalarAsync());
            q.CommandText = "INSERT INTO training_receipt_items(training_id,product_id,product_name,variant_name,barcode,quantity,unit_price_cents,vat_rate,pfand_cents,line_total_cents,list_unit_price_cents,promotion_id,promotion_name,promotion_percent,promotion_discount_unit_cents) VALUES($t,3,'Ayran','','',1,250,7,0,250,250,0,'',0,0);";
            q.Parameters.AddWithValue("$t", trainingId);
            await q.ExecuteNonQueryAsync();
            q.CommandText = "INSERT INTO training_cancelled_items(training_id,product_id,product_name,variant_name,barcode,quantity,unit_price_cents,vat_rate,pfand_cents,list_unit_price_cents,promotion_id,promotion_name,promotion_percent,promotion_discount_unit_cents) VALUES($t,3,'Ayran','','',1,250,7,0,250,0,'',0,0);";
            await q.ExecuteNonQueryAsync();
        }

        var sale = (await new SaleRepository(db).GetByIdAsync(saleId))!;
        var updateRefused = false;
        try
        {
            await using var c = db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = "UPDATE sale_cancelled_items SET quantity=0;";
            await q.ExecuteNonQueryAsync();
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { updateRefused = true; }
        assert(sale.CancelledLines.Count == 2 && sale.CancelledLines[0].Quantity == 2 && updateRefused,
            "R143 the cancelled positions are stored with the receipt, immutable, and read back with it");

        await Task.Delay(15);
        await new BusinessManagementService(db, settings, audit).CreateZArchiveAsync("chef", "TEST");
        var folder = await new DsfinvkExportService(db, settings).ExportAsync(DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddMinutes(1), Path.Combine(dir, "out"));
        var positions = Table(Path.Combine(folder, "lines.csv"));
        var salePositions = positions.Where(p => p["BON_ID"] == "\"143001\"").ToList();
        var head = Table(Path.Combine(folder, "transactions.csv")).Single(h => h["BON_ID"] == "\"143001\"");
        var cases = File.ReadAllLines(Path.Combine(folder, "businesscases.csv")).Skip(1).ToArray();
        assert(salePositions.Count == 5 &&
               salePositions.Select(p => p["MENGE"]).SequenceEqual(new[] { "1,000", "2,000", "-2,000", "1,000", "-1,000" }) &&
               salePositions.All(p => p["P_STORNO"] == "\"0\"" && p["GV_TYP"] == "\"Umsatz\"") &&
               head["UMS_BRUTTO"] == "7,00" &&
               cases.Length == 1 && cases[0].Contains(";7,00;"),
            "R143 the receipt lists its position and the cancelled ones as captured/negated pairs; its total and the closing are unchanged");

        var trainingPositions = positions.Where(p => p["BON_ID"] == "\"TR-1\"").ToList();
        assert(trainingPositions.Select(p => p["MENGE"]).SequenceEqual(new[] { "1,000", "1,000", "-1,000" }),
            "R143 a training receipt documents its cancelled positions the same way");

        // ---------- an abort keeps them ----------
        var vorgaenge = new TseVorgangService(db, new TseFailSafeService(new NoTse(), new TseOutageRepository(db, audit), audit), settings);
        await vorgaenge.StartAsync("abort-143", false, DateTimeOffset.Now, "kasse1");
        await vorgaenge.AbortAsync("abort-143", abort.Lines.Concat(TseVorgangCartTracker.CancellationPairs(abort.CancelledLines)).ToArray(), 0, "kasse1", "kasse1");
        long items, total;
        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "SELECT (SELECT COUNT(*) FROM aborted_vorgang_items i JOIN aborted_vorgaenge a ON a.id=i.aborted_id WHERE a.vorgang_id='abort-143'), (SELECT total_cents FROM aborted_vorgaenge WHERE vorgang_id='abort-143');";
            await using var r = await q.ExecuteReaderAsync();
            await r.ReadAsync();
            items = r.GetInt64(0);
            total = r.GetInt64(1);
        }
        assert(items == 3 && total == 700,
            "R143 an aborted Vorgang keeps the positions cancelled before the abort, without changing its total");
    }

    private static List<Dictionary<string, string>> Table(string path)
    {
        var lines = File.ReadAllLines(path);
        var header = lines[0].Split(';');
        return lines.Skip(1).Select(l => l.Split(';'))
            .Select(cells => header.Select((name, i) => (name, value: i < cells.Length ? cells[i] : "")).ToDictionary(x => x.name, x => x.value))
            .ToList();
    }

    private sealed class NoTse : ITseProvider
    {
        public string ProviderId => "NONE";
        public string DisplayName => "No TSE";
        public string PreferredProduct => "None";
        public bool SdkAvailable => false;
        public bool ActivationAvailable => false;
        public bool TransactionAvailable => false;
        public bool ExportAvailable => false;
        public TseRuntimeStatus GetRuntimeStatus() => new(false, false, "", "", "none");
        public Task<IReadOnlyList<string>> FindSdkLibrariesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public TseRuntimeStatus ConfigureSdkLibrary(string libraryPath) => GetRuntimeStatus();
        public Task<TseProbeResult> ProbeAsync(CancellationToken ct = default) => Task.FromResult(new TseProbeResult(TseConnectionState.Ready, "none"));
        public Task<TseActivationResult> ActivateAsync(TseActivationRequest request, CancellationToken ct = default) => Task.FromResult(new TseActivationResult(false, "none"));
        public Task<TseTransactionResult> StartTransactionAsync(TseTransactionStartRequest request, CancellationToken ct = default) => Task.FromResult(new TseTransactionResult(false, "none"));
        public Task<TseTransactionResult> UpdateTransactionAsync(TseTransactionUpdateRequest request, CancellationToken ct = default) => Task.FromResult(new TseTransactionResult(false, "none"));
        public Task<TseTransactionResult> FinishTransactionAsync(TseTransactionFinishRequest request, CancellationToken ct = default) => Task.FromResult(new TseTransactionResult(false, "none"));
        public Task<TseExportResult> ExportTarAsync(string targetPath, CancellationToken ct = default) => Task.FromResult(new TseExportResult(false, "none"));
    }
}
