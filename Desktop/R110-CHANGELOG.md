# R110 — App-Wide UI Polish (Dark Theme Consolidation)

The user asked (earlier this session) whether the UI could be "modernized
a bit compared to current competitors," then explicitly picked this up
again after the correctness/safety work (R101-R109) was done, choosing via
AskUserQuestion: **whole-app scope**, **polish the existing dark navy
theme** (not a new color palette or brand identity).

## The real problem: consistency, not the color scheme

An Explore survey confirmed only 4 files are actual XAML (`App.axaml`,
`MainWindow.axaml`, `LoginWindow.axaml`, `SettingsWindow.axaml`); the other
~40 windows are built entirely in C# code-behind, each hardcoding its own
literal hex colors via `Brush.Parse`/`Color.Parse`. There was no shared
theme system and no shared control-factory helper - real, unintentional
drift had already crept in: the same conceptual "card" container used
corner radius 7/8/9/10/12 in different windows, and the same "secondary
panel border" role used four different hex values (`#15374A` in
`SettingsWindow.axaml.cs` alone, `#1C3852`/`#111B2A`/`#28445D` elsewhere).
That inconsistency - not the navy color scheme itself - is what actually
reads as unpolished.

## What was built

New `src/TorPos.App/Theme.cs`, a static `AppTheme` class (named `AppTheme`,
not `Theme` - Avalonia's `StyledElement` already has an inherited instance
member called `Theme`, its own `ControlTheme` mechanism, which every
Window/Control in this codebase derives from; a bare `Theme.X` reference
resolves to that inherited member first, found the hard way on the first
build attempt):

- Named brushes for the ~12 real, recurring roles found in the survey:
  `BgPrimary`, `SurfacePanel`, `PanelBorder`, `InfoCardBg`, `AccentTeal`,
  `AccentBlue`, `TextPrimary`, `TextMuted`, `WarningAmber`/
  `WarningAmberBg`, `SuccessGreen`/`SuccessGreenBorder`, `InfoBlue`/
  `InfoBlueBorder`, `DangerRed`/`DangerRedBorder`, `ButtonNeutral`/
  `ButtonNeutralBorder` (matching the button chrome App.axaml's own
  `Button.action` style already established).
- `CardRadius`/`PillRadius` constants.
- A few reusable builders (`Card`, `WarningBanner`, `DangerBanner`,
  `ButtonRow`) for patterns that were copy-pasted near-identically into
  many windows' constructors - additive, not mandatory; existing per-window
  helpers stay as they are.

## Rollout

Every `.cs` file under `src/TorPos.App` carrying a hardcoded hex color
matching the consolidated palette was updated to reference the shared
`AppTheme.*` brush instead: `DiagnosticsWindow.cs`, `ProductEditorWindow.cs`
(chrome only - its ~60-color category swatch palette at lines ~1499-1526
is user-facing product data, deliberately untouched), `SettingsWindow.
axaml.cs` (including its `InfoCard` helper, now `IBrush`-typed with a
`BorderBrush` and `AppTheme.CardRadius` added for consistency),
`PaymentChoiceWindow.cs`, `EditionSelectionWindow.cs`,
`PromotionManagementWindow.cs`, `PosActionReasonWindow.cs`,
`ManagementWindows.cs`, `Dialogs.cs`, `MainWindow.Menu.cs`,
`MainWindow.axaml.cs`, `MainWindow.Safety.cs`. A handful of single-
occurrence, genuinely distinct colors were deliberately left as literals
(e.g. a toggle button's specific active-state green/red shades, one-off
warning severity tiers) - only colors actually repeated across the
codebase were consolidated, per the "only for patterns proven repeated"
principle from the approved plan.

The 4 real XAML files were checked too: their hex values already
coincide with the new palette (no drift found there), so no XAML edits
were needed - introducing `StaticResource` plumbing there for values that
already match would have been scope creep with no visible benefit.

## Testing

Pure visual/style change - touches no business logic, schema, or
fiscal/refund path. `dotnet build` clean after every batch and once fully
at the end. Full `TorPos.SafetyTests` run twice for determinism:
**558/558 checks passed, unchanged** from R108/R109 (as expected - this
suite deliberately never initializes Avalonia's real rendering platform,
R87, so it cannot and does not need to assert anything about this pass).

**Real visual confirmation remains the user's own next step** - this
session has no way to render or screenshot the live Avalonia window, same
disclosed limitation as R104/R109.
