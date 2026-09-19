using TorPos.Application;
using TorPos.Core;
using TorPos.Infrastructure;

public static class R75ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool,string> assert,
        Func<Func<Task>,string,Task> reject)
    {
        assert(
            SchemaMigrationService.TargetSchemaVersion>=5,
            "R75 Application layer refactor requires no customer database migration");

        var blockedJournal=new FakeJournal();
        var blockedTerminal=new FakeTerminal(blockedJournal);
        var blockedCompliance=new FakeCompliance(false);
        var blockedService=new CheckoutApplicationService(
            blockedCompliance,
            blockedJournal,
            blockedTerminal);

        var blocked=await blockedService.PrepareProductionAsync(
            Snapshot("r75-fiscal-block",PaymentMethod.Card));

        assert(
            blocked.Disposition==CheckoutApplicationDisposition.FiscalBlocked &&
            blockedJournal.BeginCount==0 &&
            blockedTerminal.PayCount==0,
            "R75 fiscal block is decided in Application layer before journal or terminal side effects");

        var cashJournal=new FakeJournal();
        var cashTerminal=new FakeTerminal(cashJournal);
        var cashService=new CheckoutApplicationService(
            new FakeCompliance(true),
            cashJournal,
            cashTerminal);

        var cash=await cashService.PrepareProductionAsync(
            Snapshot("r75-cash",PaymentMethod.Cash));

        assert(
            cash.Disposition==CheckoutApplicationDisposition.ReadyToCommit &&
            cash.Operation?.State=="CASH_READY" &&
            cashJournal.BeginCount==1,
            "R75 cash checkout orchestration returns durable CASH_READY");

        assert(
            cashTerminal.PayCount==0,
            "R75 cash checkout Application flow never calls card terminal");

        var approvedJournal=new FakeJournal();
        var approvedTerminal=new FakeTerminal(
            approvedJournal,
            FakeTerminalMode.Approved);
        var approvedService=new CheckoutApplicationService(
            new FakeCompliance(true),
            approvedJournal,
            approvedTerminal);

        var approved=await approvedService.PrepareProductionAsync(
            Snapshot("r75-approved",PaymentMethod.Card));

        assert(
            approved.Disposition==CheckoutApplicationDisposition.ReadyToCommit &&
            approved.Operation?.State=="APPROVED" &&
            approved.Operation.TerminalOutcome==PaymentTerminalOutcome.Approved &&
            approvedTerminal.PayCount==1,
            "R75 approved card checkout is orchestrated outside MainWindow and remains single-submit");

        var notSentJournal=new FakeJournal();
        var notSentTerminal=new FakeTerminal(
            notSentJournal,
            FakeTerminalMode.NotSent);
        var notSentService=new CheckoutApplicationService(
            new FakeCompliance(true),
            notSentJournal,
            notSentTerminal);

        var notSent=await notSentService.PrepareProductionAsync(
            Snapshot("r75-not-sent",PaymentMethod.Card));

        assert(
            notSent.Disposition==CheckoutApplicationDisposition.NotCharged &&
            notSent.Operation?.State=="NOT_CHARGED" &&
            notSent.Operation.TerminalOutcome==PaymentTerminalOutcome.NotSent &&
            !notSent.Operation.TerminalRequestSubmitted,
            "R75 Application layer normalizes PREPARED terminal pre-send failure to NOT_SENT");

        var declinedJournal=new FakeJournal();
        var declinedTerminal=new FakeTerminal(
            declinedJournal,
            FakeTerminalMode.Declined);
        var declinedService=new CheckoutApplicationService(
            new FakeCompliance(true),
            declinedJournal,
            declinedTerminal);

        var declined=await declinedService.PrepareProductionAsync(
            Snapshot("r75-declined",PaymentMethod.Card));

        assert(
            declined.Disposition==CheckoutApplicationDisposition.NotCharged &&
            declined.Operation?.TerminalOutcome==PaymentTerminalOutcome.Declined,
            "R75 explicit terminal decline returns NotCharged without forcing UNKNOWN");

        var unknownJournal=new FakeJournal();
        var unknownTerminal=new FakeTerminal(
            unknownJournal,
            FakeTerminalMode.Unknown);
        var unknownService=new CheckoutApplicationService(
            new FakeCompliance(true),
            unknownJournal,
            unknownTerminal);

        var unknown=await unknownService.PrepareProductionAsync(
            Snapshot("r75-unknown",PaymentMethod.Card));

        assert(
            unknown.Disposition==CheckoutApplicationDisposition.Unresolved &&
            unknown.Operation?.State=="UNKNOWN" &&
            unknown.Operation.TerminalOutcome==PaymentTerminalOutcome.Unknown,
            "R75 true terminal uncertainty stays unresolved and locked");

        var mismatchJournal=new FakeJournal();
        var mismatchTerminal=new FakeTerminal(
            mismatchJournal,
            FakeTerminalMode.SuccessWithoutJournalApproval);
        var mismatchService=new CheckoutApplicationService(
            new FakeCompliance(true),
            mismatchJournal,
            mismatchTerminal);

        await reject(
            () => mismatchService.PrepareProductionAsync(
                Snapshot("r75-mismatch",PaymentMethod.Card)),
            "R75 terminal success without durable APPROVED journal state is rejected");

        var reconciliationJournal=new FakeJournal();
        var reconciliationTerminal=new FakeTerminal(
            reconciliationJournal,
            FakeTerminalMode.Unknown);
        var reconciliationCompliance=new FakeCompliance(true);
        var reconciliationService=new CheckoutApplicationService(
            reconciliationCompliance,
            reconciliationJournal,
            reconciliationTerminal);

        var unresolved=await reconciliationService.PrepareProductionAsync(
            Snapshot("r75-reconcile-paid",PaymentMethod.Card));

        reconciliationCompliance.Allowed=false;

        var paid=await reconciliationService.ReconcileAsync(
            unresolved.Operation!,
            paid:true,
            actor:"admin-r75",
            evidence:"Terminalbeleg 9001 bestätigt");

        assert(
            paid.Operation.State=="APPROVED" &&
            paid.Operation.TerminalOutcome==PaymentTerminalOutcome.Unknown &&
            paid.Operation.Resolution==CheckoutResolution.ManualPaid &&
            !paid.ShouldCommit,
            "R75 paid reconciliation preserves original UNKNOWN and still respects fiscal gate");

        var noChargeJournal=new FakeJournal();
        var noChargeTerminal=new FakeTerminal(
            noChargeJournal,
            FakeTerminalMode.Unknown);
        var noChargeCompliance=new FakeCompliance(true);
        var noChargeService=new CheckoutApplicationService(
            noChargeCompliance,
            noChargeJournal,
            noChargeTerminal);

        var noChargeUnknown=await noChargeService.PrepareProductionAsync(
            Snapshot("r75-reconcile-nocharge",PaymentMethod.Card));

        var complianceCallsBefore=
            noChargeCompliance.CheckCount;

        var noCharge=await noChargeService.ReconcileAsync(
            noChargeUnknown.Operation!,
            paid:false,
            actor:"admin-r75",
            evidence:"Netzbetreiber bestätigt keine Belastung");

        assert(
            noCharge.Operation.State=="NOT_CHARGED" &&
            noCharge.Operation.Resolution==CheckoutResolution.ManualNotCharged &&
            noChargeCompliance.CheckCount==complianceCallsBefore,
            "R75 no-charge reconciliation closes journal without unnecessary fiscal recheck");

        var realDb=await SafetyDatabase.CreateCurrentAsync(
            Path.Combine(root,"r75-interface.db"));

        assert(
            new CheckoutJournal(realDb) is ICheckoutJournal,
            "R75 SQLite CheckoutJournal implements the Core application boundary");

        assert(
            typeof(CheckoutApplicationService).Assembly.GetName().Name=="TorPos.Application",
            "R75 checkout orchestration is compiled in the dedicated TorPos.Application assembly");

        assert(
            typeof(CheckoutApplicationService)
                .GetConstructors()
                .Single()
                .GetParameters()
                .All(x =>
                    x.ParameterType.Assembly.GetName().Name=="TorPos.Core"),
            "R75 CheckoutApplicationService constructor depends only on Core contracts");

        assert(
            approved.Timings.FiscalPreflightMs is >=0 &&
            approved.Timings.JournalBeginMs is >=0 &&
            approved.Timings.TerminalRoundtripMs is >=0,
            "R75 Application layer returns technical timings without depending on Infrastructure metrics");
    }

    private static CheckoutSnapshot Snapshot(
        string operationId,
        PaymentMethod method) =>
        new(
            operationId,
            new[]
            {
                new CartLine
                {
                    ProductId=1,
                    ProductName="R75 TEST",
                    Quantity=1,
                    UnitPriceCents=1000,
                    ListUnitPriceCents=1000,
                    VatRate=19
                }
            },
            0,
            method,
            "tester",
            null);

    private sealed class FakeCompliance : IFiscalComplianceService
    {
        public bool Allowed { get; set; }
        public int CheckCount { get; private set; }

        public FakeCompliance(bool allowed) =>
            Allowed=allowed;

        public Task<FiscalReadinessReport> CheckAsync(
            CancellationToken ct=default)
        {
            CheckCount++;

            return Task.FromResult(
                new FiscalReadinessReport(
                    Allowed,
                    Allowed ? "READY" : "BLOCKED",
                    "R75-TEST",
                    "2.4",
                    Array.Empty<FiscalReadinessItem>()));
        }
    }

    private enum FakeTerminalMode
    {
        Approved,
        NotSent,
        Declined,
        Unknown,
        SuccessWithoutJournalApproval
    }

    private sealed class FakeTerminal : IPaymentTerminalService
    {
        private readonly FakeJournal _journal;
        private readonly FakeTerminalMode _mode;

        public int PayCount { get; private set; }

        public FakeTerminal(
            FakeJournal journal,
            FakeTerminalMode mode=FakeTerminalMode.Approved)
        {
            _journal=journal;
            _mode=mode;
        }

        public IReadOnlyList<PaymentTerminalProfile> Profiles =>
            Array.Empty<PaymentTerminalProfile>();

        public Task<PaymentTerminalProbeResult> ProbeAsync(
            CancellationToken ct=default) =>
            Task.FromResult(
                new PaymentTerminalProbeResult(
                    true,"OK","R75 fake","fake"));

        public Task<PaymentTerminalProbeResult> RegisterAsync(
            CancellationToken ct=default) =>
            ProbeAsync(ct);

        public Task<PaymentTerminalProbeResult> EndOfDayAsync(
            CancellationToken ct=default) =>
            ProbeAsync(ct);

        public async Task<PaymentTerminalPaymentResult> PayAsync(
            long amountCents,
            string operationId,
            CancellationToken ct=default)
        {
            PayCount++;

            switch(_mode)
            {
                case FakeTerminalMode.Approved:
                    await _journal.MarkTerminalSubmittedAsync(
                        operationId,
                        "fake submit");
                    await _journal.TransitionTerminalAsync(
                        operationId,
                        "SENT",
                        "APPROVED",
                        "fake approved",
                        PaymentTerminalOutcome.Approved,
                        true,
                        "APPROVED",
                        "approved");
                    return new PaymentTerminalPaymentResult(
                        true,
                        "APPROVED",
                        "approved",
                        Outcome:PaymentTerminalOutcome.Approved,
                        RequestSubmitted:true,
                        OutcomeCode:"APPROVED");

                case FakeTerminalMode.NotSent:
                    return new PaymentTerminalPaymentResult(
                        false,
                        "NOT_SENT",
                        "connect failed",
                        Outcome:PaymentTerminalOutcome.NotSent,
                        RequestSubmitted:false,
                        OutcomeCode:"CONNECT_FAILED");

                case FakeTerminalMode.Declined:
                    await _journal.MarkTerminalSubmittedAsync(
                        operationId,
                        "fake submit");
                    await _journal.TransitionTerminalAsync(
                        operationId,
                        "SENT",
                        "NOT_CHARGED",
                        "fake decline",
                        PaymentTerminalOutcome.Declined,
                        true,
                        "DECLINED",
                        "declined");
                    return new PaymentTerminalPaymentResult(
                        false,
                        "DECLINED",
                        "declined",
                        Outcome:PaymentTerminalOutcome.Declined,
                        RequestSubmitted:true,
                        OutcomeCode:"DECLINED");

                case FakeTerminalMode.Unknown:
                    await _journal.MarkTerminalSubmittedAsync(
                        operationId,
                        "fake submit");
                    await _journal.TransitionTerminalAsync(
                        operationId,
                        "SENT",
                        "UNKNOWN",
                        "fake unknown",
                        PaymentTerminalOutcome.Unknown,
                        true,
                        "TIMEOUT",
                        "unknown");
                    return new PaymentTerminalPaymentResult(
                        false,
                        "UNGEKLAERT",
                        "unknown",
                        Outcome:PaymentTerminalOutcome.Unknown,
                        RequestSubmitted:true,
                        OutcomeCode:"TIMEOUT");

                case FakeTerminalMode.SuccessWithoutJournalApproval:
                    return new PaymentTerminalPaymentResult(
                        true,
                        "APPROVED",
                        "but journal unchanged",
                        Outcome:PaymentTerminalOutcome.Approved,
                        RequestSubmitted:true,
                        OutcomeCode:"APPROVED");

                default:
                    throw new InvalidOperationException();
            }
        }

        public Task<PaymentTerminalPaymentResult> RefundAsync(
            long amountCents,
            string operationId,
            CancellationToken ct=default) =>
            PayAsync(amountCents, operationId, ct);
    }

    private sealed class FakeJournal : ICheckoutJournal
    {
        public int BeginCount { get; private set; }
        public CheckoutOperation? Operation { get; private set; }

        public Task BeginAsync(CheckoutSnapshot snapshot)
        {
            BeginCount++;

            Operation=new CheckoutOperation(
                snapshot,
                snapshot.Method==PaymentMethod.Card
                    ? "PREPARED"
                    : "CASH_READY",
                "",
                null);

            return Task.CompletedTask;
        }

        public Task MarkTerminalSubmittedAsync(
            string id,
            string evidence)
        {
            Require(id);

            Operation=Operation! with
            {
                State="SENT",
                Evidence=evidence,
                TerminalRequestSubmitted=true
            };

            return Task.CompletedTask;
        }

        public Task TransitionTerminalAsync(
            string id,
            string expected,
            string state,
            string evidence,
            PaymentTerminalOutcome outcome,
            bool requestSubmitted,
            string terminalCode,
            string terminalMessage)
        {
            Require(id);

            if(Operation!.State!=expected)
                throw new InvalidOperationException("fake expected-state mismatch");

            Operation=Operation with
            {
                State=state,
                Evidence=evidence,
                TerminalOutcome=outcome,
                TerminalRequestSubmitted=requestSubmitted,
                TerminalCode=terminalCode,
                TerminalMessage=terminalMessage,
                Resolution=outcome switch
                {
                    PaymentTerminalOutcome.Approved =>
                        CheckoutResolution.AutoApproved,
                    PaymentTerminalOutcome.Declined or
                    PaymentTerminalOutcome.Cancelled or
                    PaymentTerminalOutcome.NotSent =>
                        CheckoutResolution.AutoNotCharged,
                    _ => CheckoutResolution.None
                }
            };

            return Task.CompletedTask;
        }

        public Task ResolveAsync(
            string id,
            string expected,
            bool paid,
            string actor,
            string evidence)
        {
            Require(id);

            if(Operation!.State!=expected)
                throw new InvalidOperationException("fake expected-state mismatch");

            if(!paid && expected=="APPROVED")
                throw new InvalidOperationException("cannot downgrade approved");

            Operation=Operation with
            {
                State=paid ? "APPROVED" : "NOT_CHARGED",
                Evidence=evidence,
                Resolution=paid
                    ? CheckoutResolution.ManualPaid
                    : CheckoutResolution.ManualNotCharged,
                ResolutionActor=actor,
                ResolutionAt=DateTimeOffset.Now.ToString("O")
            };

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CheckoutOperation>> GetOpenAsync()
        {
            IReadOnlyList<CheckoutOperation> result =
                Operation is not null &&
                Operation.State!="COMMITTED" &&
                Operation.State!="NOT_CHARGED"
                    ? new[] { Operation }
                    : Array.Empty<CheckoutOperation>();

            return Task.FromResult(result);
        }

        public Task<CheckoutOperation?> GetAsync(string id)
        {
            if(Operation?.Snapshot.OperationId!=id)
                return Task.FromResult<CheckoutOperation?>(null);

            return Task.FromResult<CheckoutOperation?>(Operation);
        }

        public Task<long?> FindSaleAsync(string id) =>
            Task.FromResult<long?>(null);

        private void Require(string id)
        {
            if(Operation?.Snapshot.OperationId!=id)
                throw new InvalidOperationException("fake operation not found");
        }
    }
}
