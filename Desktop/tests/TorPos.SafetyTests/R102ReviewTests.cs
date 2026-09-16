using Microsoft.Data.Sqlite;
using TorPos.Application;
using TorPos.Core;
using TorPos.Infrastructure;

// R102: Card-payment BON STORNO/Teilretoure, via ZVT's RefundAsync (a
// manual credit - the customer presents their card again, no dependency on
// the original transaction still being in the terminal's own memory,
// unlike ReversalAsync). The DB-side gate is structural, not just a UI
// convention: RecordStornoAsync/RecordReturnAsync refuse to record a
// reversal against a nonzero card portion unless the caller supplies
// non-empty cardRefundEvidence, which only MainWindow's orchestration
// (terminal refund confirmed APPROVED first) ever produces.
//
// NOTE: same limitation R79/R82/R90's own tests already document -
// FiscalRelease.RequireProduction() always throws in this test build, so
// the SUCCESSFUL completion of RecordStornoAsync/RecordReturnAsync (and
// therefore the actual payment_method/cash_portion_cents/card_portion_cents
// mirroring it now does instead of the old hardcoded 'CASH') can't be
// exercised end-to-end here - only the REJECTION gates (which fire before
// RequireProduction() is ever reached) and the pure domain logic
// (Sale.EffectiveCashPortionCents/EffectiveCardPortionCents, which both
// the gate and the mirroring share) are covered.
public static class R102ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert,
        Func<Func<Task>, string, Task> reject)
    {
        var dir = Path.Combine(root, "r102-card-storno-refund");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r102.db"));
        var sales = new SaleRepository(db);
        var receiptCounter = 102000L;

        long InsertRawSale(string paymentMethod, long totalCents, long cashPortionCents, long cardPortionCents, out long itemId)
        {
            using var c = db.OpenConnection();
            using var tx = c.BeginTransaction();
            long saleId;
            using (var q = c.CreateCommand())
            {
                q.Transaction = (SqliteTransaction)tx;
                q.CommandText = """
                    INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,cash_portion_cents,card_portion_cents)
                    VALUES($r,$now,$pm,$t,$t,'TEST_FIXTURE','SALE',$cash,$card);
                    SELECT last_insert_rowid();
                    """;
                q.Parameters.AddWithValue("$r", ++receiptCounter);
                q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
                q.Parameters.AddWithValue("$pm", paymentMethod);
                q.Parameters.AddWithValue("$t", totalCents);
                q.Parameters.AddWithValue("$cash", cashPortionCents);
                q.Parameters.AddWithValue("$card", cardPortionCents);
                saleId = Convert.ToInt64(q.ExecuteScalar());
            }
            long localItemId;
            using (var q = c.CreateCommand())
            {
                q.Transaction = (SqliteTransaction)tx;
                q.CommandText = "INSERT INTO sale_items(sale_id,product_id,product_name,quantity,unit_price_cents,vat_rate,line_total_cents) VALUES($sale,1,'R102 Artikel',1,$t,19,$t); SELECT last_insert_rowid();";
                q.Parameters.AddWithValue("$sale", saleId);
                q.Parameters.AddWithValue("$t", totalCents);
                localItemId = Convert.ToInt64(q.ExecuteScalar());
            }
            tx.Commit();
            itemId = localItemId;
            return saleId;
        }

        // 1) Domain: Sale.EffectiveCashPortionCents/EffectiveCardPortionCents
        // - the exact logic both the new card-refund gate AND the R101
        // report-query fallback rely on - is correct for a current-schema
        // row (nonzero portions trusted directly) and for a historical
        // pre-R101 row (both portions still 0, derived from PaymentMethod
        // instead).
        var currentCard = new Sale { PaymentMethod = PaymentMethod.Card, TotalCents = 500, CashPortionCents = 0, CardPortionCents = 500 };
        assert(
            currentCard.EffectiveCashPortionCents == 0 && currentCard.EffectiveCardPortionCents == 500,
            "R102 a current-schema KARTE sale's effective split trusts its own stored (0,500) directly");

        var historicalCard = new Sale { PaymentMethod = PaymentMethod.Card, TotalCents = 500, CashPortionCents = 0, CardPortionCents = 0 };
        assert(
            historicalCard.EffectiveCashPortionCents == 0 && historicalCard.EffectiveCardPortionCents == 500,
            "R102 a historical pre-R101 KARTE sale (portions still 0/0) derives (0,500) from PaymentMethod+TotalCents instead");

        var historicalCash = new Sale { PaymentMethod = PaymentMethod.Cash, TotalCents = 300, CashPortionCents = 0, CardPortionCents = 0 };
        assert(
            historicalCash.EffectiveCashPortionCents == 300 && historicalCash.EffectiveCardPortionCents == 0,
            "R102 a historical pre-R101 CASH sale (portions still 0/0) derives (300,0) from PaymentMethod+TotalCents instead");

        var currentMixed = new Sale { PaymentMethod = PaymentMethod.Mixed, TotalCents = 1000, CashPortionCents = 400, CardPortionCents = 600 };
        assert(
            currentMixed.EffectiveCashPortionCents == 400 && currentMixed.EffectiveCardPortionCents == 600,
            "R102 a current-schema Mixed sale's effective split trusts its own stored (400,600) directly");

        // 2) Application layer: RefundStornoCardPortionAsync is a no-op for
        // a pure Cash original (nothing to refund) and charges the terminal
        // exactly the requested card portion otherwise.
        var journal = new FakeJournal();
        var terminal = new FakeRefundTerminal();
        var appService = new CheckoutApplicationService(new FakeCompliance(true), journal, terminal);

        var noRefund = await appService.RefundStornoCardPortionAsync(0, "r102-none");
        assert(
            noRefund is null && terminal.RefundCount == 0,
            "R102 RefundStornoCardPortionAsync is a no-op when the card portion is 0 (pure Cash original)");

        var refunded = await appService.RefundStornoCardPortionAsync(600, "r102-refund");
        assert(
            refunded is not null && refunded.Success && terminal.LastRefundAmountCents == 600,
            $"R102 RefundStornoCardPortionAsync charges the terminal exactly the requested card portion (actual: {terminal.LastRefundAmountCents})");

        // 3) DB gate: a pure KARTE sale's BON STORNO is refused without
        // cardRefundEvidence - fires before FiscalRelease.RequireProduction()
        // is ever reached, so this is testable even though the successful
        // path (with evidence supplied) is not, in this harness.
        var cardSaleId = InsertRawSale("CARD", 500, 0, 500, out _);
        await reject(
            () => sales.RecordStornoAsync(cardSaleId, "tester", "Testgrund"),
            "R102 a KARTE sale's BON STORNO is refused without cardRefundEvidence");

        // 4) The same gate still fires for a historical pre-R101 CARD row
        // (payment_method='CARD', both portion columns still at their 0
        // default) via the EffectiveCardPortionCents fallback - the raw
        // column reading 0 must never silently bypass the requirement.
        var historicalCardSaleId = InsertRawSale("CARD", 300, 0, 0, out _);
        await reject(
            () => sales.RecordStornoAsync(historicalCardSaleId, "tester", "Testgrund"),
            "R102 a historical pre-R101 KARTE row (portion columns still 0) is still gated via the Effective*PortionCents fallback, not silently let through");

        // 5) A Mixed sale's BON STORNO is refused without cardRefundEvidence
        // (already covered once in R101ReviewTests for the Mixed case
        // specifically; repeated here for R102's own gate-message change).
        var mixedSaleId = InsertRawSale("MIXED", 1000, 400, 600, out _);
        await reject(
            () => sales.RecordStornoAsync(mixedSaleId, "tester", "Testgrund"),
            "R102 a Mixed sale's BON STORNO is refused without cardRefundEvidence");

        // 6) A pure Cash sale's BON STORNO still requires FiscalRelease
        // production (the ONLY gate it hits, same as always) - proves the
        // new card-refund gate itself does not fire for a Cash-only sale.
        var cashSaleId = InsertRawSale("CASH", 200, 200, 0, out _);
        await reject(
            () => sales.RecordStornoAsync(cashSaleId, "tester", "Testgrund"),
            "R102 a pure Cash sale's BON STORNO still only hits the FiscalRelease gate, never the new card-refund one");

        // 7) Teilretoure: a partial return against a Mixed sale's card
        // portion is refused without cardRefundEvidence, same shape as
        // BON STORNO.
        var mixedForReturnId = InsertRawSale("MIXED", 1000, 400, 600, out var mixedItemId);
        await reject(
            () => sales.RecordReturnAsync(mixedForReturnId, new[] { new ReturnLineRequest(mixedItemId, 1m) }, "tester", "Testgrund"),
            "R102 a Teilretoure against a Mixed sale's card-bearing portion is refused without cardRefundEvidence");
    }

    private sealed class FakeCompliance : IFiscalComplianceService
    {
        private readonly bool _allowed;
        public FakeCompliance(bool allowed) => _allowed = allowed;
        public Task<FiscalReadinessReport> CheckAsync(CancellationToken ct = default) =>
            Task.FromResult(new FiscalReadinessReport(_allowed, _allowed ? "READY" : "BLOCKED", "R102-TEST", "2.4", Array.Empty<FiscalReadinessItem>()));
    }

    private sealed class FakeRefundTerminal : IPaymentTerminalService
    {
        public int RefundCount { get; private set; }
        public long LastRefundAmountCents { get; private set; } = -1;

        public IReadOnlyList<PaymentTerminalProfile> Profiles => Array.Empty<PaymentTerminalProfile>();
        public Task<PaymentTerminalProbeResult> ProbeAsync(CancellationToken ct = default) =>
            Task.FromResult(new PaymentTerminalProbeResult(true, "OK", "R102 fake", "fake"));
        public Task<PaymentTerminalProbeResult> RegisterAsync(CancellationToken ct = default) => ProbeAsync(ct);
        public Task<PaymentTerminalProbeResult> EndOfDayAsync(CancellationToken ct = default) => ProbeAsync(ct);
        public Task<PaymentTerminalPaymentResult> PayAsync(long amountCents, string operationId, CancellationToken ct = default) =>
            throw new InvalidOperationException("R102 storno-refund tests never call PayAsync.");

        public Task<PaymentTerminalPaymentResult> RefundAsync(long amountCents, string operationId, CancellationToken ct = default)
        {
            RefundCount++;
            LastRefundAmountCents = amountCents;
            return Task.FromResult(new PaymentTerminalPaymentResult(true, "00", "OK", Outcome: PaymentTerminalOutcome.Approved, RequestSubmitted: true, OutcomeCode: "00"));
        }
    }

    private sealed class FakeJournal : ICheckoutJournal
    {
        public Task BeginAsync(CheckoutSnapshot snapshot) => Task.CompletedTask;
        public Task MarkTerminalSubmittedAsync(string id, string evidence) => Task.CompletedTask;
        public Task TransitionTerminalAsync(string id, string expected, string state, string evidence, PaymentTerminalOutcome outcome, bool requestSubmitted, string terminalCode, string terminalMessage) => Task.CompletedTask;
        public Task ResolveAsync(string id, string expected, bool paid, string actor, string evidence) => Task.CompletedTask;
        public Task<IReadOnlyList<CheckoutOperation>> GetOpenAsync() => Task.FromResult<IReadOnlyList<CheckoutOperation>>(Array.Empty<CheckoutOperation>());
        public Task<CheckoutOperation?> GetAsync(string id) => Task.FromResult((CheckoutOperation?)null);
        public Task<long?> FindSaleAsync(string id) => Task.FromResult((long?)null);
    }
}
