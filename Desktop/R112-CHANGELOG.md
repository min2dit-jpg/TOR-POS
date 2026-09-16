# R112 — Neon Glow Pass

User feedback on R111 (still the same static photo of the idle cashier
screen - hover states don't show in a static screenshot, so the R111
effects were likely never actually seen): "böyle, istersen biraz daha
neon görünüm olsun" (like this, if you want let's make it a bit more
neon). Same direction as before (dark navy theme, no new palette) - just
turned up the glow.

## What changed - `MainWindow.axaml` only, still no tile-color changes

- **Category/product tiles**: a soft resting glow around the border
  (matching their own existing accent color), growing noticeably brighter
  and larger on hover/pressed - the classic "neon sign lighting up" cue.
- **TOR logo badge, IMBISS/KIOSK edition pill, KASSIEREN (quick checkout)
  button, GEMISCHT (mixed payment) button**: each now glows in its own
  existing accent color (teal, teal, green, purple) instead of sitting
  flat.
- **Header bar, category/product panel, cart panel**: the plain
  R111 shadows swapped for colored ambient glows matching the accent
  theme (teal/blue) instead of plain black, for a more consistently
  "neon" ambient feel throughout.

## A real build-time lesson worth recording

`BoxShadow` is a property of `Border` (and a few other panel-like
controls) - **not** of `Button`. The first attempt set `BoxShadow`
directly on `Button.categorytile`/`.producttile` styles and on
`QuickCheckoutButton`/`MixedPaymentButton` and failed at build with
`AVLN2000: Unable to resolve suitable regular or attached property
BoxShadow on type ... Button`. Fixed by using `Effect` with a
`DropShadowEffect` instead (the correct, generally-available mechanism for
glow/shadow on any `Visual`, not just `Border`) - caught immediately by
`dotnet build`, since Avalonia's XAML compiler validates every property
against its actual type at build time, before ever running the app.

## Testing

Pure XAML/style change. Clean `dotnet build` (which, per the lesson
above, is a genuinely meaningful check here - it already caught one real
mistake before this even reached the user). Full `TorPos.SafetyTests`
re-run for regression: **558/558 unchanged**. Category/product tile
*colors* still untouched throughout. **Real visual confirmation on the
actual till remains the user's own next step**, same disclosed limitation
as every UI pass this session (R104, R109, R110, R111) - and this time
specifically: hover/pressed glow can only be seen by actually touching a
button, not in a static screenshot of an idle screen.
