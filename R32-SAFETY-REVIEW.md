# TOR POS Pro 0.7.33 R32 - Safety revision

Base: uploaded R31 Printer Stability / Fehlerprotokoll source package.
R30 review remains the implementation checklist; R31's existing error-slip feature is retained.

| Review finding | R32 implementation | Limit / acceptance still needed |
|---|---|---|
| Cart changes during payment | Deep checkout snapshot, UI and engine mutation lock, scanner guard, parking lock | Touch/scanner testing on Windows |
| Unknown payment can be retried | Durable PREPARED/SENT/UNKNOWN/APPROVED journal before terminal effects; one open checkout database constraint; administrator evidence review | Real ZVT interruption and terminal reconciliation tests remain |
| Committed sale can be recovered twice | Operation key, request equality check, transactionally linked sale/stock/receipt/park/checkout outcome; retries return original sale; recovery checks committed outcome | New fiscal commits remain intentionally gated |
| License bypasses fiscal readiness | UI readiness check plus build-level service gates for terminal payment and new sale commit | TSE transaction integration, official DSFinV-K export and fiscal receipt acceptance NOT implemented by this revision |
| Training takes real cart | Training never reads/writes normal recovery. Development recovery has its own file | Switching simulation to commercial mode must use the intended cart mode |
| Synchronous SQLite work freezes UI | FIFO bounded background queue for public database repository/auth/report/export entry points, reentrant nested operations; ordered background recovery writes; async closing | Native drivers and failed storage can still hang at OS level; this is not a universal hang guarantee |
| Printer blocks shutdown / loses queue | 15-second watchdog; persisted print jobs; queued jobs blocked after timeout; shutdown bounded; restart does not replay; admin evidence review | Driver acceptance is not proof of physical printing. Native call may finish after timeout; operator checks Windows queue before reprint |
| Default admin credentials | Forced first normal login change; minimum password length and non-default PIN | Existing non-default credentials remain valid |
| Power-loss durability | WAL + synchronous=FULL; recovery temporary file flushed before atomic rename | No software can guarantee recovery from failed hardware or storage that lies about flushes |
| Diagnostics / single instance | Caught main-window exceptions logged, controlled error slips, corrupt recovery quarantined with durable block, one process per profile before running marker writes | Fatal process/OS failures cannot reliably print; use logs and next-start recovery |

## Payment review
KASSE > Zahlung prüfen / fortsetzen shows the immutable amount, operation ID and stored state.
It requires an initialized administrator login and a reference/evidence text. It never sends
another terminal payment or a terminal reversal. A confirmed paid transaction cannot be reset
to unpaid through this screen. When fiscal readiness is missing, a confirmed payment stays
blocked for bookkeeping and remains visible for reconciliation.

Only demonstrably uncharged payments may be released. R30 legacy real carts without a
transaction ID are treated conservatively as unknown and require review. A failed recovery
read preserves a `.damaged-*` file and `.blocked` sentinel; no automatic data deletion/unlock.

## Printer review
KASSE > Druckwarteschlange prüfen lists uncertain durable jobs. Check Windows spooler and
physical receipts before clearing the lock with administrator credentials and evidence.
Clearing the lock does not resend old jobs. An in-flight native call cannot be unlocked;
complete the driver review/restart first. Reprinting an existing receipt uses the history
copy function; do not create a second sale.

## Verification
- Release build of the complete Avalonia application: see verification/build-summary.txt.
- Executable regression checks: see verification/safety-tests.log.
- Checks exercise actual SQLite persistence, committed-operation replay, altered-payload
  rejection, fiscal gates, FIFO/nested queue behavior, recovery quarantine and injected
  printer hangs/restart/review. Committed-sale retry uses a seeded historical outcome;
  it does not pretend that unfinished fiscal sale processing has passed certification.
- No Windows GUI, actual Star driver, payment terminal, TSE or physical power-cut test
  was performed here. Existing warning categories are recorded in the build summary.

## Windows verification before the next development step
1. Build/install in a new extracted source directory using `1-SETUP-ERSTELLEN.bat`.
2. Login in KIOSK and IMBISS. Verify mandatory admin change if prompted.
3. Use TRAINING/unlicensed test mode for payment-dialog behavior. An active license
   without fiscal readiness must not enable real terminal charging.
4. Print a test slip. Test an unavailable driver, close/reopen, inspect uncertain jobs.
5. Test open-cart recovery and a training session without consuming a real cart.
6. Keep existing user data; no database reset is required by this migration.

The previous R31 CS0136 name collision is gone. This revision does not claim production
readiness; Stage 7 fiscal integration and hardware acceptance remain separate work.
