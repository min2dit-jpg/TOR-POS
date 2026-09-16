# R105 — Auto-Maximize on Different Screen Sizes

Explicit user concern: the app should adapt automatically when run on
PCs/tablets of different sizes, not require manual window resizing.

## Finding (verified in code, not assumed)

- `MainWindow` opened at a fixed default size (`Width="1440" Height="900"`
  in `MainWindow.axaml`) with no code anywhere setting `WindowState` -
  the window never automatically filled whatever screen it was actually
  on; the cashier had to maximize it manually every time.
- The internal layout is already mostly proportional (`*`-sized rows/
  columns) - the numpad/payment grid, category area, etc. already scale
  with available space. The gap was specifically "never gets offered
  the actual screen size to scale into," not "layout is hardcoded pixels
  throughout."
- Two real, narrower gaps remain, **not yet addressed**: the cart sidebar
  is a fixed 570px column (proportionate on a normal monitor, could feel
  oversized/undersized at extreme aspect ratios); and `MinWidth="1180"
  MinHeight="740"` is a hard floor that some genuinely small tablets
  (e.g. 1024×600) fall under - this would need a real layout audit of
  what breaks below that floor, not a one-line fix.

## Fix

`MainWindow`'s constructor now sets `WindowState = WindowState.Maximized`
unconditionally on open - it fills whatever screen (or window it's given
in a multi-monitor setup) rather than sitting at its XAML default size.

## Testing

UI-only, single-line change in `TorPos.App` - no Core/Infrastructure
logic touched, safety-test baseline (534/534) unaffected and not rerun
for this change. **Not visually verified on real hardware of different
sizes** - I have no way to run the actual Windows GUI in this
environment; this needs the user's own confirmation across their real
devices (desktop monitor, and eventually the HP L7010t/any tablet).

## Follow-up: lowered the MinWidth/MinHeight floor

Investigated the sub-1180×740 tablet case rather than leaving it open.
Finding: the category/product tile grid (`CreateButtonGrid` in
`MainWindow.Menu.cs`) uses fully proportional (`*`) column/row
definitions with no hard-coded pixel minimum - it was never the thing
constraining the floor. The two genuinely fixed elements are the header
rows (116px total) and the cart sidebar (570px fixed width). Since
nothing here can technically overlap/clip (everything else scales),
lowered `MinWidth="1180" MinHeight="740"` to `MinWidth="1024"
MinHeight="640"` to accommodate common small-tablet resolutions
(~1024×600 class devices) - the real risk at this new floor is visual
cramping, not breakage.

**Not addressed, flagged for real testing**: at the new 1024px floor, the
still-fixed 570px cart sidebar leaves only ~454px for the entire
category/product area - likely visually cramped (more than half the
screen consumed by the cart list). Making the sidebar itself responsive
(proportional with a min/max clamp, not just a smaller fixed number)
would be the real fix, but reworking it further without being able to
see the result firsthand is guessing, not verifying - left as-is until
the user can check on real small hardware.
