# R116 + R117 — Sale-Mode Inversion and the Midnight Boundary

Next two items from the audit priority list
(`TOR-POS-DERIN-INCELEME-2026-09-16.md`): findings **İ3** and **İ1**.

---

# R116 — A licensed till could not sell at all (İ3)

`MainWindow.CanCompleteSale()` read:

```csharp
if (IsUnlicensedDevelopmentTestMode()) return true;   // no licence -> allowed
if (license.IsActive && FiscalRelease.Enabled && ProductionAllowed) return true;
ScannerStatus.Text = "KASSIEREN GESPERRT · Fiskal-Freigabe fehlt · TRAINING verwenden";
```

`FiscalRelease.Enabled` is hard-coded `false`, so the second line cannot be
true today. The result was inverted: an **unlicensed** till was waved through
as a "development test mode", while a **licensed** one was refused outright.
A paying customer could not even try their own register; someone without a
licence could.

Nothing fiscal was at risk either way (both paths end in a simulation or a
refusal, never a booked sale), but as a product state it was backwards.

**Fix.** The rule now lives in `TorPos.Core.SaleModePolicy`:

```csharp
CanCommitProductionSale(isTraining, licenseActive, fiscalReleaseEnabled, productionAllowed)
    => !isTraining && licenseActive && fiscalReleaseEnabled && productionAllowed;
IsSimulation(...) => !CanCommitProductionSale(...);
```

A real fiscal sale still requires the licence, the release gate *and* full
readiness — the commercial model is unchanged. What changed is the fallback:
anything else now completes as a clearly marked simulation (TEST receipt,
`TESTBON · KEIN STEUERBELEG`, "keine echte Buchung" in the status line,
nothing written to the sales journal) instead of refusing to sell.

Consequences:

- `IsSimulation` is now "cannot book a real sale", not "no licence".
- `CanCompleteSale()` reduces to the cashier's `Sale` permission check — the
  BAR/KARTE buttons no longer go dead just because the fiscal gate is closed.
- `IsUnlicensedDevelopmentTestMode()` is gone; its own comment had said
  "Remove/disable for production builds".
- The crash-recovery file name keeps its existing `development-cart-recovery
  .json` spelling on purpose, so a cart left open by a crash under the
  previous build is still found after updating.

7 checks in `R116ReviewTests.cs` pin the truth table, including the two that
matter: a licensed-but-not-released till is a *simulation* rather than a
refusal, and an unlicensed till can never book a real sale even with
everything else green.

---

# R117 — The midnight boundary (İ1), and a worse case found while fixing it

**Reported:** the Kassensturz computed expected cash per **calendar day**
(`substr(created_at,1,10) = today`) while the Z-report counted since the last
Tagesabschluss. For an IMBISS still trading at 01:00, that compared the whole
evening's physical cash against only the sales made since midnight — a large
phantom surplus. After a mid-day Z-report it had the opposite flaw: it kept
counting sales that had already been closed out.

**Found while fixing it — worse:** `GetOpenPeriodAsync` started the open
period at `max(today's midnight, last closing)`:

```csharp
var from = new DateTimeOffset(DateTime.Today, offset);
if (lastClosing > from) from = lastClosing;
```

So for a business open past midnight, sales made between the last closing and
midnight fell into **no Z-report at all**: the next Z started at 00:00, and
the previous one had been closed hours earlier. Turnover simply disappeared
from the daily-closing sequence — a fiscal reporting hole, not just a display
quirk. This affects X-report and Z-report alike.

**Fix.** Both now run from the last Tagesabschluss with **no midnight floor**,
so every sale belongs to exactly one Z period:

- `GetOpenPeriodAsync`: `from = last closing`, or everything when no closing
  has ever been recorded (the first Z legitimately covers all of it, rather
  than orphaning earlier sales permanently).
- `GetExpectedCashCentsAsync`: same boundary, applied to both the sales sum
  and the cash movements.

The period comparison already converted both bounds to UTC text
(`ToUtcColumnText`), so spanning midnight or a DST change compares as a true
instant.

5 checks in `R117ReviewTests.cs` build the real scenario — a closing the
previous morning, trade on both sides of midnight, then a count — and assert
that the pre-midnight turnover is included, that a Tagesabschluss resets the
expected drawer to the opening balance, that only later sales count towards
the next count, and that already-closed turnover never reappears in the next
period.

**Still open from this family (not changed here):** pickup numbers and
promotion validity windows also reset at local midnight. Neither loses money
or breaks a report — a pickup number restarting at 1 mid-service is an
operational annoyance, and a promotion ending "today" ending at midnight is
arguably what the operator configured. They are listed in the audit report so
the decision stays visible.

---

## Testing

Full suite: **597/597 checks passed**, run twice for determinism (585 before;
+7 R116, +5 R117).
