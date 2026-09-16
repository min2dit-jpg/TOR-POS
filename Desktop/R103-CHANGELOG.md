# R103 — Digital Receipt via QR Code

Explicit user request, prompted by seeing a competitor's ("AFS-Software
DenBonGibtEs.Online") in-store screen offering a scannable digital
receipt instead of a paper printout. §6 KassenSichV's Belegausgabepflicht
(receipt issuance obligation) can be satisfied electronically, not only on
paper, as long as the customer can actually take a copy away — a QR code
scanned right at the register satisfies that.

## Design decisions (both confirmed with the user first)

1. **Where the receipt is served from**: a small web server running on
   the till itself, reachable only over the shop's own WiFi/LAN — not a
   cloud-hosted page reachable from anywhere. Chosen over the cloud option
   because `TorCloudSyncService` is currently push-only (stock/catalog
   sync) and has no customer-facing web hosting capability at all; adding
   that would have been a much larger, separate infrastructure project.
2. **When the QR is shown**: only when the cashier has turned "BON
   EIN/AUS" off for a sale (i.e., paper printing is skipped). When paper
   printing is on, nothing changes — the QR is never shown alongside an
   already-printed receipt.

## Implementation

- **`TorPos.Core.DigitalReceipt.cs`**: `IDigitalReceiptService` interface;
  `DigitalReceiptToken` (144-bit random, base64url, unguessable — never
  the sale's own receipt number, so a Bon can't be enumerated by
  incrementing a URL); `DigitalReceiptHtml.Render` — a compact,
  mobile-readable page carrying the *same* legally-relevant content as
  the printed receipt (company data, line items, VAT summary, cash/card
  split, TSE fields), not a stripped-down summary, since this page may be
  the customer's only copy of the Beleg.
- **`TorPos.Infrastructure.DigitalReceiptService`**: a hand-rolled,
  GET-only HTTP/1.1 server over a raw `TcpListener` — deliberately *not*
  `System.Net.HttpListener`, which needs a one-time admin `netsh http add
  urlacl` reservation to bind any prefix but localhost. A `TcpListener`
  bound to `IPAddress.Any` needs no such reservation and no elevation,
  which matters here: this is a cashier-facing desktop app that must keep
  working under a normal Windows user account, never requiring the
  cashier session to run elevated. One route (`/r/{token}`), no
  keep-alive, no HTTPS — deliberately tiny scope, just enough to hand a
  phone browser one receipt page.
- New `digital_receipts` table maps token → sale id.
- New settings: `receipt.digital_qr.enabled` (default off) and
  `receipt.digital_qr.port` (default 8099), under a new "Digitaler Bon
  (QR-Code)" section in Einstellungen → Bon & Rechnung.
- **`MainWindow.CommitCheckoutAsync`**: when the existing auto-print
  condition is false (BON EIN/AUS off) and the digital-receipt feature is
  enabled and the server is running, registers the sale and shows
  `DigitalReceiptWindow` — a QR code (via the already-vendored `QRCoder`
  package, first used in R81 for the printed TSE QR) plus the plain link
  as a fallback, auto-closing after 45s or on a manual "FERTIG" tap. Never
  blocks or fails the checkout itself — a QR-rendering or server error is
  swallowed and logged, since the sale already committed successfully.
- **`LocalNetworkAddress.FindLanIPv4`**: best-effort LAN IP lookup so the
  QR points somewhere a phone on the same shop WiFi can actually reach
  (never `127.0.0.1`). Picks the first "Up", non-loopback IPv4 address —
  correct for the common single-NIC till, but could pick the wrong
  adapter on a machine with e.g. a VPN also active. Known v1 limitation.
- The server only starts when `receipt.digital_qr.enabled=true` at app
  launch — every existing installation is completely unaffected by
  default; nothing new listens on the network unless explicitly turned on.

## Testing

New `Desktop/tests/TorPos.SafetyTests/R103ReviewTests.cs` — 8 checks.
Unlike the ZVT/TSE-touching features in this codebase, this server is
real, in-process, and fully testable: the suite actually starts it,
registers a real sale, and makes genuine HTTP requests against
`http://127.0.0.1:{port}/r/{token}` — verifying a 200 with the right
sale's content, a 404 for an unknown token, and a 404 for any path
outside the one route, not just checking the pieces in isolation.

Full suite: **534/534 checks passed**, run twice for determinism.

## Not covered / left for follow-up

- Real-world WiFi/phone testing (does a customer's phone actually resolve
  the LAN IP and load the page reliably across common router/AP setups) —
  needs a real shop network, can't be verified in this environment.
- Multi-NIC IP selection (see `LocalNetworkAddress` note above).
- No token expiry/cleanup job yet — `digital_receipts` rows accumulate
  indefinitely. Low risk (tiny table, no PII beyond what the receipt
  itself already carries), but worth revisiting if it ever matters.
