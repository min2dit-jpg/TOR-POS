# Restaurant Third EXE Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans task-by-task. User explicitly authorizes autonomous implementation without intermediate approvals.

**Goal:** Deliver the third Restaurant test Setup with table-first navigation and safe interim billing.

**Architecture:** Reuse existing table and fiscal services; add Restaurant-only navigation and normalized master-data persistence. Interim bills use the report printer, never checkout.

**Tech Stack:** .NET 10, Avalonia 12, SQLite, existing SafetyTests executable and headless UiSnapshot.

**Spec:** docs/superpowers/specs/2026-09-25-restaurant-third-exe.md

## Global Constraints
- German UI; preserve KIOSK/IMBISS behavior and FiscalRelease gates.
- Work exclusively on feature/restaurant-third-exe; do not merge #95.
- Preserve crash recovery, concurrency/version checks, fiscal signing and customer displays.

## Review Focus
- Restored or unresolved carts must prevent navigation into another sale.
- Repeated table taps must not create duplicate sessions/orders.
- Partially paid tables must show only remaining lines in interim bills.
- Inactive areas/tables with open sessions must not strand orders.
- Persistent actions must remain within actual window bounds, including small screens.

### Task 1: Baseline and navigation/layout
Files: MainWindow.axaml.cs, RestaurantTablePlanWindow.cs, RestaurantRepository.cs, UiSnapshot/Program.cs, RestaurantThirdExeTests.cs.
- [x] Run unchanged SafetyTests and inspect current CI.
- [x] Add failing navigation/area/layout regressions.
- [x] Extract guarded async table navigation; wire startup and THEKE.
- [x] Add area filters and active-table opening; use fixed footer plus scroll body.
- [x] Run safety and headless Restaurant layout; commit.

### Task 2: Restaurant master data and recipes
Files: new RestaurantMasterDataWindow.cs, RestaurantIngredientsWindow.cs, RestaurantRecipeWindow.cs, RestaurantTableSettingsWindow.cs, RestaurantRecipeRepository.cs; ProductEditorWindow.cs; SchemaMigrationService.cs.
- [x] Add persistence and validation regressions.
- [x] Add migration, normalized ingredient/recipe persistence and full editor paths.
- [x] Connect Restaurant Stammdaten navigation with existing permissions.
- [x] Verify restart persistence, atomic recipe replacement, invalid quantity and inactive area handling; commit.

### Task 3: Company setup and interim bill
Files: FirstRunSetupWindow.cs, RestaurantInterimBillService.cs, RestaurantInterimBillWindow.cs, RestaurantTablePlanWindow.cs; safety tests.
- [x] Add first-run tax persistence and no-fiscal-side-effect regressions.
- [x] Reuse existing tax/profile keys and report printing with read-only snapshot.
- [x] Verify split/outstanding bill behavior and subsequent final checkout; commit.

### Task 4: Delivery
- [ ] Run full safety suite, Cloud tests, three edition builds and headless layout checks.
- [x] Review diff; push branch; create draft PR to trigger existing CI.
- [ ] Verify all CI jobs and retrieve TOR-Restaurant-Setup-DEV artifact.
- [ ] Deliver third Setup with verified checks and any hardware-only limitations.


## Checkpoint evidence
- Baseline: source Release build passed. Linux full SafetyTests reaches the known Windows DPAPI dependency; final full verification belongs to Windows CI.
- `ae705c5`: navigation, area visibility and recipe foundation; 5 targeted checks passed.
- `1e35db2`: Stammdaten, company tax setup and read-only interim billing; 13 targeted checks passed. Draft PR #96 triggers the existing installer workflow.
- Third checkpoint: product/category tiles, separate immutable customer-option snapshots, price-neutral split checkout and recipe load/save guard. 17 Restaurant persistence/behavior checks; targeted run also includes existing language, advertising-TV and customer-display checks.
- Independent review found premature recipe saving could erase an unloaded recipe. Save/edit actions now remain disabled after failed loading; headless tests cover initial, successful and failed loading.
- CI run 36177725802 passed all three product builds/publish and dedicated-product windows, then identified missing entries in the existing German-only Restaurant language-test allowlist. Added only the five new Restaurant window names and Restaurant-only inline menu; shared-edition language requirements remain unchanged.
- FiscalRelease flags and final checkout/payment/TSE services are unchanged. Hardware TSE, terminal, printer and physical TV acceptance remains a device test, not claimed by automated software checks.
