using TorPos.Core;
using TorPos.Infrastructure;

// R124: the IMBISS pickup number counts per service period (Tagesabschluss to
// Tagesabschluss) instead of per calendar day, and wraps after 999.
//
// Before: every counter was keyed "pickup.<Berlin date>", so a service running
// past midnight restarted the queue at 001 while orders 080-087 were still
// waiting - and R48ReviewTests asserted that reset as correct.
public static class R124ReviewTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "r124-pickup");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r124.db"));
        var orders = new ParkedReceiptRepository(db);
        var sales = new SaleRepository(db);

        var products = new ProductRepository(db);
        var category = (await products.GetCategoriesAsync()).First();
        var productId = await products.SaveAsync(
            new Product { CategoryId = category.Id, Name = "R124 Döner", Barcode = "4001240000015", BasePriceCents = 750 });
        var line = new CartLine
        {
            ProductId = productId, ProductName = "R124 Döner", Barcode = "4001240000015",
            Quantity = 1m, UnitPriceCents = 750, VatRate = category.VatRate
        };

        Task<ParkedReceipt> Order(bool training = false) =>
            orders.ParkAsync(new[] { line }, 0, "r124", assignPickupNumber: true, training: training);

        var first = await Order();
        var second = await Order();
        assert(
            first.PickupNumber == 1 && second.PickupNumber == 2,
            $"R124 accepted orders count up within a service period ({first.PickupNumber}, {second.PickupNumber})");

        using (var c = db.OpenConnection())
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT COUNT(*) FROM app_sequence WHERE key GLOB 'pickup.[0-9]*';";
            assert(
                Convert.ToInt64(q.ExecuteScalar()) == 0,
                "R124 no counter is keyed by a calendar date any more, so the clock passing midnight cannot reset the queue");
        }

        // The Tagesabschluss is what starts a new queue.
        await sales.RecordDailyClosingAsync("r124");
        var afterClosing = await Order();
        assert(
            afterClosing.PickupNumber == 1,
            $"R124 the first order after the Tagesabschluss starts again at 001 (actual: {afterClosing.PickupNumber})");

        var stillOpen = await orders.GetOpenByIdAsync(second.Id);
        assert(
            stillOpen is not null && stillOpen.PickupNumber == 2,
            "R124 an order accepted before the closing keeps its number - nothing already handed out is renumbered");

        var training = await Order(training: true);
        var real = await Order();
        assert(
            training.PickupNumber == 1 && real.PickupNumber == 2,
            $"R124 training orders keep their own counter within the same period (training {training.PickupNumber}, real {real.PickupNumber})");

        // A till whose operator never closes the day must not count to 1000+.
        using (var c = db.OpenConnection())
        using (var q = c.CreateCommand())
        {
            q.CommandText = "UPDATE app_sequence SET value=999 WHERE key=(SELECT 'pickup.period.' || COALESCE(MAX(id),0) FROM daily_closings);";
            assert(q.ExecuteNonQuery() == 1, "R124 the current period's real counter is found under its period key");
        }
        var wrapped = await Order();
        assert(
            wrapped.PickupNumber == 1,
            $"R124 after 999 the number wraps to 001 instead of growing past the three digits every screen and ticket prints (actual: {wrapped.PickupNumber})");

        var simulation = await PickupSequence.NextSimulationAsync(db, training: false);
        var nextReal = await Order();
        assert(
            simulation == 1 && nextReal.PickupNumber == 2,
            "R124 simulation numbers still never consume the real queue");
    }
}
