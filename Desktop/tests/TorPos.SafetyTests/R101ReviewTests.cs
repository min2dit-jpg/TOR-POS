using TorPos.Application;
using TorPos.Core;
using TorPos.Infrastructure;

// R101: Mixed payment - splits a single sale's total across cash and a card
// terminal charge. CheckoutSnapshot.EffectiveCashPortionCents/
// EffectiveCardPortionCents is the single source of truth every downstream
// consumer (terminal charge amount, checkout journal state, Sale's own
// cash/card columns, FiscalProcessData, Kassensturz, turnover reports)
// reads instead of branching on Method directly. Sale.CashPortionCents/
// CardPortionCents are always populated going forward (Cash=(total,0),
// Card=(0,total), Mixed=(X,total-X)); a historical pre-R101 row can never
// be backfilled (sales is append-only - trg_sales_no_update aborts any
// UPDATE, including from migration code), so every report query that reads
// these columns falls back to deriving the split from payment_method+
// total_cents when both portions are still 0 - verified explicitly below,
// since it's the actual bug this feature's own test-writing caught (a raw
// test fixture insert, standing in for a real pre-R101 row, briefly broke
// R90's test until this fallback was added).
public static class R101ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert,
        Func<Func<Task>, string, Task> reject)
    {
        var dir = Path.Combine(root, "r101-mixed-payment");
        Directory.CreateDirectory(dir);

        // 1) Domain: EffectiveCashPortionCents/EffectiveCardPortionCents is
        // correct for all three methods, and a Mixed snapshot's CashPortionCents
        // parameter is ignored (not summed) for Cash/Card.
        var line = new CartLine { ProductId = 1, ProductName = "R101 Test", Quantity = 1, UnitPriceCents = 1000, ListUnitPriceCents = 1000, VatRate = 19 };
        var cashSnapshot = new CheckoutSnapshot("op-cash", new[] { line }, 0, PaymentMethod.Cash, "tester", null, false, 400);
        assert(
            cashSnapshot.EffectiveCashPortionCents == 1000 && cashSnapshot.EffectiveCardPortionCents == 0,
            "R101 a Cash snapshot's effective split is (total,0) regardless of any CashPortionCents value passed in");

        var cardSnapshot = new CheckoutSnapshot("op-card", new[] { line }, 0, PaymentMethod.Card, "tester", null);
        assert(
            cardSnapshot.EffectiveCashPortionCents == 0 && cardSnapshot.EffectiveCardPortionCents == 1000,
            "R101 a Card snapshot's effective split is (0,total)");

        var mixedSnapshot = new CheckoutSnapshot("op-mixed", new[] { line }, 0, PaymentMethod.Mixed, "tester", null, false, 400);
        assert(
            mixedSnapshot.EffectiveCashPortionCents == 400 && mixedSnapshot.EffectiveCardPortionCents == 600,
            "R101 a Mixed snapshot splits (400,600) from a 400 cash portion against a 1000 total");

        var overpaidCashSnapshot = new CheckoutSnapshot("op-clamp", new[] { line }, 0, PaymentMethod.Mixed, "tester", null, false, 5000);
        assert(
            overpaidCashSnapshot.EffectiveCashPortionCents == 1000 && overpaidCashSnapshot.EffectiveCardPortionCents == 0,
            "R101 a Mixed cash portion greater than the total is clamped, never producing a negative card portion");

        // 2) Application layer: PrepareProductionAsync charges the terminal
        // EXACTLY the card portion for Mixed, never the whole total, and the
        // checkout journal is seeded PREPARED (terminal flow), not CASH_READY.
        var journal = new FakeJournal();
        var terminal = new FakeTerminal(journal);
        var service = new CheckoutApplicationService(new FakeCompliance(true), journal, terminal);

        var mixedResult = await service.PrepareProductionAsync(
            new CheckoutSnapshot("r101-mixed-app", new[] { line }, 0, PaymentMethod.Mixed, "tester", null, false, 400));

        assert(
            terminal.LastAmountCents == 600,
            $"R101 a Mixed checkout charges the card terminal exactly the card portion (600), not the whole total (actual charged: {terminal.LastAmountCents})");
        assert(
            mixedResult.Disposition == CheckoutApplicationDisposition.ReadyToCommit && journal.LastState == "APPROVED",
            "R101 a Mixed checkout goes through the same terminal-APPROVED path as a pure Card checkout");

        var pureCashJournal = new FakeJournal();
        var pureCashService = new CheckoutApplicationService(new FakeCompliance(true), pureCashJournal, new FakeTerminal(pureCashJournal));
        await pureCashService.PrepareProductionAsync(
            new CheckoutSnapshot("r101-pure-cash", new[] { line }, 0, PaymentMethod.Cash, "tester", null));
        assert(
            pureCashJournal.LastState == "CASH_READY",
            "R101 a pure Cash checkout still never touches the terminal (unaffected by the Mixed change)");

        // 3) FiscalProcessData: a Mixed sale's TSE ProcessData payload carries
        // BOTH a Bar: and an Unbar: amount tag with their real portions, not
        // one amount tag for the whole total under a single tender type.
        var mixedSale = new Sale
        {
            Id = 1, ReceiptNumber = 101001, PaymentMethod = PaymentMethod.Mixed,
            TotalCents = 1000, CashPortionCents = 400, CardPortionCents = 600,
            Lines = new[] { line }, CreatedAt = DateTimeOffset.Now
        };
        var processData = System.Text.Encoding.UTF8.GetString(FiscalProcessData.BuildKassenbeleg(mixedSale));
        assert(
            processData.EndsWith("^4.00:Bar_6.00:Unbar"),
            $"R101/R130 FiscalProcessData emits both portions of a Mixed sale, cash first, as Betrag:Zahlart per DSFinV-K Anhang I (actual: {processData})");

        // 4) Kassensturz (GetExpectedCashCentsAsync): a Mixed sale's cash
        // portion counts toward the drawer, its card portion never does -
        // and a historical pre-R101 row (payment_method='CASH', both portion
        // columns still at their 0 default, exactly what a row from before
        // this column existed looks like forever, since sales is append-only)
        // still counts its FULL total as cash via the fallback.
        var kassenDb = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "kassensturz.db"));
        var cashMovements = new CashMovementRepository(kassenDb, new AuditLogRepository(kassenDb));
        long InsertRawSale(string paymentMethod, string transactionType, long totalCents, long cashPortionCents, long cardPortionCents, long receipt)
        {
            using var c = kassenDb.OpenConnection();
            using var q = c.CreateCommand();
            q.CommandText = """
                INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,cash_portion_cents,card_portion_cents)
                VALUES($r,$now,$pm,$t,$t,'TEST_FIXTURE',$type,$cash,$card);
                SELECT last_insert_rowid();
                """;
            q.Parameters.AddWithValue("$r", receipt);
            q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
            q.Parameters.AddWithValue("$pm", paymentMethod);
            q.Parameters.AddWithValue("$t", totalCents);
            q.Parameters.AddWithValue("$type", transactionType);
            q.Parameters.AddWithValue("$cash", cashPortionCents);
            q.Parameters.AddWithValue("$card", cardPortionCents);
            return Convert.ToInt64(q.ExecuteScalar());
        }
        InsertRawSale("MIXED", "SALE", 1000, 400, 600, 101101);
        // A row exactly as it would look if it had been written before R101
        // ever existed: payment_method='CASH', portion columns never touched.
        InsertRawSale("CASH", "SALE", 700, 0, 0, 101102);

        var expected = await cashMovements.GetExpectedCashCentsAsync(0);
        assert(
            expected == 400 + 700,
            $"R101 Kassensturz counts a Mixed sale's cash portion (400, not its full 1000 total) plus a historical pre-R101 CASH row's full total via the fallback (700) - actual: {expected}");

        // 5) Turnover report: a Mixed sale's split lands in both Bar and
        // Karte, not lost entirely and not double-counted into either alone.
        var turnoverManagement = new BusinessManagementService(kassenDb, new SettingsRepository(kassenDb), new AuditLogRepository(kassenDb));
        var turnover = await turnoverManagement.BuildTurnoverSummaryAsync();
        var todayLine = turnover.Lines.Single(x => x.StartsWith("HEUTE:"));
        assert(
            todayLine.Contains("Bar 11,00 EUR") && todayLine.Contains("Karte 6,00 EUR"),
            $"R101 UMSATZBERICHTE splits the Mixed sale's 4,00/6,00 correctly into Bar (7,00+4,00=11,00) and Karte (6,00) alongside the historical CASH row (actual: {todayLine})");

        // 6) BON STORNO/Teilretoure reject a Mixed-paid sale, same as a pure
        // Card sale - per the explicit product decision that a sale with any
        // card component needs a terminal reversal, which doesn't exist yet.
        var sales = new SaleRepository(kassenDb);
        var mixedSaleId = InsertRawSale("MIXED", "SALE", 1000, 400, 600, 101103);
        using (var c = kassenDb.OpenConnection())
        using (var q = c.CreateCommand())
        {
            q.CommandText = "INSERT INTO sale_items(sale_id,product_id,product_name,quantity,unit_price_cents,vat_rate,line_total_cents) VALUES($sale,1,'X',1,1000,19,1000);";
            q.Parameters.AddWithValue("$sale", mixedSaleId);
            q.ExecuteNonQuery();
        }
        await reject(
            () => sales.RecordStornoAsync(mixedSaleId, "tester", "Testgrund"),
            "R101 BON STORNO refuses a Mixed-paid sale, same restriction as a pure KARTE sale");

        // 7) Order board: a cashed MIXED order shows the split label, not the
        // generic 'Bezahlt · siehe Bon' fallback.
        var orderDb = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "orderboard.db"));
        var workflow = new OrderWorkflowService(orderDb);
        long orderSaleId;
        using (var c = orderDb.OpenConnection())
        using (var q = c.CreateCommand())
        {
            q.CommandText = "INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type) VALUES(101201,$now,'MIXED',1000,1000,'TEST_FIXTURE','SALE'); SELECT last_insert_rowid();";
            q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
            orderSaleId = Convert.ToInt64(q.ExecuteScalar());
        }
        using (var c = orderDb.OpenConnection())
        using (var q = c.CreateCommand())
        {
            q.CommandText = "INSERT INTO parked_receipts(park_number,pickup_number,created_at,updated_at,subtotal_cents,total_cents,status,is_training,cashed_sale_id,cashed_at) VALUES(101301,0,$now,$now,1000,1000,'CASHED',0,$sale,$now);";
            q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
            q.Parameters.AddWithValue("$sale", orderSaleId);
            q.ExecuteNonQuery();
        }
        var overview = await workflow.ListAsync(training: false, includeDelivered: true);
        var mixedOrder = overview.Single(x => x.ParkNumber == 101301);
        assert(
            mixedOrder.Payment == "Bar/Karte bezahlt",
            $"R101 a Mixed-paid cashed order shows 'Bar/Karte bezahlt' on the order board (actual: {mixedOrder.Payment})");
    }

    private sealed class FakeCompliance : IFiscalComplianceService
    {
        private readonly bool _allowed;
        public FakeCompliance(bool allowed) => _allowed = allowed;
        public Task<FiscalReadinessReport> CheckAsync(CancellationToken ct = default) =>
            Task.FromResult(new FiscalReadinessReport(_allowed, _allowed ? "READY" : "BLOCKED", "R101-TEST", "2.4", Array.Empty<FiscalReadinessItem>()));
    }

    private sealed class FakeTerminal : IPaymentTerminalService
    {
        private readonly FakeJournal _journal;
        public long LastAmountCents { get; private set; } = -1;
        public FakeTerminal(FakeJournal journal) => _journal = journal;

        public IReadOnlyList<PaymentTerminalProfile> Profiles => Array.Empty<PaymentTerminalProfile>();
        public Task<PaymentTerminalProbeResult> ProbeAsync(CancellationToken ct = default) =>
            Task.FromResult(new PaymentTerminalProbeResult(true, "OK", "R101 fake", "fake"));
        public Task<PaymentTerminalProbeResult> RegisterAsync(CancellationToken ct = default) => ProbeAsync(ct);
        public Task<PaymentTerminalProbeResult> EndOfDayAsync(CancellationToken ct = default) => ProbeAsync(ct);

        public async Task<PaymentTerminalPaymentResult> PayAsync(long amountCents, string operationId, CancellationToken ct = default)
        {
            LastAmountCents = amountCents;
            await _journal.MarkTerminalSubmittedAsync(operationId, "fake submit");
            await _journal.TransitionTerminalAsync(operationId, "SENT", "APPROVED", "fake approve", PaymentTerminalOutcome.Approved, true, "00", "OK");
            return new PaymentTerminalPaymentResult(true, "00", "OK", Outcome: PaymentTerminalOutcome.Approved, RequestSubmitted: true, OutcomeCode: "00");
        }

        public Task<PaymentTerminalPaymentResult> RefundAsync(long amountCents, string operationId, CancellationToken ct = default)
        {
            LastAmountCents = amountCents;
            return Task.FromResult(new PaymentTerminalPaymentResult(true, "00", "OK", Outcome: PaymentTerminalOutcome.Approved, RequestSubmitted: true, OutcomeCode: "00"));
        }
    }

    private sealed class FakeJournal : ICheckoutJournal
    {
        private CheckoutOperation? _op;
        public string LastState => _op?.State ?? "";

        public Task BeginAsync(CheckoutSnapshot snapshot)
        {
            var state = snapshot.EffectiveCardPortionCents > 0 ? "PREPARED" : "CASH_READY";
            _op = new CheckoutOperation(snapshot, state, "", null);
            return Task.CompletedTask;
        }

        public Task MarkTerminalSubmittedAsync(string id, string evidence)
        {
            if (_op is not null) _op = _op with { State = "SENT" };
            return Task.CompletedTask;
        }

        public Task TransitionTerminalAsync(string id, string expected, string state, string evidence, PaymentTerminalOutcome outcome, bool requestSubmitted, string terminalCode, string terminalMessage)
        {
            if (_op is not null) _op = _op with { State = state, TerminalOutcome = outcome, TerminalRequestSubmitted = requestSubmitted, TerminalCode = terminalCode, TerminalMessage = terminalMessage };
            return Task.CompletedTask;
        }

        public Task ResolveAsync(string id, string expected, bool paid, string actor, string evidence) => Task.CompletedTask;
        public Task<IReadOnlyList<CheckoutOperation>> GetOpenAsync() =>
            Task.FromResult<IReadOnlyList<CheckoutOperation>>(_op is null ? Array.Empty<CheckoutOperation>() : new[] { _op });
        public Task<CheckoutOperation?> GetAsync(string id) => Task.FromResult(_op);
        public Task<long?> FindSaleAsync(string id) => Task.FromResult((long?)null);
    }
}
