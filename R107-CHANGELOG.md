# R107 — Refund-Safety Fixes from the User's Second Review

The user reviewed the shipped software a second time (after R106) and
reported new findings, again asking me to verify each against the actual
code before doing anything. Two were genuine, critical, previously-unknown
bugs. One more (digital receipt TSE-outage messaging) was also real, and
several others turned out to already be fixed by R106 or were confirmed but
out of scope for a code change. All are addressed below, honestly, one by
one.

## Fix 1 — Card refund started before validation (Kritik)

`MainWindow.OnBonStornoClick`/`OnPartialReturnClick` called the real card
terminal refund **before** the "was this Bon already reversed?" check ever
ran — that check lived only inside `RecordStornoAsync`/`RecordReturnAsync`,
reached *after* the terminal had already moved money. A retried or
duplicate STORNO attempt on an already-fully-processed sale would charge
the terminal a second time before the database ever objected.

Fixed with a new `ISaleRepository.CheckReversalAllowedAsync(originalSaleId,
forFullStorno, ct)` pre-check, called at the very top of both handlers,
before either ever touches the card terminal. It returns `null` when the
reversal may proceed or a human-readable block reason otherwise. This is a
genuine pre-check in addition to (not a replacement for) the authoritative
transactional check still inside `RecordStornoAsync`/`RecordReturnAsync` —
belt and suspenders, since a pre-check alone can still race with a second
concurrent attempt.

## Fix 2 — Storno and Teilretoure didn't check each other (Kritik)

`RecordStornoAsync` only ever checked for a prior `STORNO` row;
`RecordReturnAsync` only ever checked whether the *same line* had already
been returned. Neither checked for the *other* reversal type against the
same original sale. The user's own example: a 100 € sale partially returned
by 30 €, then fully storno'd afterward — the full storno still refunded the
complete 100 € on top of the 30 € already returned, 130 € total for a
100 € sale.

Fixed by:
- Widening `RecordStornoAsync`'s existing-check from
  `transaction_type='STORNO'` to `transaction_type IN ('STORNO','RETURN')`.
- Adding a new equivalent STORNO-existence check to `RecordReturnAsync`.
- `CheckReversalAllowedAsync` (Fix 1) encodes the same rule for the
  pre-check: an existing STORNO always blocks further reversal; an
  existing RETURN blocks only a further **full** STORNO, not another
  Teilretoure (multiple partial returns against the same not-yet-fully-
  storno'd original remain normal).

## Fix 3 — Digital receipt confused a TSE outage with a test sale (Yüksek)

`DigitalReceiptService.BuildResponseAsync` derived "is this a test/training
receipt" purely from `string.IsNullOrWhiteSpace(sale.TseSignature)` — but a
**real** sale caught in a genuine TSE hardware outage also has no
signature (`SaleFiscalSigningService` sets `sale.TseOutage = true` instead
of a signature when signing fails). A real sale during a real outage would
have shown "TESTBON · KEIN FISKALBELEG" instead of outage-specific
messaging — `DigitalReceiptHtml.Render` already has a correct, distinct
"TSE-Ausfall zum Zeitpunkt des Verkaufs" line, it was simply unreachable
because the test-mode banner short-circuited it first.

Fixed by changing the derivation to
`string.IsNullOrWhiteSpace(sale.TseSignature) && !sale.TseOutage` — a blank
signature is only "test mode" when there's also no recorded outage. Not
currently reachable in the shipped build (real, non-simulation checkouts
cannot commit at all yet — see "Confirmed, not a bug" below — so no `Sale`
today can ever have `TseOutage=true`), but a real, latent defect that would
have mattered the moment `FiscalRelease.Enabled` is ever turned on, exactly
like R106's other fixes.

## Test-methodology fix — reject() couldn't tell WHICH gate fired

The user's own critique, verified against the actual harness: `Reject()` in
`Program.cs` uses a bare `catch{}` — it counts **any** thrown exception as
success, never checking the message. A test claiming to verify the new
double-refund gate could, after some future reordering, silently start
passing because it hit `FiscalRelease.RequireProduction()` instead — a
regression that would go completely unnoticed.

Added a second helper, `RejectMessage(action, mustContain, title)`, that
additionally asserts the caught exception's message. All of R107's own new
tests use it, and explicitly include a contrast case proving a plain sale
(no prior reversal) still only ever hits the FiscalRelease gate — never the
new one — so the message-bearing tests are provably exercising the
intended check, not an accidental one. Pre-existing `Reject()` calls across
R48–R106 were left as-is; retrofitting all of them was judged out of scope
for this pass (see below).

## Confirmed, not a bug — real production sales can't commit at all yet

Traced the checkout path end-to-end: a non-simulation checkout goes through
`CheckoutApplicationService.PrepareProductionAsync`, which returns
`Disposition.FiscalBlocked` — refusing to even start the payment — whenever
`FiscalComplianceService.CheckAsync()`'s `ProductionAllowed` is false, which
it always currently is (`fiscalReleaseBuild=false`, a hard-coded
build-level `const`). So today, a real (non-training, non-dev-test)
checkout on a licensed install cannot commit a `Sale` row at all — this is
intentional and unchanged, the same standing circuit breaker R106 already
confirmed and left alone.

## Confirmed accurate, already fixed by R106 (stale in the user's source ZIP)

- Partial-return discount not prorated — fixed by R106's
  `DiscountProration.Prorate`.
- Digital receipt VAT wrong with a discount — fixed by R106's
  `VatSummaryCalculator`.
- "No persistent lock against a duplicate card-refund attempt" — fixed by
  R106's `ICardRefundLockRepository`; re-verified this pass, still correct.

The user's source ZIP evidently predates one or more of these R106 fixes.

## Confirmed accurate, no code change (tracked, out of scope here)

- **SumUp**: verified — `SumUpConnectionService` (R37) genuinely only
  implements reader listing, pairing, status query, and a fixed EUR 1.00
  device test. Its own header comment already says so: "not wired into the
  sales flow." No `IPaymentTerminalService` implementation exists for it —
  `ZvtPaymentTerminalService` is the only one, and the entire sale flow
  uses it. Matches the user's claim exactly; a genuine, already-tracked
  roadmap gap (official SumUp adapter), not a defect introduced or found
  this session.
- **Version/README inconsistency**: confirmed — `TorRelease.Revision` had
  been left at `"R75.1-Application-Namespace-Hotfix"` since long before
  this session's R101–R106 work, and `README.md`'s header still said
  "R62." Fixed: `ReleaseInfo.cs` now reads `Version = "0.7.33.807"`,
  `Revision = "R107"`; `README.md`'s header and safety-check-count
  reference now reflect the current state, with the original R62 section
  preserved underneath rather than deleted.

## Testing

New `Desktop/tests/TorPos.SafetyTests/R107ReviewTests.cs` — 8 checks,
covering `CheckReversalAllowedAsync`'s three states, both widened
Storno/Retoure existence checks (via the new `RejectMessage`, verifying the
actual block-reason text), and the FiscalRelease-only contrast case.

Full suite: **554/554 checks passed**, run twice for determinism (up from
546 before this pass).
