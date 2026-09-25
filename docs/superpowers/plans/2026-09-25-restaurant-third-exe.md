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
- [ ] Run unchanged SafetyTests and inspect current CI.
- [ ] Add failing navigation/area/layout regressions.
- [ ] Extract guarded async table navigation; wire startup and THEKE.
- [ ] Add area filters and active-table opening; use fixed footer plus scroll body.
- [ ] Run safety and headless Restaurant layout; commit.

### Task 2: Restaurant master data and recipes
Files: new RestaurantMasterDataWindow.cs, RestaurantIngredientsWindow.cs, RestaurantRecipeWindow.cs, RestaurantTableSettingsWindow.cs, RestaurantRecipeRepository.cs; ProductEditorWindow.cs; SchemaMigrationService.cs.
- [ ] Add persistence and validation regressions.
- [ ] Add migration, normalized ingredient/recipe persistence and full editor paths.
- [ ] Connect Restaurant Stammdaten navigation with existing permissions.
- [ ] Verify restart persistence, atomic recipe replacement, invalid quantity and inactive area handling; commit.

### Task 3: Company setup and interim bill
Files: FirstRunSetupWindow.cs, RestaurantInterimBillService.cs, RestaurantInterimBillWindow.cs, RestaurantTablePlanWindow.cs; safety tests.
- [ ] Add first-run tax persistence and no-fiscal-side-effect regressions.
- [ ] Reuse existing tax/profile keys and report printing with read-only snapshot.
- [ ] Verify split/outstanding bill behavior and subsequent final checkout; commit.

### Task 4: Delivery
- [ ] Run full safety suite, Cloud tests, three edition builds and headless layout checks.
- [ ] Review diff; push branch; create draft PR to trigger existing CI.
- [ ] Verify all CI jobs and retrieve TOR-Restaurant-Setup-DEV artifact.
- [ ] Deliver third Setup with verified checks and any hardware-only limitations.
