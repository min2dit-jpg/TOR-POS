# R101 — Mixed Payment (Split Cash/Card)

Explicit user request. A cashier can now split a single sale's total between
cash and a card terminal charge in one checkout.

## Design

- **`PaymentMethod`** gained a third value, `Mixed`. `Sale.CashPortionCents`/
  `CardPortionCents` are now always populated, for every sale, not just
  Mixed ones: `Cash=(total,0)`, `Card=(0,total)`, `Mixed=(X,total-X)`.
- **`CheckoutSnapshot`** gained `CashPortionCents` (meaningful only for
  `Mixed`) and two computed properties, `EffectiveCashPortionCents`/
  `EffectiveCardPortionCents`, that are correct for all three methods.
  Every downstream consumer (terminal charge amount, checkout-journal
  initial state, `Sale` insert, TSE `ProcessData`, cash-drawer kick, cash
  reconciliation) reads these two instead of branching on `Method` — this
  meant most call sites needed only a one-line change (swap `TotalCents`
  for `EffectiveCardPortionCents`, or `method==Card` for
  `EffectiveCardPortionCents>0`), not a rewritten payment pipeline.
- **UX** (per explicit product decision): a dedicated "GEMISCHT" button
  next to BAR/KARTE (placed in the header StackPanel, deliberately kept out
  of the fully-packed 4×4 numpad/payment Grid, same reasoning as R95's
  Im-Haus toggle). Opens `MixedPaymentWindow`: the cashier enters the cash
  amount; the remainder is charged to the card terminal exactly. No
  separate "tendered/change" concept — the entered amount IS what changes
  hands in cash.
- **Checkout pipeline**: a Mixed checkout goes through the *same* terminal
  flow as a pure Card sale (`CheckoutJournal.BeginAsync` seeds `PREPARED`,
  `CheckoutApplicationService.PrepareProductionAsync` calls
  `IPaymentTerminalService.PayAsync`), except the charge amount is
  `EffectiveCardPortionCents`, not the whole total.
  `ZvtPaymentTerminalService.PayAsync`'s exact-amount validation was
  updated to match.
- **TSE `ProcessData`**: a Mixed sale's Kassenbeleg now emits *both* a
  `Bar:` and an `Unbar:` amount tag with their real portions, instead of
  one amount tag for the whole total under a single tender type.
- **BON STORNO / Teilretoure**: per explicit product decision, a Mixed
  sale is refused exactly like a pure Card sale (`PaymentMethod != Cash`
  already covered this without any code change) — any sale with a card
  component needs a terminal reversal, which doesn't exist yet. Wording
  updated to mention the card portion of a mixed payment explicitly.
- **Reports**: `GetExpectedCashCentsAsync` (Kassensturz), `GetPeriodSummaryAsync`
  (X-/Z-Bericht), `BuildTurnoverSummaryAsync`, and `BuildOperatorSettlementAsync`
  (daily + monthly) now derive Bar/Karte from `cash_portion_cents`/
  `card_portion_cents` instead of a `payment_method='CASH'/'CARD'` filter
  against the whole `total_cents` — a Mixed sale's split lands correctly in
  both buckets instead of being invisible to either. The order board
  ("Bar bezahlt"/"Karte bezahlt") gained a third `"Bar/Karte bezahlt"` case.
  The GDPdU booking export gained `BAR_ANTEIL_CENT`/`KARTE_ANTEIL_CENT`
  columns so an auditor can recover the split of a Bon whose `ZAHLART`
  reads `MIXED`.
- **`sales` is append-only** (`trg_sales_no_update` aborts every `UPDATE`,
  by deliberate design — see `R78-CHANGELOG.md`). This means a historical
  pre-R101 row can *never* be backfilled with real portion values; every
  report query above treats "both portion columns still at their 0
  default" as "derive the split from `payment_method`+`total_cents`
  instead", so old rows keep reporting exactly as they did before this
  column existed. **A first draft of this migration used an `UPDATE`-based
  one-time backfill — this would have thrown `"completed sales are
  immutable"` on any real installation upgrading with historical CASH/CARD
  sales, a far worse bug than the one it was fixing. Caught by a test
  fixture (R90's) that raw-inserts a sale without the new columns,
  standing in for exactly this historical-row shape — not by reasoning
  about the trigger up front.** Worth remembering: any new column added to
  `sales`/`z_report_archive` that needs a "derive from existing data"
  migration must use a query-time fallback, never an `UPDATE`-based
  backfill, because of the immutability triggers.

## Testing

New `Desktop/tests/TorPos.SafetyTests/R101ReviewTests.cs` — 12 checks
covering: `CheckoutSnapshot`'s effective split for all three methods
(including clamping an over-large cash portion); the Application layer
charges the terminal exactly the card portion, not the total, and seeds
the correct journal state; a pure Cash checkout is unaffected; TSE
`ProcessData` emits both `Bar:`/`Unbar:` tags; Kassensturz counts only the
cash portion of a Mixed sale plus a historical row's full total via the
fallback; the turnover report splits correctly; BON STORNO refuses a
Mixed sale; the order board shows the split label.

Full suite: **515/515 checks passed**, run twice for determinism.

## Not covered (deliberately, matches an existing gap)

Card-payment Storno/Reversal — and by extension a Mixed sale's card
portion — is next on the list (see `ROADMAP.md`).
