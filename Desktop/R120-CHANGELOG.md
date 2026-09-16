# R120 — Cloud Hardening and the Blocked-Ticket View

Closes audit findings **C1**, **C2** and **C4**, documents **C5**, and
completes the R119 follow-up.

---

## Blocked kitchen tickets are now visible (R119 follow-up)

R119 made the print queue give up on a poisoned job so later tickets keep
printing, but the abandoned jobs were only reachable by querying the database.
The Diagnose window now has a **BLOCKIERTE KÜCHENBONS** section next to R106's
**BLOCKIERTE KARTENERSTATTUNGEN** — same idea, same place, for the other queue
that can silently stop delivering. Each entry shows the order, the action, the
attempt count, when it started and the stored error, with an
**ERNEUT DRUCKEN** button that puts the ticket back once the printer is fixed.

`OrderPrintOutbox` is now a registered singleton so the dispatcher and the
Diagnose window share one instance.

---

## C1 — The served installer is verified again at download time

`PUBLISH-UPDATE.ps1` verifies Authenticode when publishing, but nothing
re-checked anything when the file was actually served: the endpoint streamed
whatever the manifest named. Anything able to write into the updates directory
bypassed the publishing gate entirely, and no test ever executed that
PowerShell script.

`GET /updates/<file>` now hashes the bytes it is about to send and refuses
with **409** unless they match the manifest's `sha256`. It is a per-update
cost, not a per-request one.

This does not replace the client-side check — R114 made the desktop enforce
the pinned certificate and Authenticode even on loopback. Both ends of the
chain now verify.

## C2 — The rate limiter could lock out every user at once

```js
if (loginAttempts.size > 10000) return true;   // everyone is limited
```

Filling the map with distinct e-mail addresses locked **all** users out of the
portal for 15 minutes — a trivial denial of service against the limiter
itself. It now evicts the oldest entries instead, so the limiter degrades
rather than turning into an outage.

The IP key was `req.socket.remoteAddress`, which behind the reverse proxy this
deployment requires is the *proxy's* address — every user shared one
100-attempt bucket. `X-Forwarded-For` is now honoured, but **only** when
`TOR_CLOUD_TRUST_PROXY=true`: believing that header from an untrusted peer
would let an attacker mint a fresh "IP" per request and bypass the limit
completely. Opt-in, because only the operator knows whether a trusted proxy is
in front.

## C4 — Operational gaps

- **A dropped download could kill the process.** `fs.createReadStream(...).pipe(res)`
  had no `'error'` handler, so an aborted transfer raised an unhandled error
  event. Now handled and logged.
- **Logging said almost nothing.** `console.error('Cloud request failed', 500)`
  carried no method, path, message or stack. Failures now log timestamp,
  method, URL, status and message, plus the stack for genuine 500s.
- **No graceful shutdown.** `SIGTERM`/`SIGINT` now stop accepting connections,
  close the SQLite handle and exit, with a 10-second escape hatch for stuck
  keep-alive connections. Tills retry from their outbox, so a clean close
  costs nothing and avoids a half-applied batch on every deploy.
- **`unhandledRejection`** is logged instead of being swallowed.

## C5 — Demo credentials: verified, deliberately unchanged

The audit flagged the hard-coded demo account as "a real default in the
shipped artifact". Re-checked at the source: `server.js:43` refuses to start
at all when the host is not loopback and either demo mode is on or cookies are
insecure. Demo mode therefore **cannot run on an externally reachable host** —
the credentials are reachable only from `127.0.0.1`.

Making the password configurable would add a knob for a threat the startup
guard already prevents, so this stays as it is. Recorded here so the decision
is visible rather than silently skipped.

---

## Testing

- **Cloud: 21/21** (`npm test`), including a new
  `R120 a tampered installer is refused at download time` — it corrupts the
  staged installer on disk without touching the manifest, asserts the download
  is refused with 409, restores the file and asserts it serves again.
- **Desktop: 606/606**, unchanged — this increment touches the Diagnose window
  and the Cloud server, neither of which the desktop suite asserts against.
