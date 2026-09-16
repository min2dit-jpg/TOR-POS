using System.Linq;
using TorPos.Core;
using TorPos.Infrastructure;

// R96: fixes a gap found while reviewing R95 in high-effort mode - the
// Im-Haus/Außer-Haus choice (_imHaus) was never carried through Parken
// (IMBISS ORDER prep-then-pay-later) or crash-recovery cart restore, both
// of which silently reverted to the Außer-Haus default. Fixed by
// persisting the choice itself on parked_receipts (new im_haus column,
// via the same EnsureColumnAsync pattern as preparation_state/order_note)
// and on the crash-recovery JSON snapshot - deliberately NOT by baking the
// VAT rate into the parked cart lines, since that could leave a
// newly-added item on a reopened order at the wrong rate while old items
// sat at the elevated one. CaptureCheckout's existing CopyLines(_imHaus)
// transform at final payment already does the actual VAT elevation
// correctly once the flag itself is restored.
public static class R96ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert,
        Func<Func<Task>, string, Task> reject)
    {
        var dir = Path.Combine(root, "r96-im-haus-park");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r96.db"));
        var orders = new ParkedReceiptRepository(db);

        var lines = new[] { new CartLine { ProductName = "R96 Döner", Quantity = 1, UnitPriceCents = 700, VatRate = 7m } };

        var parkedImHaus = await orders.ParkAsync(lines, 0, "tester", imHaus: true);
        assert(
            parkedImHaus.ImHaus,
            "R96 ParkAsync's own return value carries the Im-Haus choice, not just a default");

        var reloadedById = await orders.GetOpenByIdAsync(parkedImHaus.Id);
        assert(
            reloadedById!.ImHaus,
            "R96 GetOpenByIdAsync restores the Im-Haus choice after a fresh reload - this is what OnOrderBoardClick/the Geparkte-Bons flow reopen into the live cart");

        var reloadedList = (await orders.GetOpenAsync()).Single(x => x.Id == parkedImHaus.Id);
        assert(
            reloadedList.ImHaus,
            "R96 GetOpenAsync's list view also carries the Im-Haus choice, not just the single-lookup path");

        var parkedTakeaway = await orders.ParkAsync(lines, 0, "tester");
        assert(
            !parkedTakeaway.ImHaus && !(await orders.GetOpenByIdAsync(parkedTakeaway.Id))!.ImHaus,
            "R96 a caller that never mentions Im Haus still defaults to Außer Haus (false), same as before this fix");

        // A cashier can still change their mind while an order is open -
        // UpdateAsync must be able to flip the choice on an existing park.
        await orders.UpdateAsync(parkedTakeaway.Id, lines, 0, imHaus: true);
        var afterUpdate = await orders.GetOpenByIdAsync(parkedTakeaway.Id);
        assert(
            afterUpdate!.ImHaus,
            "R96 UpdateAsync can flip an already-parked order from Außer Haus to Im Haus");

        await orders.UpdateAsync(parkedTakeaway.Id, lines, 0);
        var afterUpdateBack = await orders.GetOpenByIdAsync(parkedTakeaway.Id);
        assert(
            !afterUpdateBack!.ImHaus,
            "R96 UpdateAsync can also flip it back to Außer Haus, not just set it once");
    }
}
