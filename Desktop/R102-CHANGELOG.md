# R102 — Card-Payment BON STORNO / Teilretoure

Explicit user request, sequenced right after R101 ("önce Mixed Payment
sonra Kart Storno/Reversal UI"). Until now, BON STORNO/Teilretoure only
worked for pure BAR sales — a KARTE sale (or the card portion of a Mixed
sale) could never be storno'd at all. This closes that gap using the
already-vendored `Portalum.Zvt` SDK's `RefundAsync`.

## Design decision (confirmed with the user first)

The ZVT SDK exposes two different mechanisms:
- `ReversalAsync(int receiptNumber, ...)` — cancels a specific,
  terminal-tracked original transaction by the terminal's own receipt
  number. Real ZVT terminals typically only honor this **same-day**,
  often only for the **most recent** transaction — incompatible with how
  TOR's own BON STORNO already works (any sale, any time).
- `RefundAsync(decimal amount, ...)` — a manual credit. The customer
  presents their card again; no dependency on the original transaction
  still being in the terminal's memory, no reference back to it at all.
  Works exactly like the existing BAR-Storno's "anytime" semantics.

Chose **`RefundAsync`**, per explicit confirmation.

## Changes

- **`IPaymentTerminalService.RefundAsync`** — new interface method.
  `ZvtPaymentTerminalService.RefundAsync` is a self-contained
  connect→register→command→classify→audit shell modeled on `PayAsync`,
  deliberately **not** sharing code with it (no `checkout_operations`
  journal integration — there's no equivalent journal row for a Storno —
  keeping the already-tested original checkout path untouched).
- **`CheckoutApplicationService.RefundStornoCardPortionAsync`** — thin
  orchestration: no-op when there's nothing to refund, otherwise calls the
  terminal and returns its result.
- **`ISaleRepository.RecordStornoAsync`/`RecordReturnAsync`** gained a
  `cardRefundEvidence` parameter. The old blanket `PaymentMethod != Cash`
  refusal is gone; in its place, a **structural** gate: any sale (or, for
  a Teilretoure, the specific lines being returned) with a nonzero card
  portion refuses to record the reversal unless non-empty
  `cardRefundEvidence` is supplied — which only `MainWindow`'s
  orchestration produces, and only after the terminal confirms
  `PaymentTerminalOutcome.Approved`. This makes it structurally impossible
  to record a Storno against real money that was never actually returned.
- **`MainWindow.OnBonStornoClick`/`OnPartialReturnClick`**: before calling
  `RecordStornoAsync`/`RecordReturnAsync`, if the original (or, for a
  partial return, the proportional share of the requested lines) has a
  card portion, call the terminal refund first. `Approved` → proceed with
  evidence. `Declined`/`Cancelled`/not-sent → abort, no DB write, clear
  message. **`Unknown`** (e.g. a timeout after the refund command was
  sent) → abort, no DB write, explicit "status unclear, check terminal
  receipt, do **not** retry automatically" message — same philosophy as
  the original checkout's `Unresolved` disposition.
- **Reversal rows now mirror the original's real split** instead of being
  hardcoded `'CASH'`: a KARTE Storno is itself stored as `'CARD'`, a Mixed
  Storno mirrors both `cash_portion_cents`/`card_portion_cents`. This
  means R101's report queries (which sum these two columns) correctly
  attribute the reversal's Bar/Karte impact.
- **Teilretoure card portion is proportional** to the original sale's own
  cash/card ratio, computed once in `MainWindow` (to know how much to
  refund at the terminal before the DB write) and recomputed identically
  server-side in `RecordReturnAsync`. Same reasoning as why a manual
  discount is never prorated onto a partial return either — there's no
  unambiguous way to attribute one specific returned line to one tender
  type over the other.
- **`Sale` gained `EffectiveCashPortionCents`/`EffectiveCardPortionCents`**
  (mirroring `CheckoutSnapshot`'s R101 pattern) — the single place the
  historical-row fallback (derive from `PaymentMethod`+`TotalCents` when
  both stored portions are still 0) now lives, reused by both the new
  card-refund gate and the two report-query call sites that previously
  computed the same formula inline.
- UI copy in the Bon-Archiv/Storno picker updated to reflect that
  card/Mixed sales are no longer categorically excluded.

## A real near-miss caught while writing this

The gate's first draft read `original.CardPortionCents` (the raw column)
directly. For a **historical pre-R101** KARTE sale — where that column was
never backfilled (see R101's note on `sales` being append-only) — this
reads 0 regardless of the real payment method, which would have silently
let an old card sale's Storno through with **no refund requirement at
all**. Fixed by using `EffectiveCardPortionCents` (the same fallback
formula R101 already established) for the gate check too. Covered by a
dedicated test (`R102ReviewTests.cs`, check #4) seeding a raw row shaped
exactly like real pre-R101 data.

## Testing

New `Desktop/tests/TorPos.SafetyTests/R102ReviewTests.cs` — 11 checks.
**Note**: `FiscalRelease.RequireProduction()` always throws in this test
build (same documented limitation as R79/R82/R90's own tests), so the
*successful* completion of a card Storno — and therefore the actual
`payment_method`/portion mirroring — can't be exercised end-to-end here;
only the rejection gates (which fire before that circuit breaker is ever
reached) and the pure domain logic (`Sale.Effective*PortionCents`) are
covered. Worth re-verifying the mirrored-row shape by hand once real
hardware/FiscalRelease is available.

Full suite: **526/526 checks passed**, run twice for determinism.

## Still open

Card-payment reversal now exists for the **first time** in this app.
Real terminal acceptance testing (does the acquirer actually process a
standalone `RefundAsync` credit without the original transaction
reference, does the customer's card get credited correctly, timing/limits
imposed by the specific terminal/acquirer) has not been done — this needs
real hardware, same caveat as every other ZVT-touching feature in this
codebase.
