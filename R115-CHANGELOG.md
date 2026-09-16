# R115 — Digital Receipt Server Hardening

Closes finding **G2** (High) from this session's full project audit
(`TOR-POS-DERIN-INCELEME-2026-09-16.md`).

The R103 receipt server is deliberately unauthenticated — the customer scans
a QR on the shop's own WiFi, and the 144-bit token *is* the credential. That
part is sound. What was not sound was everything around it.

## 1. It listened on every interface

`new TcpListener(IPAddress.Any, 8099)` bound the server to **all** network
interfaces and served fiscal receipt content (totals, TSE serial, signature,
signature counter) over plain HTTP. On a till with a guest-WiFi adapter, a
second NIC, or a WAN-facing connection, that content was reachable far beyond
the shop LAN.

Now it binds exactly one address:

- an explicit `receipt.digital_qr.bind_address` when configured, otherwise
- the auto-detected LAN IPv4, and
- if neither yields a usable address, **the server does not start at all**.

Nothing is lost by narrowing it, because the QR URL is built from that same
address: without a reachable IPv4 the feature cannot work anyway. The new
setting also fixes a limitation `LocalNetworkAddress` documented from the
start — auto-detection takes the first "Up" non-loopback IPv4, which on a
till with an active VPN may not be the shop LAN. Now that adapter can simply
be named.

`IDigitalReceiptService` gained `BoundAddress`, and `MainWindow` builds the QR
URL from it instead of detecting the address a second time — the advertised
link and the listening socket can no longer disagree.

`LocalNetworkAddress` moved from `TorPos.App` into `TorPos.Infrastructure`
(one copy, used by both the server and the URL builder) — the same
"don't keep a second hand-written copy" lesson as R106/R108.

## 2. A connection could be held open forever

Each accepted connection was handled with no read timeout, no request size
limit and no concurrency cap, and `ReadLineAsync` appended one byte at a time
to an unbounded list. A client that opened a socket and never sent a newline
held it indefinitely while the buffer grew.

Added: a 10-second per-request deadline (linked `CancellationTokenSource`
plus socket `ReceiveTimeout`/`SendTimeout`), a 16-connection cap that
*refuses* rather than queues, a max request line of 8 KB, and a 50-header
limit.

## 3. Tokens never expired

The 404 page always said *"Bon nicht gefunden oder Link abgelaufen"*, but
nothing ever enforced expiry — a QR photographed once kept working forever.

Tokens now expire after `receipt.digital_qr.ttl_hours`, **default 24 hours**,
clamped to 1…8760. This is a shop-policy value, not a technical constant,
which is why it is a setting: the receipt must be *available* at the time of
the transaction, and a customer who scans in-store opens it within minutes,
but a longer window can simply be configured if wanted.

## Testing

New `Desktop/tests/TorPos.SafetyTests/R115ReviewTests.cs` — 6 checks against
a really running server: it binds exactly the configured address; a fresh
link is served; the same link aged past the window returns 404; a link just
inside the window still works (so the cut-off is the configured age, not
merely "not today"); a 64 KB request with no newline is dropped without
taking the server down, which the very next successful request proves; and
`BoundAddress` stays readable after stop for diagnostics.

`R103ReviewTests` was updated rather than worked around: it now pins
`bind_address` to `127.0.0.1` and issues its HTTP requests against
`server.BoundAddress`, so it no longer depends on the build machine happening
to have a LAN IPv4 — and it asserts the bound address explicitly.

Full suite: **585/585 checks passed**, run twice for determinism (579
before).

## Deliberately not changed

TLS. A LAN-local server reachable at a bare IPv4 has no name to obtain a
certificate for, and a self-signed one would train customers to click through
browser warnings. The content is a receipt the customer just received in
person; the meaningful controls here are the unguessable token, the narrowed
bind address and the now-enforced expiry.
