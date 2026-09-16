# R99 — CSV/DB Export→Import Round-Trip Fidelity for Im Haus/Außer Haus

Completes the R98 fix: R98 made *import* inherit a Warengruppe's existing
Im-Haus switch correctly, but neither `ExportArticlesCsvAsync` nor
`ImportArticlesFromDatabaseAsync` carried the switch itself. Exporting a
catalog and restoring it into a fresh database (backup/restore, new
installation) silently reset every Warengruppe back to the column default
(`true`, applies) — an admin who had switched a Warengruppe off would lose
that choice on the very next restore.

## Changes

- **`ExportArticlesCsvAsync`** — CSV export gained a 14th column, `IM_HAUS`,
  written as `1`/`0` for every article row, read from the article's
  Warengruppe (`category_master_data.im_haus_applicable`).
- **`ImportArticlesCsvAsync`** — parses the new `IM_HAUS` column. A missing
  column (older 13-column exports) means "unknown, don't touch"; an explicit
  value overwrites an existing Warengruppe's setting, matching how `MwSt.`
  already behaves on re-import.
- **`EnsureCategoryAsync`** — gained an `imHausOverride` parameter. When
  present, it runs a small guarded `UPDATE` against
  `category_master_data.im_haus_applicable` before reading the value back,
  kept deliberately separate from the main upsert's `ON CONFLICT` clause so
  an omitted column never resets an admin's existing choice.
- **`ImportArticlesFromDatabaseAsync`** — the database-to-database import
  path got the equivalent fix. The source-database read is guarded with a
  `pragma_table_info` existence check, so importing from a pre-R97 source
  database (no `im_haus_applicable` column at all) can never throw or fall
  back into the legacy R42–R46 compatibility tier and lose real
  stock/purchase-price data that source actually has — it just treats the
  value as unknown (`NULL`), not a fabricated default.

## Also found, not fixed (out of scope for R99)

While testing the database-to-database import path, found a genuine
pre-existing bug unrelated to Im Haus: `SafetyDatabase`'s demo/seed catalog
uses a fixed barcode under `edition_scope='KIOSK'`. `ux_products_barcode` is
a **global** unique index (not scoped by edition), but
`UpsertProductAsync`'s own duplicate-lookup **is** scope-filtered. Importing
between two databases that both carry that same seeded demo barcode under
different current scopes throws `UNIQUE constraint failed: products.barcode`
even though neither side's *visible* (scope-filtered) product list has a
conflict. This is a real gap for any real-world multi-scope or
cross-installation import and should get its own dedicated look — either the
index needs to be scoped, or the duplicate-lookup needs to stop being
scoped, but not left inconsistent as today. Logged in memory for follow-up.

## Testing

`Desktop/tests/TorPos.SafetyTests/R99ReviewTests.cs` — 4 new checks:
export writes the column and the correct per-Warengruppe value; importing an
export into a *fresh* database creates the Warengruppe with the exported
choice, not the raw default; an explicit CSV value overwrites an existing
Warengruppe's choice; `ImportArticlesFromDatabaseAsync` transfers the
source's real value into the target (source/target demo catalogs stripped in
the test to isolate this from the unrelated barcode/scope bug above).

Full suite: **500/500 checks passed**, run twice for determinism.
