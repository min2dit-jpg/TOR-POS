# R123 — German Documents Keep German Numbers

Builds on PR #1 (`fix/report-currency-culture`).

## What PR #1 found

The STORNO report wrote `5.00 EUR` instead of `5,00 EUR` whenever the Windows
account ran under an English culture. `$"{cents / 100m:0.00}"` formats with the
**current** culture, and nothing in TOR POS sets one. The PR fixed the report's
`Money` helper and proved it with a test that runs the report inside the I/O
queue under `en-US` - correctly, because `IoQueue` has its own execution
context and setting the culture on the caller alone would not reach it.

## What reviewing it found

The same pattern in more places. The most serious one is the **printed
Kassenbon itself**: `StarMcPrint3PrinterService.Money` was
`(cents / 100m).ToString("0.00")`, so a legal German receipt printed its
amounts with a decimal point on an English Windows. Also affected:

| Output | Field |
|---|---|
| Kassenbon | amounts, quantities (`1.5 x`), VAT rates (`MwSt 5.5%`) |
| Küchenbon | quantities |
| Digital receipt | quantities, VAT rates (amounts were already `de-DE`) |
| WARENBESTAND, VERKAUFSSTATISTIK, monthly package | stock quantities, VAT rates |
| Article label PDF | price |
| KASSENSTURZ printout | Soll / Ist / Differenz |
| Z-Archiv list, inventory list, Z-Abschluss status | amounts |

R49 already decided that printed and fiscal output stays German whatever the UI
language is; this makes that true for numbers, not only for words.

## The fix

`TorPos.Core.GermanFormat` - `Eur`, `Amount`, `Number`, and `Line` for whole
interpolated strings - used at every site above. PR #1's report helper now
delegates to it too, so there is one rule instead of three copies.

Parsing was checked at the same time and is already culture-safe: typed
quantities and stock use `InvariantCulture` with `,` normalized to `.`, money
input goes through `Formatting.TryParseMoney` with `de-DE`.

Deliberately **not** changed: technician-only diagnostics (MB, ms, iteration
counts) and on-screen quantity hints in the cashier UI, which the operator
never hands to anyone.

## Testing

`R123ReviewTests` - every check runs under `en-US`, the culture that exposes
the bug: the shared rule, the printer's own `Money` formatter, the rendered
digital receipt, and the WARENBESTAND report run through the I/O queue.
Verified in both directions: with the source changes stashed, the Kassenbon
check fails with `actual: 5.00 EUR`; restored, it passes.

Desktop: **655/655**, run twice (648 on the PR branch before).
