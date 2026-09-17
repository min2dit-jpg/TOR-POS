using TorPos.Core;
using TorPos.Infrastructure;

// R106: three real bugs, all found by the user's own review of the shipped
// software rather than by this session's own testing, and confirmed
// against the actual code before fixing:
// 1. DigitalReceiptHtml's (R103) VAT summary computed VAT on the
//    PRE-discount gross while showing the discount as an unrelated extra
//    line - overstating VAT by the discount's own share of it. Fixed by
//    extracting the correct, already-existing printed-receipt formula
//    into a shared TorPos.Core.VatSummaryCalculator both now use.
// 2. RecordReturnAsync (R82) never prorated the original Bon's manual
//    discount onto a partial return, crediting the item's full
//    undiscounted price - more than the customer actually paid for it.
// 3. R102's card-refund flow had no durable lock against a duplicate
//    refund attempt after an ambiguous ("Unknown") terminal outcome -
//    unlike the forward checkout direction's checkout_operations/
//    ux_checkout_one_open. ICardRefundLockRepository closes this gap.
public static class R106ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert,
        Func<Func<Task>, string, Task> reject)
    {
        var dir = Path.Combine(root, "r106-audit-fixes");
        Directory.CreateDirectory(dir);

        // 1) The exact scenario from the user's own finding: a 119,00 EUR
        // sale (19% VAT) with a 10% (11,90 EUR) manual discount must show
        // 17,10 EUR VAT (on the 107,10 EUR actually paid), not 19,00 EUR
        // (on the undiscounted 119,00 EUR).
        var line = new CartLine { ProductName = "R106 Artikel", Quantity = 1, UnitPriceCents = 11900, VatRate = 19m };
        var vatGroups = VatSummaryCalculator.Compute(new[] { line }, discountCents: 1190);
        assert(
            vatGroups.Count == 1 && vatGroups[0].GrossCents == 10710 && vatGroups[0].TaxCents == 1710,
            $"R106 VatSummaryCalculator correctly reduces VAT to 17,10 EUR (1710 cents) on a 119,00 EUR/19% sale with a 10% discount, not 19,00 EUR on the undiscounted gross (actual: gross={vatGroups[0].GrossCents}, tax={vatGroups[0].TaxCents})");

        // 2) The digital receipt's own VAT groups reflect the fix, not just
        // the underlying calculator - guards against a second hand-written
        // formula creeping back in. R145: the document TOR Cloud shows.
        var discountedJob = new ReceiptPrintJob(106001, DateTimeOffset.Now, "R106 Laden", "", "", "", "", "", "Bar", 1190, 10710, new[] { line });
        var document = DigitalReceiptDocument.From(discountedJob, DigitalReceiptDocument.PaymentsFor(PaymentMethod.Cash, 10710, 0));
        assert(
            document.Vat.Count == 1 && document.Vat[0].TaxCents == 1710 && document.Vat[0].GrossCents == 10710,
            $"R106 the digital receipt shows 17,10 EUR VAT for a discounted sale, not 19,00 EUR (actual: {document.Vat[0].TaxCents})");

        // 3) DiscountProration.Prorate: a 50,00 EUR item out of a 100,00 EUR
        // Bon (subtotal) actually billed at 90,00 EUR (10 EUR discount)
        // returns 45,00 EUR, not the full 50,00 EUR.
        var prorated = DiscountProration.Prorate(rawSubsetAmountCents: 5000, originalSubtotalCents: 10000, originalTotalCents: 9000);
        assert(
            prorated == 4500,
            $"R106 DiscountProration.Prorate returns 45,00 EUR (4500 cents) for a 50,00 EUR item out of a 100,00/90,00 EUR discounted Bon, not the full undiscounted 50,00 EUR (actual: {prorated})");

        // A Bon with NO discount at all must prorate to exactly the raw
        // amount, unchanged - the fix must never affect an undiscounted return.
        var notProrated = DiscountProration.Prorate(rawSubsetAmountCents: 5000, originalSubtotalCents: 10000, originalTotalCents: 10000);
        assert(
            notProrated == 5000,
            "R106 DiscountProration.Prorate leaves the amount unchanged when the original Bon had no discount at all");

        // 4) ICardRefundLockRepository: the full lifecycle, fully testable
        // end-to-end (unlike RecordStornoAsync/RecordReturnAsync's own
        // FiscalRelease-gated completion) since locking a Bon against a
        // duplicate refund attempt is not itself a fiscal booking.
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r106.db"));
        var locks = new CardRefundLockRepository(db, new AuditLogRepository(db));

        assert(
            !await locks.HasUnresolvedAsync(999001),
            "R106 a sale with no refund attempts at all reports no unresolved lock");

        var attemptId = await locks.BeginAsync(999001, "STORNO", 5000);
        assert(
            await locks.HasUnresolvedAsync(999001),
            "R106 BeginAsync immediately locks the sale (pessimistic - locked before the terminal call, not after)");

        await reject(
            () => locks.BeginAsync(999001, "STORNO", 5000),
            "R106 a second BeginAsync for the SAME sale while one is still unresolved is refused (the partial unique index, not just the app-level HasUnresolvedAsync check)");

        var unresolved = await locks.GetUnresolvedAsync();
        assert(
            unresolved.Any(x => x.Id == attemptId && x.OriginalSaleId == 999001 && x.Kind == "STORNO" && x.AmountCents == 5000),
            "R106 GetUnresolvedAsync lists the open attempt with its correct sale id, kind, and amount");

        await locks.ResolveAsync(attemptId, "tester", "Bank bestätigt: keine Belastung erfolgt.");
        assert(
            !await locks.HasUnresolvedAsync(999001),
            "R106 ResolveAsync clears the lock - the sale can be storno'd/retourniert again afterward");

        await reject(
            () => locks.ResolveAsync(attemptId, "tester", "erneuter Versuch"),
            "R106 resolving the same attempt twice is refused, not silently accepted");

        // A DEFINITE outcome (Approved/Declined/...) uses ClearAsync
        // directly, never leaving a lingering lock at all.
        var secondAttemptId = await locks.BeginAsync(999002, "RETURN", 3000);
        await locks.ClearAsync(secondAttemptId);
        assert(
            !await locks.HasUnresolvedAsync(999002),
            "R106 ClearAsync (used for a definite Approved/Declined/Cancelled/NotSent outcome) removes the lock immediately, no manual resolution needed");

        // Different sales never interfere with each other's locks.
        var thirdAttemptId = await locks.BeginAsync(999003, "STORNO", 1000);
        assert(
            await locks.HasUnresolvedAsync(999003) && !await locks.HasUnresolvedAsync(999002),
            "R106 a lock on one sale never blocks or reports as unresolved for a different sale");
    }
}
