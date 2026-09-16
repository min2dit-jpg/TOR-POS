# R106 — Three Real Bugs Found by the User's Own Review

The user reviewed the shipped software (not this session) and reported
four findings, asking me to verify each against the actual code before
doing anything. All four checked out as accurate:

1. **`FiscalRelease.Enabled == false`** blocks real production sales —
   confirmed, and **intentional** (the standing fiscal circuit breaker,
   unchanged, not touched here).
2. **Partial return doesn't account for the Bon's manual discount** —
   confirmed, a real financial gap dating back to R82's own documented
   simplification. **Fixed here.**
3. **Digital receipt's VAT is wrong when a discount applies** — confirmed,
   and this one is a genuine bug in my own R103 code, not previously
   known. **Fixed here.**
4. **Card refund has no lock against a duplicate attempt** — confirmed,
   matching the "narrow crash-window risk" R102's own changelog already
   flagged as accepted-but-unfixed. **Fixed here.**

## Fix 1 — Digital receipt VAT (R103 bug)

`DigitalReceiptHtml.Render`'s VAT summary computed VAT on the
**pre-discount** gross total and showed the manual discount as an
unrelated extra line, never actually reducing the VAT calculation. A
119,00 € sale (19% VAT) with a 10% discount (107,10 € actually paid)
showed 19,00 € VAT instead of the correct 17,10 €.

The **correct** formula already existed —
`StarMcPrint3PrinterService.BuildTaxSummary`, used by the printed
receipt, already prorates a manual discount proportionally across each
VAT-rate group before computing VAT. Extracted it into a new shared
`TorPos.Core.VatSummaryCalculator.Compute`, used by **both** the printed
and digital receipt now — eliminating the duplicate, independently-
maintained copy that let the two silently diverge in the first place.

## Fix 2 — Partial return discount proration (R82 gap)

`RecordReturnAsync` credited a returned line at its full, undiscounted
unit price, ignoring any whole-Bon manual discount entirely — a 100 €
item on a Bon discounted to 90 € refunded the full 100 € if returned
alone, 10 € more than the customer paid. Documented as a conscious
simplification at the time ("no unambiguous way to split a Bon-level
discount across a subset of its lines") — but a real, quantifiable
over-refund, worth fixing now.

Fixed by prorating the original sale's discount onto the return's own
raw (pre-discount) subtotal, via a new shared `TorPos.Core
.DiscountProration.Prorate` helper — used by **both**
`RecordReturnAsync` (the authoritative DB-side computation, now also
correctly populating the RETURN row's own `discount_cents` instead of
hardcoding `0`) **and** `MainWindow.OnPartialReturnClick` (which needs
the identical number *before* calling `RecordReturnAsync`, to know how
much to refund at the card terminal for a Mixed/Card original — without
this, the terminal would have refunded the raw amount while the DB
recorded the correctly discounted one, an actual over-refund at the
terminal). Same "one shared helper, not two hand-written copies" lesson
as Fix 1.

## Fix 3 — Card refund duplicate-attempt lock (R102 gap)

New `card_refund_attempts` table + `ICardRefundLockRepository`, giving
the *reverse* (refund) direction the same "durable, must-be-reconciled"
property `checkout_operations`/`ux_checkout_one_open` already gives the
*forward* (payment) direction:

- `BeginAsync` locks the original sale **pessimistically**, before the
  terminal call — a crash between "terminal charged" and "DB reversal
  recorded" still leaves the sale locked, not silently retriable.
- A partial unique index (`ux_card_refund_one_unknown_per_sale`, on
  `original_sale_id` `WHERE state='UNKNOWN'`) refuses a second concurrent
  attempt at the database level, not just via an app-side check.
- A **definite** terminal outcome (Approved/Declined/Cancelled/NotSent)
  clears the lock immediately via `ClearAsync` — only **Unknown**
  (ambiguous) leaves it in place.
- New "BLOCKIERTE KARTENERSTATTUNGEN" section in the Diagnose-Fenster
  lists any locked sale; a technician marks it resolved (name + a note on
  what they verified with the terminal/bank) only after manually
  confirming the real-world outcome — mirroring how an ambiguous
  checkout already requires manual reconciliation via
  `CheckoutReviewWindow`.
- `MainWindow.OnBonStornoClick`/`OnPartialReturnClick` check
  `HasUnresolvedAsync` before ever calling the terminal, and a locked Bon
  now refuses **both** BON STORNO and Teilretoure until resolved (not
  just a retry of the same action).

## Testing

New `Desktop/tests/TorPos.SafetyTests/R106ReviewTests.cs` — 12 checks.
Notably, the card-refund lock (Fix 3) is **fully end-to-end testable**,
unlike `RecordStornoAsync`/`RecordReturnAsync` themselves — locking a
sale is not itself a fiscal booking, so it never hits the
`FiscalRelease.RequireProduction()` wall the rest of this suite works
around. Caught one bug in the test itself while writing it: an
assertion checked for the substring `"19,00 €</td>"`, which is
accidentally *also* a substring of the correct `"119,00 €</td>"` (the
line's own unrelated pre-discount total) — fixed by anchoring the check
with a `>` boundary.

Full suite: **546/546 checks passed**, run twice for determinism.
