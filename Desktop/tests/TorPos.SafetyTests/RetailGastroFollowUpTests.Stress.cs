using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

// Einzelhandel/Gastro load and input robustness. The fiscal production gate
// stays closed, so new sales cannot be booked here; what is stressed is what
// protects against double payment and double fiscalization on a busy till:
// the payment journal, the commit retry path, SQLite locking, a concurrent
// DSFinV-K export and a slow TSE - plus seeded barcode and scale input.
public static partial class RetailGastroFollowUpTests
{
    private static CheckoutSnapshot Snapshot(string id, PaymentMethod method = PaymentMethod.Card) =>
        new(id, new[] { new CartLine { ProductId = 1, ProductName = "Artikel", Quantity = 1, UnitPriceCents = 2000, VatRate = 19m } },
            0, method, "kasse", null);

    private static async Task<int> CountAsync(SqliteDatabase db, string sql)
    {
        await using var c = db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = sql;
        return Convert.ToInt32(await q.ExecuteScalarAsync());
    }

    internal static async Task Stress(string dir, Action<bool, string> assert)
    {
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "stress.db"));
        var journal = new CheckoutJournal(db);
        var sales = new SaleRepository(db);

        // 1. A double-tapped PAY button / two terminals racing for the same till:
        //    the same operation begun 60 times in parallel exists once, and no
        //    second payment can open while it is unresolved.
        var first = Snapshot("stress-op-1");
        var begins = await Task.WhenAll(Enumerable.Range(0, 60).Select(async _ =>
        {
            try { await journal.BeginAsync(first); return true; }
            catch { return false; }
        }));
        var others = await Task.WhenAll(Enumerable.Range(0, 30).Select(async i =>
        {
            try { await journal.BeginAsync(Snapshot($"stress-other-{i}")); return true; }
            catch { return false; }
        }));
        assert(
            begins.Count(x => x) == 1 && others.All(x => !x) &&
            await CountAsync(db, "SELECT COUNT(*) FROM checkout_operations;") == 1,
            "Stress: 60 parallel starts of one payment create one payment journal entry, and 30 competing payments cannot open while it is unresolved");

        // 2. Retry storm after a crash: the committed operation is replayed 80
        //    times in parallel (with SQLite held busy by another writer for part
        //    of it) and a DSFinV-K export runs at the same time. Every replay
        //    returns the one original sale; nothing is booked or signed twice.
        long saleId;
        await using (var c = db.OpenConnection())
        await using (var tx = (SqliteTransaction)await c.BeginTransactionAsync())
        {
            await using var q = c.CreateCommand();
            q.Transaction = tx;
            q.CommandText = "INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status) VALUES(7001,$now,'CARD',2000,2000,'TEST_FIXTURE'); SELECT last_insert_rowid();";
            q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
            saleId = Convert.ToInt64(await q.ExecuteScalarAsync());
            q.Parameters.Clear();
            q.CommandText = "UPDATE checkout_operations SET state='COMMITTED',sale_id=$sale WHERE id=$id;";
            q.Parameters.AddWithValue("$sale", saleId);
            q.Parameters.AddWithValue("$id", first.OperationId);
            await q.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }

        var lockHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busyWriter = Task.Run(async () =>
        {
            await using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db.DatabasePath }.ToString());
            await c.OpenAsync();
            await using (var q = c.CreateCommand())
            {
                q.CommandText = "PRAGMA busy_timeout=3000; BEGIN IMMEDIATE; INSERT INTO app_settings(key,value) VALUES('stress.lock','1') ON CONFLICT(key) DO UPDATE SET value='1';";
                await q.ExecuteNonQueryAsync();
            }
            lockHeld.TrySetResult();
            await Task.Delay(700);
            await using (var q = c.CreateCommand())
            {
                q.CommandText = "COMMIT;";
                await q.ExecuteNonQueryAsync();
            }
        });
        await lockHeld.Task;

        var export = new DsfinvkExportService(db, new SettingsRepository(db));
        var today = DateOnly.FromDateTime(DateTime.Now);
        var range = DsfinvkExportRange.ForDates(today.AddDays(-1), today);
        var exportTask = export.ValidateAsync(range.FromInclusive, range.ToInclusive);
        var replays = await Task.WhenAll(Enumerable.Range(0, 80).Select(async _ =>
        {
            try { return (await sales.CommitAsync(first)).Id; }
            catch { return -1L; }
        }));
        await busyWriter;
        var exportReport = await exportTask;
        assert(
            replays.All(id => id == saleId) &&
            await CountAsync(db, "SELECT COUNT(*) FROM sales;") == 1 &&
            await CountAsync(db, "SELECT COUNT(*) FROM checkout_operations WHERE state='COMMITTED';") == 1 &&
            exportReport.Issues.Count > 0,
            "Stress: 80 parallel replays of a committed payment - while SQLite is held busy and a DSFinV-K export runs - all return the one original sale; nothing is booked twice");

        // 3. A slow TSE under parallel load: every transaction either gets its
        //    own TSE transaction number or is an outage - never a shared or
        //    fabricated one.
        var tse = new SlowTse();
        var audit = new AuditLogRepository(db);
        var failSafe = new TseFailSafeService(tse, new TseOutageRepository(db, audit), audit);
        var request = new TseTransactionStartRequest("CLIENT", Array.Empty<byte>(), "Kassenbeleg-V1");
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => failSafe.StartTransactionAsync(request, "kasse")));
        var numbers = results.Where(r => r.Result.Success).Select(r => r.Result.TransactionNumber).ToList();
        assert(
            results.All(r => r.Result.Success != r.TseOutage) &&
            numbers.Count == numbers.Distinct().Count() &&
            results.Where(r => !r.Result.Success).All(r => r.Result.TransactionNumber == 0),
            "Stress: 40 parallel transactions against a slow, intermittently failing TSE each get a unique TSE number or a documented outage - never both, never shared");
    }

    private sealed class SlowTse : ITseProvider
    {
        private long _next;
        public string ProviderId => "FAKE";
        public string DisplayName => "Slow TSE";
        public string PreferredProduct => "Fake";
        public bool SdkAvailable => true;
        public bool ActivationAvailable => true;
        public bool TransactionAvailable => true;
        public bool ExportAvailable => true;
        public TseRuntimeStatus GetRuntimeStatus() => new(true, true, "1.0", "", "OK");
        public Task<IReadOnlyList<string>> FindSdkLibrariesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public TseRuntimeStatus ConfigureSdkLibrary(string libraryPath) => GetRuntimeStatus();

        public async Task<TseProbeResult> ProbeAsync(CancellationToken ct = default)
        {
            await Task.Delay(5, ct);
            return new TseProbeResult(TseConnectionState.Ready, "OK",
                new TseDeviceInfo("Fake", "Fake", "", "", "SER-1", "", null, "", CertificateExpiresAtUtc: DateTimeOffset.UtcNow.AddYears(2)));
        }

        public Task<TseActivationResult> ActivateAsync(TseActivationRequest request, CancellationToken ct = default) =>
            Task.FromResult(new TseActivationResult(true, "OK"));

        public async Task<TseTransactionResult> StartTransactionAsync(TseTransactionStartRequest request, CancellationToken ct = default)
        {
            await Task.Delay(Random.Shared.Next(1, 25), ct);
            var number = Interlocked.Increment(ref _next);
            if (number % 7 == 0)
                throw new TimeoutException("TSE antwortet nicht");
            if (number % 5 == 0)
                return new TseTransactionResult(false, "TSE belegt");
            return new TseTransactionResult(true, "OK", (ulong)number, (ulong)number, DateTimeOffset.UtcNow, "SER-1", "c2ln");
        }

        public Task<TseTransactionResult> UpdateTransactionAsync(TseTransactionUpdateRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<TseTransactionResult> FinishTransactionAsync(TseTransactionFinishRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<TseExportResult> ExportTarAsync(string targetPath, CancellationToken ct = default) =>
            Task.FromResult(new TseExportResult(true, "OK", targetPath));
    }

    internal static void BarcodeAndScale(Action<bool, string> assert)
    {
        const int seed = 20260927;
        var random = new Random(seed);
        var failure = "";
        var accepted = 0;
        for (var i = 0; i < 5000 && failure.Length == 0; i++)
        {
            var digits = string.Concat(Enumerable.Range(0, 12).Select(_ => (char)('0' + random.Next(10))));
            var check = Ean13Check(digits);
            var valid = digits + check;
            string candidate = random.Next(5) switch
            {
                0 => valid,
                1 => digits,
                2 => MutateOneDigit(valid, random),
                3 => " " + valid.Insert(random.Next(valid.Length), "-") + "\r",
                _ => new string(Enumerable.Range(0, random.Next(20)).Select(_ => (char)random.Next(0x20, 0x3000)).ToArray())
            };

            try
            {
                var ok = SimplePdfWriter.TryBuildEan13(candidate, out var modules);
                var asciiDigits = new string(candidate.Where(ch => ch is >= '0' and <= '9').ToArray());
                var expected = asciiDigits.Length == candidate.Count(char.IsDigit) &&
                               (asciiDigits.Length == 12 || (asciiDigits.Length == 13 && Ean13Check(asciiDigits[..12]) == asciiDigits[12]));
                if (ok != expected)
                    failure = $"iteration {i}: '{candidate}' accepted={ok}, expected={expected}";
                else if (ok && (modules.Length != 95 || !modules.StartsWith("101") || modules.Substring(45, 5) != "01010" || !modules.EndsWith("101")))
                    failure = $"iteration {i}: malformed bars";
                if (ok) accepted++;
            }
            catch (Exception ex)
            {
                failure = $"iteration {i}: {ex.GetType().Name}";
            }
        }

        assert(failure.Length == 0 && accepted > 1000,
            $"Property (seed {seed}): 5000 scanned/typed EAN inputs - a single wrong digit is always rejected, separators and Unicode never crash the label EAN-13 builder {failure}");

        var scaleFailure = "";
        for (var i = 0; i < 5000 && scaleFailure.Length == 0; i++)
        {
            var grams = random.Next(1, 9_999_000);
            var price = random.NextInt64(0, 10_000_00);
            try
            {
                var fromGrams = WeightedSales.ToKilograms(grams, WeightInputUnit.Gram);
                var fromKilograms = WeightedSales.ToKilograms(grams / 1000m, WeightInputUnit.Kilogram);
                var total = WeightedSales.TotalCents(price, fromGrams);
                var more = WeightedSales.TotalCents(price, fromGrams + 0.001m);
                if (fromGrams != fromKilograms || total < 0 || more < total ||
                    Math.Abs(total - fromGrams * price) > 0.5m)
                    scaleFailure = $"iteration {i}: {grams} g × {price}";
            }
            catch (Exception ex)
            {
                scaleFailure = $"iteration {i}: {ex.GetType().Name}";
            }
        }

        var rejects = new (decimal Value, WeightInputUnit Unit)[] { (0m, WeightInputUnit.Gram), (-1m, WeightInputUnit.Kilogram), (0.0004m, WeightInputUnit.Kilogram), (0.4m, WeightInputUnit.Gram), (10_000m, WeightInputUnit.Kilogram) };
        var allRejected = rejects.All(x =>
        {
            try { WeightedSales.ToKilograms(x.Value, x.Unit); return false; }
            catch (ArgumentOutOfRangeException) { return true; }
        });
        assert(scaleFailure.Length == 0 && allRejected,
            $"Property (seed {seed}): 5000 scale weights give the same kilograms in g and kg, a cent-exact never-decreasing price, and zero/negative/sub-gram/oversized weights are refused {scaleFailure}");
    }

    private static char Ean13Check(string first12)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (first12[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (char)('0' + (10 - sum % 10) % 10);
    }

    private static string MutateOneDigit(string value, Random random)
    {
        var index = random.Next(value.Length);
        var digit = (char)('0' + (value[index] - '0' + 1 + random.Next(9)) % 10);
        return value[..index] + digit + value[(index + 1)..];
    }
}
