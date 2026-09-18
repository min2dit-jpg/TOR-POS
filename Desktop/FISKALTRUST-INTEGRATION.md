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
