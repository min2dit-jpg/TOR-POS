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
        var terminal = new FakeTerminal(journal);
        var service = new CheckoutApplicationService(
            new FakeCompliance(),
            journal,
            terminal,
            new FakeCatalog(catalogProducts));

        var allowedMixed = await service.PrepareProductionAsync(snapshot);
        var allocatedLine = allowedMixed.Operation?.Snapshot.Lines.Single();
        assert(
            allowedMixed.Disposition == CheckoutApplicationDisposition.ReadyToCommit &&
            journal.BeginCount == 1 &&
            terminal.PayCount == 1 &&
            allocatedLine is not null &&
            allocatedLine.ProductName == "Döner Menü" &&
            allocatedLine.VatAllocations.Length == 2 &&
            allocatedLine.VatAllocations.Single(x => x.VatRate == 7m).GrossCents == 630 &&
            allocatedLine.VatAllocations.Single(x => x.VatRate == 19m).GrossCents == 270,
            "R151 mixed-rate menu stays one commercial Döner Menü line while checkout carries hidden 7%/19% gross allocations");

        var vatSummary = VatSummaryCalculator.Compute(
            allowedMixed.Operation!.Snapshot.Lines,
            allowedMixed.Operation.Snapshot.DiscountCents);
        assert(
            vatSummary.Count == 2 &&
            vatSummary.Sum(x => x.GrossCents) == 900 &&
            vatSummary.Single(x => x.Rate == 7m).GrossCents == 630 &&
            vatSummary.Single(x => x.Rate == 19m).GrossCents == 270,
            "R151 receipt/TSE VAT summary reads hidden menu allocations without expanding customer-facing lines");

        var quantityTwo = MenuVatPolicy.LineAllocations(new CartLine
        {
            ProductId = menu.Id,
            ProductName = menu.Name,
            Quantity = 2m,
            UnitPriceCents = 900,
            VatRate = 7m,
            VatAllocations = takeAway.Allocations.ToArray()
        });
        assert(
            quantityTwo.Sum(x => x.GrossCents) == 1800 &&
            quantityTwo.Single(x => x.VatRate == 7m).GrossCents == 1260 &&
            quantityTwo.Single(x => x.VatRate == 19m).GrossCents == 540,
            "R151 menu allocation scales deterministically with quantity and still reconciles to the commercial line total");

        var inHouseJournal = new FakeJournal();
        var inHouseTerminal = new FakeTerminal(inHouseJournal);
        var inHouseService = new CheckoutApplicationService(
            new FakeCompliance(),
            inHouseJournal,
            inHouseTerminal,
            new FakeCatalog(catalogProducts));
        var inHouseSnapshot = snapshot with { OperationId = "r151-inhouse", ImHaus = true, Method = PaymentMethod.Cash };
        var allowedInHouse = await inHouseService.PrepareProductionAsync(inHouseSnapshot);
        assert(
            allowedInHouse.Disposition == CheckoutApplicationDisposition.ReadyToCommit &&
            allowedInHouse.Operation!.Snapshot.Lines.Single().VatAllocations.Length == 1 &&
            allowedInHouse.Operation.Snapshot.Lines.Single().VatAllocations.Single().VatRate == 19m &&
            allowedInHouse.Operation.Snapshot.Lines.Single().VatAllocations.Single().GrossCents == 900,
            "R151 in-house menu stores one hidden 19% bucket when all effective component rates are 19%");

        var promotionSnapshot = snapshot with
        {
            OperationId = "r151-mixed-promotion",
            Method = PaymentMethod.Cash,
            Lines = new[]
            {
                snapshot.Lines[0] with
                {
                    PromotionId = 88,
                    PromotionName = "Extra Angebot",
                    PromotionPercent = 10,
                    PromotionDiscountUnitCents = 90
                }
            }
        };
        var promotionJournal = new FakeJournal();
        var promotionService = new CheckoutApplicationService(
            new FakeCompliance(),
            promotionJournal,
            new FakeTerminal(promotionJournal),
            new FakeCatalog(catalogProducts));
        var blockedPromotion = await promotionService.PrepareProductionAsync(promotionSnapshot);
        assert(
            blockedPromotion.Disposition == CheckoutApplicationDisposition.FiscalBlocked &&
            blockedPromotion.FiscalReadiness.Items.Single().Code == "MENU_VAT_ALLOCATION" &&
            promotionJournal.BeginCount == 0,
            "R151 additional promotion on a mixed-VAT menu remains fail-closed until immutable Preisfindung allocation is represented");
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
        private readonly FakeJournal _journal;
        public FakeTerminal(FakeJournal journal) => _journal = journal;
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
            await _journal.TransitionTerminalAsync(
                operationId,
                "PREPARED",
                "APPROVED",
                "R151 fake approval",
                PaymentTerminalOutcome.Approved,
                requestSubmitted: true,
                terminalCode: "APPROVED",
                terminalMessage: "approved");
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
