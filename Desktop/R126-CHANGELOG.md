# R126 — Small Screens

From photos of the same version on two tills: a Lenovo laptop and the small HP
touch screen.

## What the photos showed

1. **The header ran off the right edge.** On the HP screen nothing after
   "AUS…" was visible — **AUSSER HAUS, GEMISCHT and ABMELDEN could not be
   reached at all.** That is not cosmetic: AUSSER HAUS/IM HAUS selects the VAT
   rate, GEMISCHT is split payment, ABMELDEN hands over the till. On the laptop
   ABMELDEN was cut in half, and on both the company name had disappeared.
2. **The numpad digits were cut in half.** "0" read as "n", "9" as "q", and
   the "," key looked empty.
3. Labels were broken mid-word or cut: "BESTELLUN / G ANNEHME / N",
   "SOFORT STO", "EINGABI", "ANTIPPE".
4. On the laptop C, EXTRA and SCHNELLARTIKEL were grey; on the HP the same
   version showed them active.
5. All category tiles blue on the HP, coloured on the laptop.

## Causes and fixes

**Header.** Everything right of the logo — menu, every status badge and the
essential buttons — was one horizontal `StackPanel`, which cannot shrink. When
the screen was narrower than its content, the company name (the only flexible
column) went to zero and the rest was drawn past the window edge. The
TSE-AUSFALL badge added in R113 took the last free space; even before it the
name was already truncated on the laptop.

Now the essential buttons have their own column that is laid out first and is
never clipped. Everything else lives in a host that clips only as a last
resort, and the status texts come in three lengths chosen by measurement
(`TorPos.Core.HeaderDensityPolicy`):

| | Full | Compact | Minimal |
|---|---|---|---|
| TSE | TSE-AUSFALL · seit 16.09. 10:16 | TSE-AUSFALL | TSE-AUSFALL |
| Fiscal | TESTBETRIEB · KEINE LIZENZ · KEINE ECHTE BUCHUNG | TEST · KEINE LIZENZ | TEST |
| User | admin · Vollzugriff | admin | *(hidden)* |

The TSE outage stays named at every length — it is a legal state; only its
start time moves into the tooltip. Every shortened text keeps the full one as a
tooltip. Minimal also hides the IMBISS badge, which is repeated in the category
header anyway.

**Numpad.** A key needed about 50px for a 24pt digit plus 12px padding and
margin; on the HP a row was about 37px, and Avalonia draws the text from the
top and clips the rest. The digit now sits in a `Viewbox` that only scales
*down*, padding is smaller, and the numpad has a minimum height of its own
(the cart panel's 250px minimum used to take everything first). The payment
row shares the space instead of a fixed 78px, capped at 78px so a large screen
looks exactly as before.

**Labels.** Action keys shrink to fit instead of clipping; two-word labels
break between words; the empty-cart hint wraps; LOKALE KASSE is hidden when the
cart panel is narrow, so EINGABE — the quantity being typed — is never the part
that gets cut.

**Grey keys.** `RefreshSalesActionState()` ran while the start-up cart
recovery was still pending (so `CartLocked` was true) and was never run again
once recovery finished. C, EXTRA and SCHNELLARTIKEL stayed disabled after every
start until the cashier happened to change the cart. The HP till had already
been used, the laptop had not — that was the whole difference. It is now
recomputed as soon as recovery completes.

**Tile colours: not a bug.** A Warengruppe without a chosen colour gets the
default dark blue `#17466A`, and the IMBISS starter categories are created
without colours. The laptop's colours were picked by hand in
Artikelverwaltung; on the HP they never were.

## Seeing the screen before it ships

Every layout change in this project so far was checked from photos of real
tills after the fact — R113's badge broke the header exactly that way. New
development tool **`tools/TorPos.UiSnapshot`** renders the real `MainWindow`
off-screen (Avalonia headless + Skia, the same Windows fonts) at 1920×1080,
1366×768, 1280×800, 1280×720 and 1024×640 and saves PNGs. With `--check` it
fails when ABMELDEN, GEMISCHT, AUSSER HAUS or the TSE-AUSFALL badge are not
entirely on screen, or a numpad key is shorter than 34px.

It was run against the old layout first and failed where the tills did —
e.g. at 1366×768 "LogoutButton extends past the window (1453..1559 of 1366)",
at 1024×640 "numpad key '7' is only 31px tall" — and passes with the fix. The
GitHub CI now runs it after the safety tests and uploads the screenshots with
the logs.

**Safety:** on Windows `%APPDATA%` cannot be redirected through the
environment, and this development PC is also a till with real data. The tool
therefore points `AppPaths` at a throwaway folder through an `internal`
override visible only to the tool, and the cart-recovery file — the one path
that bypassed `AppPaths` — now goes through it too (same folder in production).
Every run was checked against the real data folder: untouched.

## Also

`TorPos.App.csproj` still carried `Version 0.7.33.751` and
`InformationalVersion …-R75.1-Application-Namespace-Hotfix` — the version
Windows shows in the exe's file properties. Now in step with `TorRelease`,
the installer script and `manifest.json`.

## Testing

`R126ReviewTests` (8): the density rule, the TSE label at every length, the
company name threshold, and that the data-folder override resolves everything
inside the sandbox and leaves production paths unchanged.
Desktop **671/671**, run twice. Layout check: 5/5 sizes pass (Debug and
Release); fails against the previous layout as shown above.

Not verifiable from here: touch behaviour and the real HP screen's exact
resolution and scaling — the check covers 1024×640 as the smallest supported
window.
