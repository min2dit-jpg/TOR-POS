# R145 — Digital Receipt through TOR Cloud

The customer chooses **Papierbeleg** or **Digitalbeleg (QR)** after the sale. The
digital receipt is published to TOR Cloud and opened on the customer's phone under
`https://bon.<domain>/r/<token>` without a login: the page "Ihr digitaler Kassenbon"
shows the receipt, with `PDF herunterladen`, `Teilen` and `Drucken`.

Operations, security and the Cloud side in detail: `Cloud/docs/DIGITALER-KASSENBON.md`.

## The rules

- **§ 146a Abs. 2 AO, § 6 Satz 5 KassenSichV:** the receipt may be given on paper or
  electronically.
- **AEAO zu § 146a Nr. 2.5.2:** the transaction is finished before the receipt is
  provided.
- **Nr. 2.5.3:** an electronic receipt needs the customer's consent, which needs no
  particular form and may be implied.
- **Nr. 2.5.4:** showing the receipt only on the merchant's screen is not enough.
- **Nr. 2.5.6:** a standardised data format that free standard software can show; a QR
  code or a download link is expressly allowed.
- **Nr. 2.5.7:** the receipt is issued right after the end of the Vorgang.

**Data format.** The electronic receipt is made available in a standardised data format
as AEAO zu § 146a Nr. 2.5.6 requires. TOR provides a PDF download as standard.

## What was wrong

- **The digital receipt only worked inside the shop.** Since R103 it was a web page
  served by the till itself, reachable only in the shop's WiFi. A customer who scanned
  it on mobile data, or opened it after leaving, got nothing; there was no download in a
  standard format.
- **The digital receipt had its own copy of the receipt content** (`DigitalReceiptHtml`,
  built from the sale), separate from the paper receipt's print job. R106, R107 and R122
  each fixed a place where the two had drifted apart.
- **No customer choice was recorded.** The QR appeared only when BON EIN/AUS was off.

## The fix

### Two separate stores

| | TOR Fiscal Archive | TOR Digital Receipt Cloud |
|---|---|---|
| What | Kassen-DB, TSE, DSFinV-K and the other records to be kept | the customer's copy behind the QR code |
| Where | on the till | `bon.<domain>` |
| How long | legal retention periods | limited, 90 days by default; then token, PDF and content are deleted |

The receipt cloud is not an archive and never replaces the fiscal archive.

- **Retention.** No legal rule limits cloud receipts to 30–90 days. The limited lifetime
  is TOR's choice for data minimisation (Art. 5 Abs. 1 lit. c, e DSGVO).
- **§ 147 AO periods are separate.** Depending on the record they are 10 years,
  8 years for Buchungsbelege, or 6 years. They are met by the records on the till,
  which stay available for an audit or Kassen-Nachschau (§ 146b, § 147 Abs. 6 AO).

### Till (TOR POS Pro)

- **Receipt choice** (`ReceiptChoiceWindow`), after the sale is final and signed:
  - It is offered when "Digitaler Kassenbon (TOR Cloud)" is switched on (Firma & Bon)
    and TOR Cloud is set up and active on the till.
  - Choosing Digitalbeleg is the consent (Nr. 2.5.3).
  - Enter, Escape or closing the window means paper.
  - The choice is written to the audit log (`RECEIPT_CHANNEL`), with the Cloud receipt
    id but never the link.
  - It is offered for test/training sales too, marked TESTBON, so the flow can be tried
    before the fiscal release.
- **One source.** `DigitalReceiptDocument.From(ReceiptPrintJob, payments)` builds the
  digital receipt from the same print job as the paper receipt:
  - lines, VAT groups (R106), payments (R101 split) and the footer;
  - the TSE fields with their times unchanged in UTC (R140), the till serial (R144) and
    the order start (R137);
  - the § 6 statement or the outage note (R122).
- **Only what the receipt shows is sent.** No operator, no cash tendered, no customer
  data, no product ids.
- **Mandatory fields.** A receipt missing a mandatory field is not published — the
  printer's rule — and nothing leaves the till.
- **Publishing.** `CloudDigitalReceiptService` posts to the authenticated device API
  with a 10-second limit.
  - A retry uses the same reference, so no second receipt is created.
  - The QR code is shown only for a receipt link over HTTPS (HTTP only on this PC).
- **Fallback.** If TOR Cloud does not confirm, the window says why and the paper
  receipt is issued at once (Nr. 2.5.7). Nothing is queued for later.
- **QR display.** The QR appears on the cashier's screen and on the Kundendisplay, which
  now keeps it for 60 seconds.
- **Removed:** the local receipt server:
  - `DigitalReceiptService`, `LocalNetworkAddress`, `IDigitalReceiptService`,
    `DigitalReceiptToken`, `DigitalReceiptHtml`;
  - the settings `receipt.digital_qr.*`.
  - The `digital_receipts` table stays so existing databases keep their schema; nothing
    reads it.

### TOR Cloud

- **Device API.** `POST /api/v1/devices/receipts` validates the document:
  - It accepts known fields only.
  - Positions, discount, VAT groups and payments must add up.
  - It stores the document and the PDF under the SHA-256 hash of a 256-bit token, with
    tenant, till, `created_at` and `expires_at`.
  - The same reference with the same content gets a new token (the lifetime is not
    extended); different content is refused (409).
- **Receipt domain** (`TOR_CLOUD_RECEIPT_URL`, required to differ from
  `TOR_CLOUD_PUBLIC_URL`):
  - It serves only `/r/<token>`, `/r/<token>/pdf`, its assets and `robots.txt`.
  - It has no API, portal or updates, and no receipt is served on any other host.
- **Response headers on the receipt domain:**
  - `Cache-Control: private, no-store`;
  - `X-Robots-Tag: noindex, nofollow, noarchive, nosnippet`;
  - `Referrer-Policy: no-referrer`;
  - a strict CSP, `X-Frame-Options: DENY`;
  - HSTS over HTTPS;
  - no cookies.
- **robots.txt blocks nothing**, so a search engine can see the noindex.
- **HTTPS redirect.** Behind the proxy, an HTTP request is redirected to the configured
  HTTPS origin.
- **PDF writer** (`receipt-pdf.js`), dependency-free:
  - Courier, receipt column on A4, several pages when needed;
  - Turkish letters through an encoding difference.
- **Deletion.** Expired receipts are deleted by the hourly housekeeping (`secure_delete`)
  and are not served from the moment they expire.
- **Also:**
  - Receipt paths are redacted in error logs.
  - Many unknown links from one address get 429; a valid link always works.
  - Optional `TOR_CLOUD_IMPRINT_URL` / `TOR_CLOUD_PRIVACY_URL` are linked on every page.
- **Deployment.** `deploy/Caddyfile.example` covers `api.` and `bon.` with TLS 1.2/1.3,
  HSTS and no access log for the receipt domain. `deploy/tor-pos-cloud.env.example` has
  the new variables.

## Testing

- **`R145ReviewTests`** (11 checks):
  - the document adds up;
  - it equals the contract file `Cloud/tests/fixtures/digitalbon-kasse.json`, which the
    Cloud tests accept unchanged;
  - a stable upload reference; payment lines;
  - offered only when switched on and Cloud active;
  - published through the device API;
  - no operator or tendered cash sent;
  - incomplete receipts refused without sending;
  - test receipts marked;
  - only HTTPS receipt links become a QR code;
  - a timeout ends the wait.
- **Tests moved to the document.** R103, R106, R122, R123, R137 and R140 now check the
  document instead of the removed HTML.
- **Tests removed.** The HTTP checks of the removed local server (R103, and R115 as a
  whole) were removed: 12 checks.
- **Safety suite 831/831** under en-US, de-DE and tr-TR (832 before: −12, +11).
- **UI layout check** passed. `tools/TorPos.UiSnapshot` now also renders and checks the
  receipt choice and the QR/failure window.
- **Cloud 38/38** (`npm test`):
  - `tests/receipts.test.js`: document validation, page, PDF structure and encoding,
    the contract file;
  - `tests/receipt-service.test.js`: end to end with both domains on one port.
- **Manual check.** The page was rendered in a browser at phone width. The PDF was
  rendered with pdf.js, including Turkish letters.

## For the operator

- **Domains.** `api.<domain>` and `bon.<domain>` must be set up (Caddy, DNS,
  `TOR_CLOUD_RECEIPT_URL`).
- **Impressum and privacy notice** for the receipt domain (§ 5 DDG, Art. 13 DSGVO).
- **Data processing agreement** with the merchants (Art. 28 DSGVO).
- **Merchant's Verfahrensdokumentation:** receipt issuance on paper or digital, with
  paper as the fallback.
