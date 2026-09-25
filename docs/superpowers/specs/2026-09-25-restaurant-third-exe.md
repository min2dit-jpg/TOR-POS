# Restaurant third test installer

Implement the user's ten-point brief on feature/restaurant-third-exe, based on 9043c135. Main and the branch were identical at intake; #94 fiscal retry, #89 VAT/Z guard and #86 TV corrections are merged. #95 is separate unfinished retail/gastro work.

Restaurant opens its table plan after startup recovery. A restored cart/payment must be resolved before changing context. THEKE closes the table plan into the existing category/article/checkout screen without a table reservation. Existing table checkout, fiscal signing, split payment, takeover, kitchen dispatch and TV services remain in use.

The table plan provides active areas with active tables, direct table opening, visible navigation and payment actions. Settings and table content scroll independently from persistent bottom actions at 1366x768 and smaller supported sizes. Stammdaten provides Artikel, Zutaten, Warengruppen, Bestelloptionen, Tische & Bereiche, Mitarbeiter, Drucker/Küche and Firmendaten with existing permission checks.

Recipe ingredients are separate from customer order instructions. Ingredient identity, base unit and per-product decimal quantity persist in normalized tables; no inventory/cost/allergen UI is advertised. Article editing links to recipes. Customer options retain a distinct model and order snapshot.

First-run company tax fields use company.tax_no/company.vat_id and edition profile keys already used by receipts/settings. Existing fiscal company-change safeguards stay in place.

Zwischenrechnung is a read-only snapshot of unpaid active table lines, visibly marked “Zwischenrechnung – kein Zahlungsbeleg”. Report printing never creates a sale/payment, reserves items, closes a table, allocates a receipt number or invokes TSE. Final payment continues through the existing guarded checkout.

All operator-facing additions are German. FiscalRelease gates stay closed. Baseline, behavior tests, actual headless layout, three editions, CI and installer artifact must be verified before delivery.
