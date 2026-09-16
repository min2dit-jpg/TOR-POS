# R111 — Front-Screen Freshness Pass

User feedback after installing R110: "değişen birşey yok gibi" (doesn't
look like anything changed) - looking at a photo of the live cashier
screen. Correct observation: R110 consolidated colors across ~40
*secondary* windows (Settings, Diagnostics, payment/product dialogs), but
deliberately left the front cashier screen's category tile colors alone
(admin-owned per-Warengruppe data) and its header/toolbar chrome already
matched the new palette with no drift to fix - so the one screen a
cashier actually stares at all day looked unchanged, correctly.

Asked which to do next; user said "şu tazeliği bir kat" (add that
freshness) - to the front screen specifically, still without touching
category tile colors (those stay admin data).

## What changed - `MainWindow.axaml` only

1. **Hover/pressed feedback added to `Button.action`/`.topaction`/
   `.keypad`** - these had NONE before (only `.categorytile`/`.producttile`
   already had it). Every button on the cashier screen now reacts
   consistently when touched/clicked, with a smooth 120ms color
   transition instead of an instant snap.
2. **Subtle hover "grow" on category and product tiles** (`scale(1.015)`,
   ~1.5% - deliberately small so it can never visually overlap a
   neighboring tile given their existing margins) plus the same smooth
   transition, on top of the pointer-over border highlight that already
   existed.
3. **Soft drop shadows** (`BoxShadow`) added to the header bar, the
   category/product panel, and the cart panel - a standard "flat design
   with depth" cue that reads as considerably more current than perfectly
   flat panels sitting directly on the background.

Category tile/product tile *background colors* were not touched anywhere
in this pass - same admin-configured data as before.

## Testing

Pure XAML/style change. `dotnet build` clean (Avalonia's XAML compiler
statically validates `BoxShadow`/`Transitions`/`RenderTransform` syntax at
build time, so a genuinely invalid property would have failed here - it
didn't). Full `TorPos.SafetyTests` re-run for regression: **558/558
unchanged**. **Real visual confirmation on the actual till remains the
user's own next step** - same disclosed limitation as every UI-only change
this session (R104, R109, R110): this session cannot render or screenshot
the live Avalonia window.
