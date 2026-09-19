# TOR POS – fiskaltrust Sandbox Integration

Status: isolated integration branch, not wired into production checkout.

## Goal

TOR should be able to use the same POS-side integration against different
fiskaltrust backends:

- local Middleware + Swissbit Hardware TSE,
- local Middleware + Swissbit Cloud TSE,
- local Middleware + Bundesdruckerei/CryptoVision,
- hosted CloudCashbox / SignatureCloud.

The POS talks to the Middleware interface. The SCU/TSE choice remains a portal
configuration concern.

## Why this is separate from ITseProvider

TOR's existing `ITseProvider` represents low-level TSE operations:
Start/Update/FinishTransaction and TAR export.

fiskaltrust works one level higher. TOR sends a full ReceiptRequest with
charge items, payment items and receipt cases; fiskaltrust returns a
ReceiptResponse containing the TSE/receipt signatures and printable data.

Forcing fiskaltrust into `ITseProvider` would lose useful semantics and would
make Storno, Retoure, Bestellung and DSFinV-K mapping harder. The first adapter
therefore uses a separate `IFiskaltrustMiddlewareClient`.

## Local Windows REST

The portal can configure a local endpoint such as:

```
http://localhost:14002/rest
```

The v1 REST methods are then:

```
POST http://localhost:14002/rest/json/v1/Echo
POST http://localhost:14002/rest/json/v1/Sign
```

Do not hard-code the port/path. TOR stores the endpoint as configuration.

## CloudCashbox

For SaaS/CloudCashbox, the same JSON API is used but the request additionally
uses the `cashboxid` and `accesstoken` headers.

The access token must never be committed to Git, printed into logs or embedded
in installer files. Production storage must use TOR's protected-secret
mechanism.

## Recovery rule

A timeout is not treated as "not signed".

The same `cbReceiptReference` identifies the fiscal action. Before retrying a
possibly submitted request, TOR must use fiskaltrust's ReceiptRequest recovery
flag and query the already stored queue result. Blindly sending a new sale after
a timeout could create a duplicate fiscal action.

This matches TOR's existing payment-journal philosophy: ambiguous external
effects are reconciled, not guessed.

## Current branch content

- API v1 request/response contracts.
- REST Echo client.
- REST Sign client.
- Local and SaaS authentication modes.
- DE constants for POS receipt, StartTransaction, implicit flow and
  ReceiptRequest recovery.

No checkout path is switched to fiskaltrust yet.

## Next sandbox steps

1. Create a Sandbox CashBox in the fiskaltrust portal.
2. Obtain the CashBox ID, POS-System ID, endpoint and, if cloud-hosted, access token.
3. Run Echo first.
4. Send a Zero/Test receipt or documented sandbox receipt.
5. Persist the returned Queue/Receipt identifiers.
6. Implement explicit Start -> Finish mapping using the same
   `cbReceiptReference`.
7. Map TOR line items, mixed VAT menu splits, cash/card/mixed payments,
   Storno/Retoure and Bestellung.
8. Parse the returned QR/signature fields and compare them with TOR's receipt.
9. Compare fiskaltrust DSFinV-K output with TOR's own exporter.
10. Only after those tests consider a production provider switch.


## Launcher package handling

The portal-generated Windows launcher archive contains the CashBox credentials
needed by the launcher to download its configuration. In particular,
`test.cmd` and `install-service.cmd` contain an access token in clear text.

Rules for TOR development:

- Never commit a launcher ZIP, extracted launcher directory or access token.
- Never write the token to TOR logs, crash reports or screenshots.
- Keep the launcher outside the repository, e.g. `C:\fiskaltrust\TOR-POS\`.
- Start with `test.cmd`; do not install the Windows service until the sandbox
  connection is understood and stable.
- Local TOR -> Middleware REST calls do not need the portal access token.
  The token is for the launcher/configuration channel. SaaS/CloudCashbox is the
  exception and uses `cashboxid` + `accesstoken` headers.
- A portal REST URL such as `rest://localhost:1500/<queue-id>` is normalized
  by TOR to `http://localhost:1500/<queue-id>` before HttpClient use.

The launcher archive inspected during sandbox setup reports Launcher product
version 1.3.52. Its binaries are not copied into the TOR repository.


## ReceiptResponse projection prepared

The sandbox branch now preserves all Middleware-added printable supplements
returned by API v1:

- `ftReceiptHeader`
- `ftChargeItems` / `ftChargeLines`
- `ftPayItems` / `ftPayLines`
- `ftSignatures`
- `ftReceiptFooter`

For Germany, `FiskaltrustGermanReceiptProjection` extracts the documented
signature types for QR payload, POS serial, process type/data, transaction
number, signature counter, TSE times, algorithm, signature, public key,
process-start time, certification identification and TSE serial number.

The projection keeps the raw strings exactly as returned. It can rebuild the
DSFinV-K V0 QR payload from the individual signature items without reformatting
timestamps or signatures, so the first physical-TSE test can compare the
Middleware QR payload and TOR's representation exactly.

No returned fiscal value is persisted into a Sale yet and no production
checkout path is changed. That remains intentionally blocked until a physical
Swissbit TSE is available and a real Sign/receipt/recovery run has been
captured.


## DE case constants re-verified against current fiskaltrust docs

During the sandbox review the recovery flag was corrected from a low generic
`0x8000` bit to the Germany ReceiptRequest flag
`0x0000800000000000`. This matters after a timeout: recovery must query the
already processed receipt by `cbReceiptReference`, not accidentally send a
different case.

The branch also now distinguishes:

- 19 % charge item: `0x4445000000000001`
- 7 % charge item: `0x4445000000000002`
- 0 % tax-free charge item: `0x4445000000000006`
- cash: `0x4445000000000001`
- debit card: `0x4445000000000004`
- credit card: `0x4445000000000005`

TOR deliberately does not map its generic CARD payment to debit or credit yet.
That classification must come from terminal/payment evidence rather than be
guessed.


## Ambiguous Sign recovery prepared

`IFiskaltrustMiddlewareClient.RecoverAsync` is now available on the sandbox
branch. It intentionally takes the original `ReceiptRequest`, keeps the same
`cbReceiptReference`, charge items and payment items, and only adds the German
ReceiptRequest recovery flag before calling `/json/v1/Sign`.

A JSON `null` response is treated as "no previously processed matching
receipt found" and is returned as null instead of being misreported as a signed
receipt.

This method is not wired into checkout yet. The production recovery journal
will only call it after the physical-TSE sandbox run establishes the exact
persist-before-send and restart behavior.


## ZeroReceipt payload prepared (not sent)

The sandbox branch now contains a side-effect-free
`FiskaltrustSandboxRequests.ZeroReceipt` builder. It creates the German
ZeroReceipt with empty charge/pay arrays and the required implicit-flow flag.
Nothing calls it automatically.

When the physical Swissbit TSE is connected, this is the first Sign payload to
use for a controlled functional/status test before any sale is allowed to reach
fiskaltrust.


## First cash-sale sandbox payload prepared (not sent)

After a successful ZeroReceipt with a physical TSE, the branch can now compose a
deliberately restricted cash POS receipt via
`FiskaltrustSandboxRequests.SimpleCashSale`.

It supports only a straightforward positive SALE with no manual receipt
discount, no cancelled positions and no card portion. It maps 19/7/0 % charge
cases explicitly, adds the take-away marker to applicable reduced-rate food
lines, checks that the line sum equals the receipt total, and creates a single
cash pay item.

The helper intentionally refuses CARD/MIXED, STORNO/RETURN, discounts and
negative lines. Those cases will only be enabled after their exact fiskaltrust
business-case mapping and real-hardware behavior have separate tests.


### 0 % VAT is intentionally not auto-classified

The German Middleware has different charge-item cases for non-taxable,
tax-free and VAT-not-determinable transactions. A numeric TOR VAT rate of
`0 %` does not by itself identify which legal case applies. The sandbox
adapter therefore refuses automatic 0 % mapping until TOR stores the legal
tax category explicitly. This avoids silently treating every 0 % article as
tax-free.


## German ftState decoding prepared

The sandbox branch now decodes the German country prefix separately from the
operational flags. In particular, it can identify ready state, TSE
communication-failed state and SCU-switching state without confusing the
constant `0x4445` country prefix with a failure bit.

No automatic recovery action is triggered yet. A future runtime adapter may
use a ZeroReceipt to probe/recover the TSE communication state only after the
physical-TSE tests confirm the intended behavior.


## Hardware-test guards tightened

The current sandbox adapter follows the current fiskaltrust DE reference tables:

- DE `ftState` handling recognizes the documented TSE-communication-failed
  state (`0x4445000000000002`) and SCU-switching state
  (`0x4445000000000100`); undocumented guessed DE state bits were removed.
- `ZeroReceiptWithTseInfo` adds the DE TSE-info flag
  `0x0000000000800000` but does not force self-test/time-update. The latter
  stays opt-in because fiskaltrust explicitly warns against using it by default.
- The restricted cash-sale builder preserves TOR's `StartedAt` as the earliest
  item timestamp so an implicit-flow response can represent the actual business
  action start.
- Pfand is blocked in the first cash-sale sandbox payload. TOR's current cart
  line can include deposit in the article price, while fiskaltrust/DSFinV-K has
  dedicated Pfand/PfandRueckzahlung cases. It will be enabled only after a
  dedicated split/mapping test exists.


## Durable Sign journal prepared

Before any real sale can be wired to fiskaltrust, the sandbox branch now has a
`FiskaltrustSignJournal`.

The intended sequence is:

1. Persist the complete immutable ReceiptRequest as `PREPARED`.
2. Change it durably to `SENT` immediately before POST `/Sign`.
3. Persist the ReceiptResponse as `COMMITTED` when the response arrives.
4. If the result is ambiguous, keep `SENT/UNKNOWN` plus evidence.
5. After restart, only those `SENT/UNKNOWN` operations are exposed as recovery
   candidates; their original payload is reused with the ReceiptRequest flag.
6. A committed operation can never move back to SENT, and one
   `cbReceiptReference` cannot be reused with a different payload.

The journal has an immutable request trigger and a no-delete trigger. It is
still isolated from production checkout; this is the crash-safety foundation
that must exist before the first real Sign call is wired.


## Crash-safe Sign coordinator prepared

`FiskaltrustSignCoordinator` now composes the client and durable journal
without touching production checkout.

Its rule is conservative:

- PREPARED -> persist SENT -> call Sign -> persist COMMITTED.
- Any ambiguous Sign failure becomes UNKNOWN (or at minimum remains durable
  SENT if writing UNKNOWN itself fails).
- Re-entering a SENT/UNKNOWN operation calls `RecoverAsync` first.
- A recovered ReceiptResponse is committed.
- A null ReceiptRequest result does **not** cause an automatic resend; the
  operation stays unresolved for an explicit later decision.
- A COMMITTED operation returns its stored response and cannot be sent again.

This is the same safety principle TOR already applies to uncertain card
payments: an external effect is reconciled, never guessed.
