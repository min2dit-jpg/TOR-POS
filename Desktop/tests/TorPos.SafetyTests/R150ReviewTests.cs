using TorPos.Application;
using TorPos.Core;

public static class R150ReviewTests
{
    public static async Task Run(Action<bool, string> assert)
    {
        var food = new Product
        {
            Id = 1501,
            Name = "Döner",
            BasePriceCents = 700,
            VatRate = 7m,
            ImHausApplicable = true
        };
        var cola = new Product
        {
            Id = 1502,
            Name = "Cola",
            BasePriceCents = 300,
            VatRate = 19m,
            ImHausApplicable = false
        };
        var menu = new Product
        {
            Id = 1503,
            Name = "Döner Menü",
            BasePriceCents = 900,
            VatRate = 7m,
            ComboItems = new[]
            {
                new ProductComboItem(1503, 1501, "Döner", 1m, 0),
                new ProductComboItem(1503, 1502, "Cola", 1m, 1)
            }
        };
        var catalogProducts = new[] { food, cola, menu };

        var takeAway = MenuVatPolicy.Analyze(menu, catalogProducts, imHaus: false);
        assert(
            takeAway.IsValid && takeAway.IsMixed &&
            takeAway.Allocations.Single(x => x.VatRate == 7m).GrossCents == 630 &&
            takeAway.Allocations.Single(x => x.VatRate == 19m).GrossCents == 270,
            "R150 Marktwertmethode splits a 9.00 EUR menu from 7.00/3.00 single prices into 6.30 EUR at 7% and 2.70 EUR at 19%");

        var inHouse = MenuVatPolicy.Analyze(menu, catalogProducts, imHaus: true);
        assert(
            inHouse.IsValid && !inHouse.IsMixed &&
            inHouse.Allocations.Single().VatRate == 19m &&
            inHouse.Allocations.Single().GrossCents == 900,
            "R150 Im-Haus rule is applied to menu components before deciding whether the menu is mixed-rate");

        var promoted = MenuVatPolicy.Analyze(menu, catalogProducts, imHaus: false, menuGrossCents: 850);
        assert(
            promoted.IsValid &&
            promoted.Allocations.Single(x => x.VatRate == 7m).GrossCents == 595 &&
            promoted.Allocations.Single(x => x.VatRate == 19m).GrossCents == 255,
            "R150 actual sold menu price is allocated proportionally and cent rounding remains deterministic");

        var overpriced = MenuVatPolicy.Analyze(menu, catalogProducts, imHaus: false, menuGrossCents: 1100);
        assert(
            !overpriced.IsValid && overpriced.Message.Contains("übersteigt"),
            "R150 refuses a menu price above the sum of known single-sale market values instead of inventing an allocation");

        var bottle = new Product
        {
            Id = 1504,
            Name = "Flasche",
            BasePriceCents = 200,
            PfandCents = 25,
            VatRate = 19m
        };
        var depositMenu = new Product
        {
            Id = 1505,
            Name = "Menü mit Pfand",
            BasePriceCents = 900,
            VatRate = 7m,
            ComboItems = new[]
            {
                new ProductComboItem(1505, 1501, "Döner", 1m, 0),
                new ProductComboItem(1505, 1504, "Flasche", 1m, 1)
            }
        };
        var depositAnalysis = MenuVatPolicy.Analyze(
            depositMenu,
            new[] { food, bottle, depositMenu },
            imHaus: false);
        assert(
            !depositAnalysis.IsValid && depositAnalysis.Message.Contains("Pfand"),
            "R150 combo Pfand is blocked until it can be represented as its own immutable fiscal position");

        var snapshot = new CheckoutSnapshot(
            "r150-mixed-menu",
            new[]
            {
                new CartLine
                {
                    ProductId = menu.Id,
                    ProductName = menu.Name,
                    Quantity = 1m,
                    UnitPriceCents = 900,
                    ListUnitPriceCents = 900,
                    VatRate = 7m
                }
            },
            0,
            PaymentMethod.Card,
            "tester",
            null,
            ImHaus: false);

        var journal = new FakeJournal();
        var terminal = new FakeTerminal();
        var service = new CheckoutApplicationService(
            new FakeCompliance(),
            journal,
            terminal,
            new FakeCatalog(catalogProducts));

        var blocked = await service.PrepareProductionAsync(snapshot);
        assert(
            blocked.Disposition == CheckoutApplicationDisposition.FiscalBlocked &&
            blocked.FiscalReadiness.Items.Single().Code == "MENU_MIXED_VAT" &&
            journal.BeginCount == 0 &&
            terminal.PayCount == 0,
            "R150 mixed-rate menu is blocked before checkout journal and terminal side effects");

        var inHouseSnapshot = snapshot with { OperationId = "r150-inhouse", ImHaus = true, Method = PaymentMethod.Cash };
        var allowed = await service.PrepareProductionAsync(inHouseSnapshot);
        assert(
            allowed.Disposition == CheckoutApplicationDisposition.ReadyToCommit &&
            journal.BeginCount == 1 &&
            terminal.PayCount == 0,
            "R150 same menu is not blocked when effective component rates are uniformly 19% in-house");
    }

    private sealed class FakeCatalog : IProductCatalog
    {
        public FakeCatalog(IReadOnlyList<Product> products) => Products = products;
        public IReadOnlyList<ProductGroup> Groups => Array.Empty<ProductGroup>();
        public IReadOnlyList<Category> Categories => Array.Empty<Category>();
        public IReadOnlyList<Product> Products { get; }
        public ValueTask ReloadAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public bool TryGetByBarcode(string barcode, out Product? product)
        {
            product = null;
            return false;
        }
        public IReadOnlyList<Product> GetByCategory(long categoryId) => Array.Empty<Product>();
    }

    private sealed class FakeCompliance : IFiscalComplianceService
    {
        public Task<FiscalReadinessReport> CheckAsync(CancellationToken ct = default) =>
            Task.FromResult(new FiscalReadinessReport(
                true,
                "PRODUKTIV",
                "R150",
                "2.4",
                Array.Empty<FiscalReadinessItem>()));
    }

    private sealed class FakeJournal : ICheckoutJournal
    {
        public int BeginCount { get; private set; }
        private CheckoutOperation? _operation;

        public Task BeginAsync(CheckoutSnapshot snapshot)
        {
            BeginCount++;
            _operation = new CheckoutOperation(
                snapshot,
                snapshot.EffectiveCardPortionCents > 0 ? "PREPARED" : "CASH_READY",
                "",
                null);
            return Task.CompletedTask;
        }

        public Task MarkTerminalSubmittedAsync(string id, string evidence)
        {
            _operation = _operation! with { State = "SENT", Evidence = evidence, TerminalRequestSubmitted = true };
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
            _operation = _operation! with
            {
                State = state,
                Evidence = evidence,
                TerminalOutcome = outcome,
                TerminalRequestSubmitted = requestSubmitted,
                TerminalCode = terminalCode,
                TerminalMessage = terminalMessage
            };
            return Task.CompletedTask;
        }

        public Task ResolveAsync(string id, string expected, bool paid, string actor, string evidence) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<CheckoutOperation>> GetOpenAsync() =>
            Task.FromResult<IReadOnlyList<CheckoutOperation>>(_operation is null ? Array.Empty<CheckoutOperation>() : new[] { _operation });

        public Task<CheckoutOperation?> GetAsync(string id) =>
            Task.FromResult(_operation?.Snapshot.OperationId == id ? _operation : null);

        public Task<long?> FindSaleAsync(string id) => Task.FromResult<long?>(null);
    }

    private sealed class FakeTerminal : IPaymentTerminalService
    {
        public int PayCount { get; private set; }
        public IReadOnlyList<PaymentTerminalProfile> Profiles => Array.Empty<PaymentTerminalProfile>();

        public Task<PaymentTerminalProbeResult> ProbeAsync(CancellationToken ct = default) =>
            Task.FromResult(new PaymentTerminalProbeResult(true, "OK", "R150 fake", "fake"));

        public Task<PaymentTerminalProbeResult> RegisterAsync(CancellationToken ct = default) => ProbeAsync(ct);
        public Task<PaymentTerminalProbeResult> EndOfDayAsync(CancellationToken ct = default) => ProbeAsync(ct);

        public Task<PaymentTerminalPaymentResult> RefundAsync(long amountCents, string operationId, CancellationToken ct = default) =>
            PayAsync(amountCents, operationId, ct);

        public async Task<PaymentTerminalPaymentResult> PayAsync(long amountCents, string operationId, CancellationToken ct = default)
        {
            PayCount++;
            return new PaymentTerminalPaymentResult(
                true,
                "APPROVED",
                "approved",
                Outcome: PaymentTerminalOutcome.Approved,
                RequestSubmitted: true,
                OutcomeCode: "APPROVED");
        }
    }
}
