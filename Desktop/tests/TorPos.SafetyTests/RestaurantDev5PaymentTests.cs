using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

// Restaurant payments under concurrency and repetition, against the real
// repository and payment store: two windows on one table, a double click, a
// repeated commit, partial payments down to the last cent, and the table state
// after a payment that did not happen.
public static class RestaurantDev5PaymentTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var previousEdition = Environment.GetEnvironmentVariable("TOR_POS_PRODUCT_EDITION");
        try
        {
            Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", "RESTAURANT", EnvironmentVariableTarget.Process);
            var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(root, "restaurant-dev5-payment-" + Guid.NewGuid().ToString("N") + ".db"));
            var repo = new RestaurantRepository(db);
            var areaId = await repo.SaveAreaAsync("DEV5 Zahlung", 1);

            await TwoWindows(db, repo, areaId, assert);
            await DoubleClickAndRepeatedCommit(db, repo, areaId, assert);
            await PartialPaymentsToTheLastCent(db, repo, areaId, assert);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", previousEdition, EnvironmentVariableTarget.Process);
            SqliteConnection.ClearAllPools();
        }
    }

    private static int _table;

    private static async Task<(RestaurantTableSession Session, RestaurantSessionItem Item)> TableWithItemAsync(
        RestaurantRepository repo, long areaId, decimal quantity, long priceCents, string unit)
    {
        var n = Interlocked.Increment(ref _table);
        var tableId = await repo.SaveTableAsync(areaId, $"P{n}", $"Zahlung {n}", 4, n);
        var session = await repo.OpenTableAsync(tableId, "DEV5-TEST", 2, "KASSE-DEV5");
        var product = new Product { Id = 960000 + n, Name = $"Artikel {n}", BasePriceCents = priceCents, VatRate = 19m, Unit = unit, IsActive = true };
        var item = await repo.AddItemAsync(session.Id, session.Version, product, quantity, "DEV5-TEST", "KASSE-DEV5");
        return (session, item);
    }

    private static async Task<RestaurantCheckoutDraft> DraftAsync(RestaurantRepository repo, string sessionId, long itemId, long quantityMilli)
    {
        var current = await repo.GetSessionAsync(sessionId) ?? throw new InvalidOperationException("session missing");
        return await repo.BuildCheckoutDraftAsync(current.Id, current.Version, new[] { new RestaurantSplitSelection(itemId, quantityMilli) });
    }

    // The committed sale applies the reservation (as SaleRepository does inside
    // its commit transaction); the sale row itself is not under test.
    private static async Task ApplyAsync(SqliteDatabase db, RestaurantCheckoutDraft draft, long saleId)
    {
        await using var c = db.OpenConnection();
        await using (var fk = c.CreateCommand()) { fk.CommandText = "PRAGMA foreign_keys=OFF;"; await fk.ExecuteNonQueryAsync(); }
        await using var tx = c.BeginTransaction();
        var snapshot = new CheckoutSnapshot(draft.OperationId, CheckoutSnapshot.CopyLines(draft.Lines, true), 0, PaymentMethod.Cash, "DEV5-TEST", null, draft.ImHaus);
        await RestaurantPaymentStore.ApplyCommittedSaleAsync(c, tx, snapshot, saleId, CancellationToken.None);
        await tx.CommitAsync();
    }

    private static async Task<long> ScalarAsync(SqliteDatabase db, string sql, params (string Name, object Value)[] args)
    {
        await using var c = db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = sql;
        foreach (var (name, value) in args) q.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(await q.ExecuteScalarAsync());
    }

    private static async Task<string> TryAsync(Func<Task> action)
    {
        try { await action(); return ""; }
        catch (InvalidOperationException ex) { return ex.Message; }
    }

    // Two tills/windows open BEZAHLEN on the same table at the same time.
    private static async Task TwoWindows(SqliteDatabase db, RestaurantRepository repo, long areaId, Action<bool, string> assert)
    {
        var (session, item) = await TableWithItemAsync(repo, areaId, 2m, 450, "Stück");
        var windowA = await DraftAsync(repo, session.Id, item.Id, 2000);
        var windowB = await DraftAsync(repo, session.Id, item.Id, 2000);
        var results = await Task.WhenAll(
            TryAsync(() => repo.PreparePaymentReservationAsync(windowA)),
            TryAsync(() => repo.PreparePaymentReservationAsync(windowB)));
        var prepared = await repo.ListPreparedPaymentReservationOperationIdsAsync();
        var mine = prepared.Where(x => x == windowA.OperationId || x == windowB.OperationId).ToArray();

        assert(
            results.Count(r => r.Length == 0) == 1 && results.Count(r => r.Length > 0) == 1 &&
            mine.Length == 1 &&
            (await repo.GetSessionAsync(session.Id))?.State == RestaurantTableSessionState.CheckRequested &&
            await ScalarAsync(db, "SELECT COUNT(*) FROM restaurant_payment_reservations WHERE session_id=$s", ("$s", session.Id)) == 1,
            "DEV5 payment: two windows starting BEZAHLEN on one table at the same time - exactly one reservation, the other is refused, the table is locked once");

        await repo.CancelPaymentReservationAsync(mine[0]);
        assert(
            (await repo.GetSessionAsync(session.Id))?.State == RestaurantTableSessionState.Open &&
            await ScalarAsync(db, "SELECT paid_cents FROM restaurant_session_items WHERE id=$i", ("$i", item.Id)) == 0 &&
            await ScalarAsync(db, "SELECT COUNT(*) FROM restaurant_session_items WHERE id=$i AND state='ACTIVE' AND quantity_milli=2000", ("$i", item.Id)) == 1,
            "DEV5 payment: after a payment that did not happen (cancel, short cash, declined) the table is open with every position unpaid and unchanged");
    }

    // Double click on BEZAHLEN, and a commit that is repeated after a restart.
    private static async Task DoubleClickAndRepeatedCommit(SqliteDatabase db, RestaurantRepository repo, long areaId, Action<bool, string> assert)
    {
        var (session, item) = await TableWithItemAsync(repo, areaId, 1m, 1290, "Stück");
        var draft = await DraftAsync(repo, session.Id, item.Id, 1000);
        await repo.PreparePaymentReservationAsync(draft);
        var second = await TryAsync(() => repo.PreparePaymentReservationAsync(draft));
        assert(
            second.Length > 0 &&
            await ScalarAsync(db, "SELECT COUNT(*) FROM restaurant_payment_reservations WHERE operation_id=$o", ("$o", draft.OperationId)) == 1,
            "DEV5 payment: a double click prepares the same payment once - the second start is refused, one reservation row");

        await ApplyAsync(db, draft, 960101);
        var paidState = await ScalarAsync(db, "SELECT COUNT(*) FROM restaurant_session_items WHERE id=$i AND state='PAID'", ("$i", item.Id));
        var lineAfterFirst = await ScalarAsync(db, "SELECT line_total_cents FROM restaurant_session_items WHERE id=$i", ("$i", item.Id));
        var repeated = await TryAsync(() => ApplyAsync(db, draft, 960101));
        assert(
            repeated.Length == 0 && paidState == 1 &&
            await ScalarAsync(db, "SELECT line_total_cents FROM restaurant_session_items WHERE id=$i", ("$i", item.Id)) == lineAfterFirst &&
            await ScalarAsync(db, "SELECT COUNT(*) FROM restaurant_payment_reservations WHERE operation_id=$o AND state='APPLIED'", ("$o", draft.OperationId)) == 1,
            "DEV5 payment: repeating the commit of an applied payment (retry after restart) changes nothing - no second payment of the same positions");

        var again = await TryAsync(() => DraftAsync(repo, session.Id, item.Id, 1000));
        assert(
            again.Length > 0,
            "DEV5 payment: a fully paid position can not be selected for payment again");
    }

    // A 1,5 l open-wine position paid in three parts: every part is charged once and
    // the parts add up to the line total to the cent, including the odd cent.
    private static async Task PartialPaymentsToTheLastCent(SqliteDatabase db, RestaurantRepository repo, long areaId, Action<bool, string> assert)
    {
        var (session, item) = await TableWithItemAsync(repo, areaId, 1.5m, 999, "l");
        var lineTotal = await ScalarAsync(db, "SELECT line_total_cents FROM restaurant_session_items WHERE id=$i", ("$i", item.Id));
        var paidParts = new List<long>();
        var states = new List<RestaurantTableSessionState>();
        (long Shown, long Open, long WineLeft) openAfterFirstPart = default;
        var over = "";
        for (var part = 0; part < 3; part++)
        {
            if (part == 1)
            {
                // More than what is left is refused, not charged.
                over = await TryAsync(async () => await DraftAsync(repo, session.Id, item.Id, 1500));
            }
            var draft = await DraftAsync(repo, session.Id, item.Id, 500);
            await repo.PreparePaymentReservationAsync(draft);
            await ApplyAsync(db, draft, 960200 + part);
            paidParts.Add(draft.TotalCents);
            states.Add((await repo.GetSessionAsync(session.Id))!.State);
            if (part == 0)
            {
                var open = await ScalarAsync(db, "SELECT COALESCE(SUM(line_total_cents-paid_cents),0) FROM restaurant_session_items i JOIN restaurant_sessions s ON s.id=i.session_id WHERE s.state IN ('OPEN','CHECK_REQUESTED') AND i.state='ACTIVE' AND s.assigned_waiter='DEV5-TEST'");
                var row = (await new RestaurantWaiterSettlementService(db).BuildForOpenPeriodAsync()).Rows.Single(x => x.Waiter == "DEV5-TEST");
                openAfterFirstPart = (row.OpenTablesCents, open, lineTotal - draft.TotalCents);
            }
        }

        var openRows = await ScalarAsync(db, "SELECT COUNT(*) FROM restaurant_session_items WHERE session_id=$s AND state='ACTIVE'", ("$s", session.Id));
        assert(
            lineTotal == 1499 && paidParts.Sum() == lineTotal && paidParts.All(p => p > 0) &&
            openRows == 0 && over.Length > 0,
            $"DEV5 payment: an open-wine line of 1,5 l (14,99 EUR) paid in three parts ({string.Join(" + ", paidParts)} cents) adds up exactly, the odd cent is neither lost nor charged twice, and selecting more than is left is refused");
        assert(
            states.Count == 3 &&
            states[0] == RestaurantTableSessionState.Open && states[1] == RestaurantTableSessionState.Open &&
            states[2] == RestaurantTableSessionState.Closed,
            $"DEV5 payment: the table stays open while anything is unpaid and closes only with the payment of the last part ({string.Join(" -> ", states)})");
        assert(
            openAfterFirstPart.WineLeft == 999 && openAfterFirstPart.Shown == openAfterFirstPart.Open &&
            openAfterFirstPart.Shown == 900 + 999,
            $"DEV5 payment: the Kellnerabrechnung shows a partly paid table with what is still open (14,99 - 5,00 = 9,99 EUR here, plus the other open table), not the full amount ({openAfterFirstPart.Shown} cents)");
    }
}
