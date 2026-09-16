# R41 validation · 2026-09-07

- Desktop Avalonia application compiled with .NET SDK 10.0.103: zero errors; six existing UI warnings.
- Desktop safety suite: 70 checks passed (including 14 new Cloud checks).
- Same suite against isolated real Node HTTP server: 71 checks passed.
- Node Cloud integration suite: 8 tests passed (tenant, schema, totals, duplicate/conflict, scoped IDs, stock ordering, Berlin/DST, pagination and authentication/origin checks).
- R40 -> R41 SQLite migration preserves 8 demo receipts and amounts; second R41 startup is idempotent.
- No real charge was made. Fiscal gate remains disabled. Windows DPAPI and physical printer/terminal tests were not run on this Linux environment.
- DOM interaction test passed: all 10 menus, receipt open/close, refresh and offline-state handling.
- Browser rendering was not verified: the browser runtime download was unavailable. Node syntax and API contracts were checked. Manual Windows/portal checklist is at bundle root.

## Scope remaining
Stock transfer is manual (maximum 5000 active articles); heartbeat is automatic.
Receipt outbox is wired into real sale commit but real-sale execution remains fiscally gated.
No automatic historical sales import, no simulation receipt export, no cash/Z emission yet.
Cloud is a loopback development portal, not a public production deployment.
