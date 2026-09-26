# Restaurant Main Workspace Correction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the modal Restaurant table-plan flow with one embedded Restaurant workspace that keeps Tischplan, table ordering, Theke and checkout inside the Restaurant edition's main window.

**Architecture:** Extract the existing table/session behavior from `RestaurantTablePlanWindow` into a reusable `RestaurantWorkspaceControl`, embed that control as an overlay in `MainWindow`, and let `MainWindow` own mode switching and final checkout. Existing RestaurantRepository, RestaurantFiscalOrderService, kitchen outbox/dispatcher, split checkout, interim bill and POS payment services remain authoritative; no second sales engine is introduced.

**Tech Stack:** .NET 10, C#, Avalonia 12, SQLite, existing TorPos.SafetyTests executable, TorPos.UiSnapshot, GitHub Actions/Inno Setup.

**Spec:** docs/superpowers/specs/2026-09-25-restaurant-main-workspace-correction.md

## Global Constraints

- Work only on `feature/restaurant-third-exe`; do not merge to `main`.
- Preserve existing fiscal/TSE release gates, crash recovery, payment guards, table concurrency/version checks, kitchen outbox semantics, audit and permissions.
- Restaurant operator-facing additions stay German.
- KIOSK/Einzelhandel and IMBISS/Gastronomie behavior must remain unchanged.
- 1366×768 is the primary Restaurant layout target; 1024×640 remains supported.
- A table tap is the open/select action; no mandatory second “TISCH ÖFFNEN” step.
- THEKE is a Restaurant mode in the same MainWindow, not a close-to-old-POS fallback.
- The normal Restaurant flow must not use `ShowDialog<RestaurantCheckoutDraft?>` to open the table plan.
- Existing item persistence/fiscal securing and kitchen outbox behavior are preserved. “BESTELLUNG SENDEN” wakes/flushes the existing kitchen dispatcher for the selected table and returns to table overview; it does not create a second unsent-order persistence model.
- The fourth test artifact must be distinguishable from the third: `TOR-Restaurant-Setup-DEV4-${{ github.sha }}`.

## Review Focus

- Repeated rapid taps on a free table must not create two live sessions; the repository's existing single-live-session guard must surface/reload cleanly.
- Switching Tischplan ↔ THEKE while a cart, checkout draft or protected operation is active must be rejected without losing either context.
- Aborting or closing the payment dialog for a table checkout must return to the same Restaurant workspace, not expose the legacy POS background.
- A kitchen job already handed over must not be duplicated by “BESTELLUNG SENDEN”; the button only triggers the dispatcher and reports “already sent” when no pending job for the selected session remains.
- At 1024×640, the table/order body may scroll, but TISCHPLAN/THEKE navigation and the five primary bottom actions must remain reachable.

---

### Task 1: Extract reusable Restaurant workspace from the modal window

**Files:**
- Create: `Desktop/src/TorPos.App/RestaurantWorkspaceControl.cs`
- Modify: `Desktop/src/TorPos.App/RestaurantTablePlanWindow.cs`
- Modify: `Desktop/src/TorPos.App/AppWindowFactory.cs`
- Test: `Desktop/tests/TorPos.SafetyTests/RestaurantThirdExeTests.cs`

**Interfaces:**
- Produces: `RestaurantWorkspaceControl`
- Produces: `Task InitializeAsync()`
- Produces: `Task RefreshAsync()`
- Produces: `bool IsBusy { get; }`
- Produces: `Func<RestaurantCheckoutDraft, Task>? CheckoutRequestedAsync { get; set; }`
- Produces: `Func<Task>? CounterRequestedAsync { get; set; }`
- Produces: `Func<Task>? OpenMasterDataAsync { get; set; }`
- Produces: `IAppWindowFactory.CreateRestaurantWorkspaceControl(AuthenticatedUser user)`
- Consumes: existing RestaurantRepository, RestaurantFiscalOrderService, RestaurantKitchenOutbox, RestaurantKitchenDispatcher, IProductCatalog, ISettingsRepository, ControlledPosActionService, IReceiptPrinterService and AuthenticatedUser.

- [ ] **Step 1: Add a failing structural regression test**

Extend `RestaurantThirdExeTests.Run` with a source/assembly-level assertion that the Restaurant workspace type exists and exposes the exact public callbacks/methods above, while `RestaurantTablePlanWindow` is only a wrapper around the workspace rather than the owner of checkout/navigation state.

- [ ] **Step 2: Run the targeted Restaurant checks and confirm RED**

Run from `Desktop`:

`dotnet run --project tests/TorPos.SafetyTests -c Release -- --restaurant-third`

Expected: FAIL on the missing `RestaurantWorkspaceControl` contract.

- [ ] **Step 3: Extract the current table/session UI and behavior into `RestaurantWorkspaceControl`**

Move the functional contents of `RestaurantTablePlanWindow` into the new control without changing repository/fiscal/kitchen semantics. Dialogs that require an owner resolve the containing top-level Window. Replace `Close(draft)` with `CheckoutRequestedAsync(draft)`; replace the existing THEKE close behavior with `CounterRequestedAsync()`. Keep all existing move/merge/split/storno/interim/recipe-option behavior.

- [ ] **Step 4: Turn `RestaurantTablePlanWindow` into a thin compatibility/test wrapper**

The wrapper creates one workspace, forwards the optional callbacks needed by existing headless coverage, calls `InitializeAsync()` on open, and must no longer contain independent copies of table/order logic.

- [ ] **Step 5: Add the DI factory method**

Add `CreateRestaurantWorkspaceControl(AuthenticatedUser user)` to `IAppWindowFactory` and `AppWindowFactory`, using the same service set currently used for `CreateRestaurantTablePlanWindow`.

- [ ] **Step 6: Re-run targeted checks**

Run:

`dotnet run --project tests/TorPos.SafetyTests -c Release -- --restaurant-third`

Expected: PASS with the new workspace contract checks included.

- [ ] **Step 7: Commit**

`git add Desktop/src/TorPos.App/RestaurantWorkspaceControl.cs Desktop/src/TorPos.App/RestaurantTablePlanWindow.cs Desktop/src/TorPos.App/AppWindowFactory.cs Desktop/tests/TorPos.SafetyTests/RestaurantThirdExeTests.cs`

`git commit -m "refactor: extract restaurant workspace control"`

### Task 2: Embed Tischplan and THEKE as MainWindow Restaurant modes

**Files:**
- Modify: `Desktop/src/TorPos.App/MainWindow.axaml`
- Modify: `Desktop/src/TorPos.App/MainWindow.axaml.cs`
- Modify: `Desktop/src/TorPos.App/MainWindow.Restaurant.cs`
- Test: `Desktop/tests/TorPos.SafetyTests/RestaurantThirdExeTests.cs`

**Interfaces:**
- Consumes: `IAppWindowFactory.CreateRestaurantWorkspaceControl(AuthenticatedUser user)`
- Consumes: `RestaurantWorkspaceControl.InitializeAsync()`, `RefreshAsync()`, `IsBusy` and its three callbacks.
- Produces: `Task ShowRestaurantTableWorkspaceAsync()`
- Produces: `void ShowRestaurantCounterWorkspace()`
- Produces: `Task HandleRestaurantCheckoutAsync(RestaurantCheckoutDraft draft)`

- [ ] **Step 1: Add failing navigation regressions**

Add tests that pin these source/UI contracts:
1. Restaurant startup does not call `ShowDialog<RestaurantCheckoutDraft?>` for the table plan.
2. `MainWindow.axaml` has a named `RestaurantWorkspaceHost` and Restaurant-only `RestaurantCounterButton` with content `THEKE`.
3. `OpenRestaurantTablePlanAsync` is removed/replaced by `ShowRestaurantTableWorkspaceAsync`.
4. KIOSK/IMBISS still leave Restaurant-only controls hidden.

- [ ] **Step 2: Run targeted checks and confirm RED**

Run:

`dotnet run --project tests/TorPos.SafetyTests -c Release -- --restaurant-third`

Expected: FAIL on the modal/startup/navigation assertions.

- [ ] **Step 3: Add the Restaurant workspace host to `MainWindow.axaml`**

Add a `ContentControl x:Name="RestaurantWorkspaceHost"` covering the cashier body area, initially hidden, plus a Restaurant-only header button `RestaurantCounterButton` labeled `THEKE`. The host overlays the legacy cashier body when Tischplan mode is active, so the old POS screen cannot be exposed behind a modal dialog.

- [ ] **Step 4: Wire Restaurant edition startup and mode guards**

In `MainWindow`, create the workspace once through the factory, set:
- `CheckoutRequestedAsync = HandleRestaurantCheckoutAsync`
- `CounterRequestedAsync` to guarded THEKE switching
- `OpenMasterDataAsync` to `ShowRestaurantMasterDataAsync(this)`

After existing startup recovery completes, call `ShowRestaurantTableWorkspaceAsync()`. Reject Tischplan/THEKE switching when `CartLocked`, a non-empty direct-sale cart, an active `_restaurantCheckoutDraft`, or `RestaurantWorkspaceControl.IsBusy` would make the switch unsafe.

- [ ] **Step 5: Keep THEKE inside the same MainWindow**

`ShowRestaurantCounterWorkspace()` hides `RestaurantWorkspaceHost`, shows the existing category/article/cart body, calls `ShowCategoryOverview()`, and sets a Restaurant-specific status. It must not close a Restaurant window because there is no separate daily-sales window anymore.

- [ ] **Step 6: Route table checkout through the existing payment pipeline**

`HandleRestaurantCheckoutAsync(RestaurantCheckoutDraft draft)` sets `_restaurantCheckoutDraft`, `_operationId`, and `_imHaus=true`, invokes existing `OpenPaymentWindowAsync(invokedByQuickCheckout:false)`, and in a `finally` block restores/refreshes the Restaurant workspace when the protected payment flow has finished or been cancelled. Do not duplicate payment, sale, receipt or TSE logic.

- [ ] **Step 7: Re-run targeted checks**

Run:

`dotnet run --project tests/TorPos.SafetyTests -c Release -- --restaurant-third`

Expected: PASS.

- [ ] **Step 8: Commit**

`git add Desktop/src/TorPos.App/MainWindow.axaml Desktop/src/TorPos.App/MainWindow.axaml.cs Desktop/src/TorPos.App/MainWindow.Restaurant.cs Desktop/tests/TorPos.SafetyTests/RestaurantThirdExeTests.cs`

`git commit -m "feat: embed restaurant workspace in main window"`

### Task 3: Make table ordering a one-tap service workflow with persistent primary actions

**Files:**
- Modify: `Desktop/src/TorPos.App/RestaurantWorkspaceControl.cs`
- Test: `Desktop/tests/TorPos.SafetyTests/RestaurantThirdExeTests.cs`
- Test: `Desktop/tools/TorPos.UiSnapshot/Program.cs`

**Interfaces:**
- Consumes: existing `RestaurantRepository.OpenTableAsync`, `GetLiveSessionForTableAsync`, `BuildCheckoutDraftAsync`.
- Consumes: existing RestaurantKitchenOutbox `PendingAsync()` and RestaurantKitchenDispatcher `Notify()`.
- Produces: private `Task SelectOrOpenTableAsync(RestaurantTable table, RestaurantTableSession? liveSession)`
- Produces UI names: `RestaurantSendOrder`, `InterimBill`, `RestaurantMove`, `RestaurantSplit`, `TablePayAll`.

- [ ] **Step 1: Add failing one-tap and action-layout regressions**

Extend Restaurant targeted tests and UiSnapshot checks to require:
- no visible mandatory `TISCH ÖFFNEN` primary step;
- tapping a free table routes through open-and-select behavior;
- tapping an occupied table selects its live session;
- primary footer includes exactly the five required actions in the Restaurant order workspace;
- the old `TablePlanClose/SCHLIESSEN` daily-flow button is absent;
- repeated open attempts are recovered by reload rather than creating a second session.

- [ ] **Step 2: Run targeted behavior test and headless Restaurant checks to confirm RED**

Run:

`dotnet run --project tests/TorPos.SafetyTests -c Release -- --restaurant-third`

Then:

`dotnet run --project tools/TorPos.UiSnapshot -c Release -p:TorProductEdition=RESTAURANT -- ci-results/ui-restaurant-main 1366x768 --check`

Expected: at least one new assertion fails before implementation.

- [ ] **Step 3: Implement one-tap table selection**

Change each table tile click to call `SelectOrOpenTableAsync(table, liveSession)`. If `liveSession` is null, open with the existing `OpenTableAsync`, then select/reload it. If a concurrent open wins first, catch the existing “already opened” condition, reload and select the now-live session rather than retrying creation.

- [ ] **Step 4: Reshape the order side into the agreed primary workflow**

Keep Warengruppe → Artikel and the always-visible open-position list. Replace the current bottom row with fixed primary buttons:
- `BESTELLUNG SENDEN`
- `ZWISCHENRECHNUNG`
- `UMBUCHEN`
- `TEILEN`
- `BEZAHLEN`

Keep Tischdetails/Storno/merge/empty-close as secondary contextual controls, not primary bottom actions. Remove the normal daily `SCHLIESSEN` button.

- [ ] **Step 5: Implement truthful “BESTELLUNG SENDEN” behavior without a new persistence model**

The item-add path continues to persist, fiscal-secure and enqueue kitchen jobs exactly as today. `BESTELLUNG SENDEN` checks `RestaurantKitchenOutbox.PendingAsync()` for the selected session, calls `RestaurantKitchenDispatcher.Notify()` when pending jobs exist, reports “Bestellung gesendet” / “Bestellung bereits gesendet” appropriately, clears the table selection and returns to table overview. It must never enqueue a second NEW job for an already-added item.

- [ ] **Step 6: Verify both supported Restaurant sizes**

Run:

`dotnet run --project tools/TorPos.UiSnapshot -c Release -p:TorProductEdition=RESTAURANT -- ci-results/ui-restaurant-main 1366x768 --check`

Run:

`dotnet run --project tools/TorPos.UiSnapshot -c Release -p:TorProductEdition=RESTAURANT -- ci-results/ui-restaurant-small 1024x640 --check`

Expected: both PASS; TISCHPLAN/THEKE and the five primary actions are within bounds.

- [ ] **Step 7: Re-run Restaurant targeted suite**

Run:

`dotnet run --project tests/TorPos.SafetyTests -c Release -- --restaurant-third`

Expected: PASS.

- [ ] **Step 8: Commit**

`git add Desktop/src/TorPos.App/RestaurantWorkspaceControl.cs Desktop/tests/TorPos.SafetyTests/RestaurantThirdExeTests.cs Desktop/tools/TorPos.UiSnapshot/Program.cs`

`git commit -m "feat: streamline restaurant table ordering"`

### Task 4: Regression-prove fiscal, interim bill and other editions

**Files:**
- Modify only if a failing regression requires a correction in the Task 1–3 files.
- Test: existing SafetyTests and UiSnapshot suite.

**Interfaces:**
- Consumes all interfaces from Tasks 1–3.
- Produces no new production interface.

- [ ] **Step 1: Run the complete SafetyTests executable**

Run from `Desktop`:

`dotnet run --project tests/TorPos.SafetyTests -c Release`

Expected on Windows CI: final line `ALL <N> CHECKS PASSED`. On a non-Windows local runner, document only genuinely platform-specific limits and rely on the mandatory Windows CI before delivery.

- [ ] **Step 2: Run the three dedicated edition builds**

Run:
- `dotnet build src/TorPos.App/TorPos.App.csproj -c Release -p:TorProductEdition=KIOSK`
- `dotnet build src/TorPos.App/TorPos.App.csproj -c Release -p:TorProductEdition=IMBISS`
- `dotnet build src/TorPos.App/TorPos.App.csproj -c Release -p:TorProductEdition=RESTAURANT`

Expected: all three PASS.

- [ ] **Step 3: Run dedicated headless windows**

Run UiSnapshot with `--check` for KIOSK, IMBISS and RESTAURANT at 1366×768, plus RESTAURANT at 1024×640.

Expected: all PASS; no Restaurant control leaks into the other editions.

- [ ] **Step 4: Re-run targeted Restaurant checks after full regression**

Run:

`dotnet run --project tests/TorPos.SafetyTests -c Release -- --restaurant-third`

Expected: PASS.

- [ ] **Step 5: Commit only if verification required code/test adjustments**

Use a focused commit message such as:

`git commit -m "test: harden restaurant workspace regressions"`

### Task 5: Produce and verify the fourth Restaurant test installer

**Files:**
- Modify: `.github/workflows/tor-pos-ci.yml`
- Modify: `docs/superpowers/plans/2026-09-25-restaurant-main-workspace-correction.md` only for checkpoint evidence after verification.

**Interfaces:**
- Consumes: green Task 1–4 branch.
- Produces artifact `TOR-Restaurant-Setup-DEV4-${{ github.sha }}`.

- [ ] **Step 1: Add the DEV4 artifact-name assertion before changing the workflow**

Add a lightweight repository/source regression in the existing verification path that requires the Restaurant development artifact name to contain `DEV4`.

- [ ] **Step 2: Confirm the assertion fails**

Run the relevant repository/workflow verification locally (or the targeted SafetyTests source assertion if that is where the check lives).

Expected: FAIL because the workflow still names the artifact `TOR-Restaurant-Setup-DEV-${{ github.sha }}`.

- [ ] **Step 3: Rename only the Restaurant development artifact**

Change the Restaurant upload-artifact name to:

`TOR-Restaurant-Setup-DEV4-${{ github.sha }}`

Do not rename the installed EXE itself: it remains `TOR-Restaurant-Setup.exe`.

- [ ] **Step 4: Run the workflow/source assertion again**

Expected: PASS.

- [ ] **Step 5: Commit**

`git add .github/workflows/tor-pos-ci.yml Desktop/tests/TorPos.SafetyTests/RestaurantThirdExeTests.cs`

`git commit -m "ci: label fourth restaurant test installer"`

- [ ] **Step 6: Push the feature branch and use existing PR #96 to trigger Windows CI**

This is a shared GitHub side effect. Obtain user approval immediately before the push if the execution environment requires a separate side-effect confirmation.

- [ ] **Step 7: Verify the complete Windows CI**

Require both jobs to complete successfully. In the Windows job specifically verify:
- Release build/test project;
- KIOSK, IMBISS and RESTAURANT builds;
- three dedicated publishes;
- dedicated-window headless checks;
- full safety/regression suite;
- standard five-size layout gate;
- Turkish/English layout gate;
- Inno Setup Restaurant installer creation.

- [ ] **Step 8: Retrieve and inspect the fourth artifact**

Download `TOR-Restaurant-Setup-DEV4-<sha>`, confirm it contains `TOR-Restaurant-Setup.exe`, record artifact size/digest, and deliver that EXE/ZIP for the next physical Windows test.

- [ ] **Step 9: Record checkpoint evidence**

Append the final commit SHA, CI run ID, passed check counts, supported headless sizes and artifact identifier to this plan. Do not claim physical printer, terminal or TSE hardware acceptance from software CI alone.
