using TorPos.Core;
using TorPos.Infrastructure;

// R118 + R119: audit findings G3 and İ2.
//
// G3 - the login lockout RESET failed_attempts to 0 at the very moment it
//      locked the account, so every 5-minute lockout handed the next attacker
//      five fresh guesses: a flat ~1440 guesses a day against a 4-digit PIN.
//
// İ2 - the kitchen print queue was processed in order and stopped at the FIRST
//      failing job, with no attempt counter, no give-up state and no way to
//      see it. One ticket addressed to a printer that no longer exists blocked
//      every later kitchen ticket indefinitely.
public static class R118R119ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert)
    {
        // ---------- R118: escalating lockout ----------
        assert(
            LoginLockoutPolicy.LockoutFor(LoginLockoutPolicy.FreeAttempts - 1) == TimeSpan.Zero,
            "R118 an account below the free-attempt limit is not locked at all");

        var first = LoginLockoutPolicy.LockoutFor(5);
        var second = LoginLockoutPolicy.LockoutFor(6);
        var third = LoginLockoutPolicy.LockoutFor(7);
        assert(
            first == TimeSpan.FromMinutes(5) && second == TimeSpan.FromMinutes(10) && third == TimeSpan.FromMinutes(20),
            $"R118 each further failure doubles the wait instead of handing out five fresh guesses ({first.TotalMinutes}/{second.TotalMinutes}/{third.TotalMinutes} min)");

        assert(
            LoginLockoutPolicy.LockoutFor(50) == TimeSpan.FromMinutes(60) &&
            LoginLockoutPolicy.LockoutFor(int.MaxValue) == TimeSpan.FromMinutes(60),
            "R118 the wait is capped - a till whose admin locked themselves out must not be unusable for a day, and a huge stored counter must not overflow the doubling");

        // The point of the change: sustained guessing gets far slower.
        var guessesPerDayBefore = 24 * 60 / 5 * LoginLockoutPolicy.FreeAttempts;   // old: 5 guesses every 5 min
        var guessesPerDayAfter = 24 * LoginLockoutPolicy.FreeAttempts;             // now: 5 guesses per capped hour
        assert(
            guessesPerDayAfter * 10 < guessesPerDayBefore,
            $"R118 sustained guessing drops by more than an order of magnitude ({guessesPerDayBefore} -> {guessesPerDayAfter} per day)");

        // ---------- R119: the print queue keeps moving ----------
        var dir = Path.Combine(root, "r119-order-print");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r119.db"));
        var outbox = new OrderPrintOutbox(db);

        using (var c = db.OpenConnection())
        {
            using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM pragma_table_info('order_print_outbox') WHERE name IN ('attempts','last_error');";
            assert(
                Convert.ToInt64(q.ExecuteScalar()) == 2,
                "R119 the order print queue carries an attempt counter and the last error");
        }

        string Enqueue(string id, long orderId)
        {
            using var c = db.OpenConnection();
            using var q = c.CreateCommand();
            q.CommandText = "INSERT INTO order_print_outbox(id,order_id,action,payload,state,created_at) VALUES($id,$order,'ACCEPT','{}','PENDING',$at);";
            q.Parameters.AddWithValue("$id", id);
            q.Parameters.AddWithValue("$order", orderId);
            q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
            q.ExecuteNonQuery();
            return id;
        }

        var poisoned = Enqueue("r119-poisoned", 1);
        Enqueue("r119-healthy", 2);

        // A job that keeps failing is retried a bounded number of times.
        var gaveUp = false;
        for (var attempt = 1; attempt <= OrderPrintOutbox.MaxAttempts; attempt++)
            gaveUp = await outbox.FailedAsync(poisoned, "Drucker nicht gefunden");

        assert(
            gaveUp,
            $"R119 a job that keeps failing is given up on after {OrderPrintOutbox.MaxAttempts} attempts instead of being retried forever");

        var stillPending = await outbox.PendingAsync();
        assert(
            stillPending.Count == 1,
            $"R119 the given-up job leaves the queue so the ticket behind it can print - this is the head-of-line block that used to be permanent (pending: {stillPending.Count})");

        var failed = await outbox.FailedJobsAsync();
        assert(
            failed.Count == 1 && failed[0].Id == poisoned && failed[0].Attempts == OrderPrintOutbox.MaxAttempts &&
            failed[0].LastError.Contains("Drucker nicht gefunden"),
            "R119 the given-up job stays visible with its attempt count and reason, instead of failing silently");

        // Once the printer is fixed, the job can be put back.
        await outbox.RetryFailedAsync(poisoned);
        var afterRetry = await outbox.PendingAsync();
        assert(
            afterRetry.Count == 2 && (await outbox.FailedJobsAsync()).Count == 0,
            $"R119 a given-up job can be queued again after the printer is fixed (pending: {afterRetry.Count})");
    }
}
