using System.Text;
using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

// R147: a training receipt shares the Abrechnungskreis of the training order it paid.
//
// DSFinV-K 2.7.1: with secured orders "ist sicherzustellen, dass das Feld
// ABRECHNUNGSKREIS in der Datei Bonkopf_AbrKreis … ein Kriterium … enthält über
// das ein inhaltlicher Zusammenhang hergestellt werden kann". A real receipt is
// linked through the order it was cashed from (R137); since R142 training orders
// are secured and exported too, but a training receipt had no link to its order.
public static class R147ReviewTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "r147-training-order-link");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r147.db"));
        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='training_receipt_orders';";
            assert(Convert.ToInt64(await q.ExecuteScalarAsync()) == 1 && SchemaMigrationService.TargetSchemaVersion >= 19,
                "R147 schema migration V19 adds the link between a training receipt and its training order");
        }

        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["company.name"] = "R147 Imbiss",
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
        var orders = new OrderFiscalSigningService(tse, settings, parked) { Vorgaenge = vorgaenge, Bestellungen = new OrderBestellungRepository(db) };

        // a secured training order, paid as training receipt TR-1; TR-2 without an order
        await vorgaenge.StartAsync("training-order-147", training: true, DateTimeOffset.Now.AddMinutes(-2), "azubi");
        var order = await parked.ParkAsync(new[] { new CartLine { ProductId = 2, ProductName = "Ayran", Quantity = 2, UnitPriceCents = 250, VatRate = 7m } }, 0, "azubi", assignPickupNumber: true, training: true);
        await orders.SignInVorgangAsync(order, "training-order-147", DateTimeOffset.Now.AddMinutes(-2), "azubi");

        long linked, unlinked;
        var updateRefused = false;
        await using (var c = db.OpenConnection())
        {
            async Task<long> TrainingReceiptAsync(SqliteTransaction tx, long number)
            {
                await using var q = c.CreateCommand();
                q.Transaction = tx;
                q.CommandText = "INSERT INTO training_receipts(training_number,created_at,operator_name,payment_method,discount_cents,total_cents,cash_portion_cents,card_portion_cents,im_haus) VALUES($n,$at,'azubi','CASH',0,500,500,0,0); SELECT last_insert_rowid();";
                q.Parameters.AddWithValue("$n", number);
                q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
                var id = Convert.ToInt64(await q.ExecuteScalarAsync());
                q.Parameters.Clear();
                q.CommandText = "INSERT INTO training_receipt_items(training_id,product_id,product_name,variant_name,barcode,quantity,unit_price_cents,vat_rate,pfand_cents,line_total_cents,list_unit_price_cents,promotion_id,promotion_name,promotion_percent,promotion_discount_unit_cents) VALUES($t,2,'Ayran','','',2,250,7,0,500,250,0,'',0,0);";
                q.Parameters.AddWithValue("$t", id);
                await q.ExecuteNonQueryAsync();
                return id;
            }

            await using (var tx = (SqliteTransaction)await c.BeginTransactionAsync())
            {
                linked = await TrainingReceiptAsync(tx, 1);
                await TrainingReceiptRepository.LinkOrderAsync(c, tx, linked, order.Id);
                unlinked = await TrainingReceiptAsync(tx, 2);
                await TrainingReceiptRepository.LinkOrderAsync(c, tx, unlinked, null);
                await tx.CommitAsync();
            }

            try
            {
                await using var q = c.CreateCommand();
                q.CommandText = "UPDATE training_receipt_orders SET parked_receipt_id=0;";
                await q.ExecuteNonQueryAsync();
            }
            catch (SqliteException) { updateRefused = true; }

            await using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT COUNT(*) FROM training_receipt_orders;";
                assert(Convert.ToInt64(await q.ExecuteScalarAsync()) == 1 && updateRefused,
                    "R147 a training receipt paid from a training order keeps that order, immutable; one without an order has no link");
            }

            await using (var q = c.CreateCommand())
            {
                q.CommandText = "UPDATE parked_receipts SET status='SIMULATED' WHERE id=$id;";
                q.Parameters.AddWithValue("$id", order.Id);
                await q.ExecuteNonQueryAsync();
            }
        }

        await Task.Delay(15);
        await new BusinessManagementService(db, settings, audit).CreateZArchiveAsync("chef", "TEST");
        var folder = await new DsfinvkExportService(db, settings).ExportAsync(DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddMinutes(1), Path.Combine(dir, "out"));
        var groups = Csv(Path.Combine(folder, "allocation_groups.csv"));
        var orderGroup = groups.Single(g => g["BON_ID"] == $"BE-{order.ParkNumber}-1")["ABRECHNUNGSKREIS"];
        assert(groups.Single(g => g["BON_ID"] == "TR-1")["ABRECHNUNGSKREIS"] == orderGroup &&
               orderGroup == DsfinvkClosingBuilder.OrderAllocationGroup(order) &&
               groups.All(g => g["BON_ID"] != "TR-2"),
            "R147 the training receipt carries the Abrechnungskreis of its training order records (DSFinV-K 2.7.1); a training receipt without an order carries none");
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
