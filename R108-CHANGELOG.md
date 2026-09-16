# R108 — Self-Directed Audit: The TSE Fiscal Payload Had R106's Bug Too

After two rounds of the user's own source review (R106, R107), and then two
externally-generated architecture assessments that turned out to already be
addressed in this codebase (async-bounded hardware I/O, RSA-signed
licensing, offline-first cloud sync — all already built), the user handed
sequencing back explicitly: *"nerden devam edeceğine sen karar ver"* ("you
decide where to continue from").

Rather than inventing new architecture work from a template, continued the
one pattern that has actually found real bugs this session: search for any
remaining independent, hand-rolled copy of the VAT-by-rate-group formula —
the exact bug shape R106 already found and fixed twice (R103's digital
receipt, R82's partial return).

## The bug — highest stakes yet

`FiscalProcessData.BuildKassenbeleg`/`BuildBestellung` — the code that
builds the actual TSE **ProcessData payload that gets cryptographically
signed** as the legally-binding Beleg record — grouped `sale.Lines`/
`order.Lines` by `VatRate` and summed **raw, pre-discount**
`LineTotalCents`, completely ignoring `Sale.DiscountCents`/
`ParkedReceipt.DiscountCents`.

The `Betrag-Summe` (total amount) tag in the same payload already used the
correctly-discounted `TotalCents`. The result: a discounted sale's VAT-class
tags (`UStNormal:119.00`) would not sum to match its own `Betrag-Summe`
(`107.10`) in the very same signed document — an internally inconsistent
fiscal record. This is the same defect class as R103's digital receipt and
R82's partial return, just one layer deeper: those were display/refund-
amount bugs, this one would have reached the actual TSE signature the
moment `FiscalRelease.Enabled` is ever turned on.

Fixed by using the same shared `TorPos.Core.VatSummaryCalculator.Compute`
every other VAT-by-rate-group site now uses, in both `BuildKassenbeleg`
(sale, at final checkout) and `BuildBestellung` (IMBISS order acceptance's
own separate TSE Vorgang).

## Testing

New `Desktop/tests/TorPos.SafetyTests/R108ReviewTests.cs` — 4 checks,
reusing R106's exact 119,00 EUR/19%/10%-discount scenario, verifying the
`Betrag-Summe` and `UStNormal` tags now agree (both 107,10 EUR), an
undiscounted sale is completely unaffected, and the same fix holds for
`BuildBestellung`. Checked all existing `BuildKassenbeleg`/
`BuildBestellung` call sites in R78/R80/R82/R83/R101's own tests — none use
a discount, so none needed updating; all still pass unchanged.

Full suite: **558/558 checks passed**, run twice for determinism (up from
554 before this pass).

## Also addressed this pass — two external architecture reviews, corrected

The user brought two AI-generated architecture assessments claiming: (1)
monolithic synchronous startup risking UI freezes, (2) weak/hash-only
licensing vulnerable to cracking, (3) unbounded hardware I/O risking POS
freezes, (4) missing offline-first cloud sync, plus a "cross-platform
Windows/Linux" claim. Verified each against the actual code rather than
accepting the premise:

- Startup already runs behind an async splash screen
  (`StartupLoadingWindow`), no live hardware round-trip happens there (only
  a bounded 20s TSE-SDK-loaded check), so no freeze risk exists. The one
  accurate part: services are eager-loaded, not lazy — a code-organization
  observation, not a bug.
- Licensing already uses genuine RSA-SHA256/PKCS1 signature verification
  against an embedded public key (`CommercialLicenseService.cs`) — not a
  "simple hash." Only the (separately optional, tradeoff-heavy) DLL
  obfuscation part was accurate.
- Every ZVT terminal call already has explicit connect/command timeouts via
  linked `CancellationTokenSource`; printer calls run through an async
  queue with its own bounded native-call timeout. This is already the
  "circuit breaker" pattern being recommended.
- `TorCloudOutbox` already is a genuine local-first outbox: sale events are
  written in the same transaction as the sale commit, with a background
  drain using exponential backoff. Already exactly what was being
  recommended.
- "Cross-platform Windows/Linux" is not accurate: the only `.sh` file is a
  developer cross-compile helper (`build-linux.sh`), not a working Linux
  runtime — printer/TSE/terminal I/O are all deeply Windows-specific
  (confirmed earlier this session, R105).

No code changes made for any of these four — they were already correct.
