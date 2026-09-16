# R118 + R119 — Login Lockout Escalation and an Unblockable Print Queue

Next two items from the audit priority list
(`TOR-POS-DERIN-INCELEME-2026-09-16.md`): findings **G3** and **İ2**.

---

# R118 — The lockout handed out fresh guesses (G3)

`RegisterFailedAttemptAsync` reset the counter to zero at the exact moment it
locked the account:

```sql
SET failed_attempts = CASE WHEN failed_attempts + 1 >= 5 THEN 0 ELSE failed_attempts + 1 END,
    locked_until    = CASE WHEN failed_attempts + 1 >= 5 THEN $until ELSE locked_until END
```

So five wrong PINs bought a five-minute lockout — and then five fresh guesses,
forever. A flat rate of roughly **1440 guesses a day**, which puts a 4-digit
PIN (10 000 combinations) within a few days of patient guessing.

**Fix.** The counter now only ever rises; a *successful* login is what clears
it (that already happened). Each failure past the free attempts doubles the
wait — 5, 10, 20, 40 minutes — capped at 60. Sustained guessing drops to about
**120 a day**, an order of magnitude slower.

The cap is a deliberate trade-off, not an oversight: this is a cash register,
and only *staff* accounts can be cleared by an admin — an admin who locks
themselves out has no in-app unlock path. A till that cannot be opened for a
whole day is its own kind of outage.

The schedule lives in `TorPos.Core.LoginLockoutPolicy` as a pure function, the
same pattern as R114/R116, so the escalation (and the overflow guard for an
absurdly large stored counter) is asserted directly in the suite rather than
inferred from a service that needs a database and a clock.

---

# R119 — One dead printer blocked every later kitchen ticket (İ2)

`OrderPrintDispatcher.DispatchOnceAsync` walked the queue in insertion order
and `break`-ed at the first failure. There was no attempt counter, no give-up
state, and no view of the queue anywhere in the UI. A single job addressed to
a printer that no longer exists therefore blocked **every subsequent kitchen
ticket indefinitely** — the cashier's only clue being a repeated error
notification, while orders silently stopped reaching the kitchen.

**Fix.**

- `order_print_outbox` gained `attempts` and `last_error` (additive
  `EnsureColumnAsync` migration, same pattern as every other column added
  since R77).
- A failing job is retried up to `OrderPrintOutbox.MaxAttempts` (5) and then
  parked as `FAILED`, so the dispatcher moves on to the next ticket instead of
  stopping.
- Ownership transfer still wins: if the print journal shows the job was handed
  over despite the exception, it counts as done, not failed — the
  no-duplicate-print guarantee from the original design is untouched.
- `FailedJobsAsync()` lists given-up jobs with their attempt count and reason,
  and `RetryFailedAsync(id)` puts one back in the queue once the printer is
  fixed.

9 checks across both fixes in `R118R119ReviewTests.cs`, including the one that
matters most: after a poisoned job is given up on, the healthy ticket behind
it is still pending and printable.

---

## Testing

Full suite: **606/606 checks passed**, run twice for determinism (597 before;
+4 R118, +5 R119).

## Note

The `FAILED` jobs are currently reachable through `FailedJobsAsync()` but not
yet shown in the Diagnose window. Surfacing them there (next to the R106
"BLOCKIERTE KARTENERSTATTUNGEN" section, which solves the same class of
problem for card refunds) is the natural follow-up and is listed as such.
