# R109 — Small-Screen Cart Panel (the last R105 gap)

User asked to pick the next priority ("önceliği sen belirle"). Closed the
one concrete, still-open gap R105 itself had flagged and left unaddressed:
the cart panel's fixed 570px column width.

## The problem

`MainWindow.axaml`'s root Grid was `ColumnDefinitions="*,570"` — the cart
column stayed a hard-coded 570px regardless of window size, while the
product/category side got whatever was left. At R105's own lowered
`MinWidth=1024` floor, that meant the cart consumed 570/1024 = **56% of
the window on the smallest supported screen** — backwards from what a
small tablet actually needs (more room for products, less for the cart).

## The fix

Converted the shorthand `ColumnDefinitions="*,570"` to explicit
`<ColumnDefinition>` elements: the product side stays `Width="*"` with a
`MinWidth="480"` floor; the cart column becomes `Width="0.42*"` bounded by
`MinWidth="380"`/`MaxWidth="650"`. The 0.42 ratio is calibrated so a normal
maximized desktop (~1920px wide) still renders the cart at close to its
original ~570px — no visual change on the screens this was already
designed and used against — while a small tablet at the 1024px floor now
gives the cart only ~380px (its floor) and the product grid the remaining
~640px, instead of the previous inverted 570/454 split. A very wide
monitor is capped at 650px so the cart never grows unreasonably large
either.

Checked the cart panel's actual content (`CartList`) for any internal
fixed-pixel sub-layout that could break as the column narrows: it's a
plain `ListBox` of strings, no fixed-width sub-columns (unlike a DataGrid)
- safe to shrink, degrades by truncating long lines, never overlaps or
clips other elements.

## Testing

XAML-only layout change — no `TorPos.SafetyTests` assertions apply (this
suite deliberately never initializes Avalonia's real rendering platform,
R87). Full suite re-run for regression: **558/558 checks passed**,
unchanged from R108. **Visual verification on an actual small-screen
device is the user's own next step** — this session has no way to render
or screenshot the live Avalonia window.
