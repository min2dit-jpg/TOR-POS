using System.Text;
using TorPos.Core;
using TorPos.Infrastructure;

// R139: the Kassensturz books its difference as DifferenzSollIst, and the
// calculated cash runs on without a break at a closing.
//
// DSFinV-K Anhang C, DifferenzSollIst: "stellt die Abweichung zwischen einem
// errechneten und dem gezählten Kassenbestand dar ... Differenzen können so
// festgestellt, protokolliert und ausgeglichen werden." Anfangsbestand: cash
// taken out at a closing is booked (Geldtransit), the next Anfangsbestand is what
// is left. Until R139 the difference only appeared on the printout, and every
// Z-Bericht reset the calculated cash to a fixed start amount.
public static class R139ReviewTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "r139-kassensturz");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r139.db"));
        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        var movements = new CashMovementRepository(db, audit);
        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["company.name"] = "R139 Kiosk",
            ["company.street"] = "Hauptstraße 1",
            ["company.zip"] = "10115",
            ["company.city"] = "Berlin",
            ["company.tax_no"] = "27/123/45678",
            ["tse.status"] = "AKTIV",
            ["tse.client_id"] = "KASSE-1",
        });

        assert(CashBusinessCases.Allowed(CashMovementKind.Einlage, CashBusinessCase.DifferenzSollIst) &&
               CashBusinessCases.Allowed(CashMovementKind.Entnahme, CashBusinessCase.DifferenzSollIst) &&
               !CashBusinessCases.Allowed(CashMovementKind.CashCount, CashBusinessCase.DifferenzSollIst) &&
               !CashBusinessCases.For(CashMovementKind.Einlage).Contains(CashBusinessCase.DifferenzSollIst) &&
               !CashBusinessCases.For(CashMovementKind.Entnahme).Contains(CashBusinessCase.DifferenzSollIst),
            "R139 DifferenzSollIst fits a surplus and a shortfall, but is not offered for a manual Einlage/Entnahme");

        var manualRefused = false;
        try { await movements.AddAsync(new CashMovementRequest(CashMovementKind.Einlage, 100, "x", CashBusinessCase.DifferenzSollIst), "kasse1"); }
        catch (InvalidOperationException ex) { manualRefused = ex.Message.Contains("Kassensturz"); }
        assert(manualRefused, "R139 a difference can only be booked by a Kassensturz");

        // ---------- production: no reset at a closing ----------
        await InsertSaleAsync(db, 139001, 2500);
        await InsertMovementAsync(db, "EINLAGE", 700, "PRODUCTION", "Geldtransit");
        await InsertMovementAsync(db, "EINLAGE", 9999, "TEST_ONLY", "Einzahlung");
        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "INSERT INTO daily_closings(closed_at,operator_name,close_type) VALUES($at,'chef','Z_REPORT');";
            q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
            await q.ExecuteNonQueryAsync();
        }
        await InsertMovementAsync(db, "ENTNAHME", 2000, "PRODUCTION", "Geldtransit");
        var afterClosing = await movements.GetCashBalanceAsync(10000, production: true);
        assert(afterClosing.ExpectedCents == 10000 + 2500 + 700 - 2000 && afterClosing.CountedAt is null,
            $"R139 the calculated cash runs on across a Z-Bericht: Anfangsbestand + cash sales + booked movements, test entries left out (actual {afterClosing.ExpectedCents})");

        // a confirmed Kassensturz (written directly, the breaker stays off) is the new origin
        await InsertMovementAsync(db, "CASH_COUNT", 11000, "PRODUCTION", "");
        await InsertSaleAsync(db, 139002, 450);
        var afterCount = await movements.GetCashBalanceAsync(10000, production: true);
        assert(afterCount.ExpectedCents == 11450 && afterCount.BaseCents == 11000 && afterCount.CountedAt is not null,
            "R139 after a confirmed Kassensturz the counted amount is the starting point; only later cash flows are added");

        var productionRefused = false;
        try { await movements.BookCashCountAsync(11000, 10000, "", production: true, "kasse1"); }
        catch (InvalidOperationException) { productionRefused = true; }
        var afterRefusal = await movements.GetCashBalanceAsync(10000, production: true);
        assert(productionRefused && afterRefusal.ExpectedCents == 11450,
            "R139 while the fiscal circuit breaker is off a real Kassensturz books nothing");

        // ---------- the booking itself (test mode, same code path) ----------
        var testDb = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r139-test.db"));
        var testMovements = new CashMovementRepository(testDb, new AuditLogRepository(testDb));
        await InsertMovementAsync(testDb, "EINLAGE", 1000, "TEST_ONLY", "Geldtransit");
        var shortfall = await testMovements.BookCashCountAsync(5800, 5000, "Zählfehler", production: false, "kasse1");
        var afterShortfall = await testMovements.GetCashBalanceAsync(5000, production: false);
        var surplus = await testMovements.BookCashCountAsync(5900, 5000, "", production: false, "kasse1");
        var exact = await testMovements.BookCashCountAsync(5900, 5000, "", production: false, "kasse1");
        assert(shortfall.ExpectedCents == 6000 && shortfall.DifferenceCents == -200 &&
               shortfall.Difference is { Kind: CashMovementKind.Entnahme, AmountCents: 200, BusinessCase: CashBusinessCase.DifferenzSollIst } &&
               shortfall.Count.Id > shortfall.Difference.Id && afterShortfall.ExpectedCents == 5800 &&
               surplus.Difference is { Kind: CashMovementKind.Einlage, AmountCents: 100 } &&
               exact.Difference is null && exact.DifferenceCents == 0,
            "R139 a shortfall is booked as Entnahme DifferenzSollIst, a surplus as Einlage, no difference books nothing; the count is the new origin");

        long auditRows;
        await using (var c = testDb.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM audit_log WHERE event_type='CASH_COUNT' AND details LIKE '%soll_cents=6000; ist_cents=5800; differenz_cents=-200%';";
            auditRows = Convert.ToInt64(await q.ExecuteScalarAsync());
        }
        assert(auditRows == 1 && shortfall.Difference!.FiscalMode == CashMovement.TestMode,
            "R139 every confirmed Kassensturz is logged with Soll, Ist and difference; on a test till nothing is fiscal");

        // ---------- a real difference: TSE and export ----------
        var provider = new RecordingTseProvider();
        var tse = new TseFailSafeService(provider, new TseOutageRepository(db, audit), audit);
        var differenceId = await InsertMovementAsync(db, "ENTNAHME", 350, "PRODUCTION", "DifferenzSollIst");
        var difference = new CashMovement(differenceId, DateTimeOffset.Now, CashMovementKind.Entnahme, 350, "Kassendifferenz · Kassensturz", "kasse1",
            CashMovement.ProductionMode, CashBusinessCase.DifferenzSollIst);
        var signed = await new CashMovementFiscalSigningService(tse, settings, movements).SignAsync(difference, "kasse1");
        assert(signed.Signed && provider.Finishes.Count == 1 && provider.Finishes[0].ProcessType == "Kassenbeleg-V1" &&
               Encoding.UTF8.GetString(provider.Finishes[0].ProcessData) == "Beleg^0.00_0.00_0.00_0.00_-3.50^-3.50:Bar",
            "R139 a real difference is secured like any cash movement: Kassenbeleg-V1, 0 % container, Bar");

        await Task.Delay(15);
        await new BusinessManagementService(db, settings, audit).CreateZArchiveAsync("chef", "TEST");
        var folder = await new DsfinvkExportService(db, settings).ExportAsync(DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddMinutes(1), Path.Combine(dir, "out"));
        var bon = $"\"KB-{differenceId}\"";
        var head = File.ReadAllLines(Path.Combine(folder, "transactions.csv")).Skip(1).Single(l => l.Contains($";{bon};"));
        var line = File.ReadAllLines(Path.Combine(folder, "lines.csv")).Skip(1).Select(l => l.Split(';')).Single(f => f[3] == bon);
        var cases = File.ReadAllLines(Path.Combine(folder, "businesscases.csv")).Skip(1).ToArray();
        assert(head.Contains("\"Kassendifferenz (Fehlbetrag beim Kassensturz)\"") && head.Contains(";-3,50;") &&
               line[8] == "\"DifferenzSollIst\"" && line[9] == "\"Kassendifferenz\"" &&
               cases.Any(c => c.Contains(";\"DifferenzSollIst\";") && c.Contains("-3,50")),
            "R139 the export shows the difference as GV_TYP DifferenzSollIst, in the Einzelaufzeichnung and in the closing");
    }

    private static async Task InsertSaleAsync(SqliteDatabase db, long receipt, long cents)
    {
        await Task.Delay(15);
        await using var c = db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = "INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,cash_portion_cents,card_portion_cents) VALUES($r,$at,'CASH',$t,$t,'TEST_FIXTURE','SALE',$t,0);";
        q.Parameters.AddWithValue("$r", receipt);
        q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        q.Parameters.AddWithValue("$t", cents);
        q.CommandText += " SELECT last_insert_rowid();";
        var id = Convert.ToInt64(await q.ExecuteScalarAsync());
        await using var item = c.CreateCommand();
        item.CommandText = "INSERT INTO sale_items(sale_id,product_id,product_name,quantity,unit_price_cents,vat_rate,line_total_cents) VALUES($s,1,'Artikel',1,$t,19,$t);";
        item.Parameters.AddWithValue("$s", id);
        item.Parameters.AddWithValue("$t", cents);
        await item.ExecuteNonQueryAsync();
    }

    private static async Task<long> InsertMovementAsync(SqliteDatabase db, string type, long cents, string mode, string businessCase)
    {
        await Task.Delay(15);
        await using var c = db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = "INSERT INTO cash_movements(created_at,movement_type,amount_cents,reason,actor,fiscal_mode,business_case) VALUES($at,$type,$amount,'R139','kasse1',$mode,$case); SELECT last_insert_rowid();";
        q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        q.Parameters.AddWithValue("$type", type);
        q.Parameters.AddWithValue("$amount", cents);
        q.Parameters.AddWithValue("$mode", mode);
        q.Parameters.AddWithValue("$case", businessCase);
        return Convert.ToInt64(await q.ExecuteScalarAsync());
    }

    private sealed class RecordingTseProvider : ITseProvider
    {
        public readonly List<TseTransactionFinishRequest> Finishes = new();

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

        public Task<TseTransactionResult> StartTransactionAsync(TseTransactionStartRequest request, CancellationToken ct = default) =>
            Task.FromResult(new TseTransactionResult(true, "OK", 39, 1, DateTimeOffset.UtcNow, "FAKE-SERIAL", "c3RhcnQ="));

        public Task<TseTransactionResult> UpdateTransactionAsync(TseTransactionUpdateRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TseTransactionResult> FinishTransactionAsync(TseTransactionFinishRequest request, CancellationToken ct = default)
        {
            Finishes.Add(request);
            return Task.FromResult(new TseTransactionResult(true, "OK", request.TransactionNumber, 2, DateTimeOffset.UtcNow, "FAKE-SERIAL", "ZmluaXNo"));
        }

        public Task<TseExportResult> ExportTarAsync(string targetPath, CancellationToken ct = default) =>
            Task.FromResult(new TseExportResult(false, "Fake export"));
    }
}
