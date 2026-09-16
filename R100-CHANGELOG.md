# R100 — Cross-Installation Import Barcode Collision

Fixes a genuine, reproducible bug found while writing R99's tests
(logged as deferred in memory at the time; fixed now rather than left open,
since it turned out to affect every cross-installation catalog import, not
just an unlucky demo-data coincidence).

## The bug

`ux_products_barcode` (`Infrastructure.cs`) is a **global** unique index —
`CREATE UNIQUE INDEX ... ON products(barcode) WHERE barcode <> ''`, not
scoped by `edition_scope`. But every fresh TOR POS database — real
installations included, not just test fixtures — unconditionally seeds the
same demo article ("Cola 0,33 l", barcode `5449000000996`) under
`edition_scope='KIOSK'` at creation time. `UpsertProductAsync`'s own
duplicate-lookup, used by both `ImportArticlesCsvAsync` and
`ImportArticlesFromDatabaseAsync`, was scope-filtered
(`... AND (edition_scope='ALL' OR edition_scope=$currentScope)`), so it
never found that row when the current business mode differed from `KIOSK`.
The import then fell into its INSERT branch and collided with the real,
scope-agnostic unique index: `SQLite Error 19: UNIQUE constraint failed:
products.barcode`.

Net effect: importing a product catalog between any two independently
created TOR POS installations — a realistic scenario (migrating to a new
till, syncing catalogs between stations, restoring from another site's
backup) — always failed, because both sides carry the identical seeded
barcode from their own independent installs.

## The fix

`BusinessManagementService.UpsertProductAsync`'s barcode duplicate-lookup
now matches the real constraint: a plain global `WHERE barcode=$v`, no
scope filter. This makes the lookup consistent with what the unique index
actually enforces, so an existing row in a different scope is found and
updated instead of triggering a doomed INSERT. The SKU lookup was left
scope-filtered — there is no unique index on `sku`, so no real constraint
mismatch exists there; that's a business-logic choice (same SKU is allowed
across editions today), not a bug.

## Testing

New `Desktop/tests/TorPos.SafetyTests/R100ReviewTests.cs` reproduces the
exact scenario: two independently `SafetyDatabase.CreateCurrentAsync`-seeded
databases (both carrying the same KIOSK demo barcode), imports one into the
other, and asserts (1) the import no longer throws, (2) the target still has
exactly one row for the shared barcode — not a duplicate — and (3) a second,
genuinely new product from the source still imports normally alongside it.

Full suite: **503/503 checks passed**, run twice for determinism.
