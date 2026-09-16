# R27 Card Test Hotfix

Problem observed on R26:
- F1 opened the cash payment dialog.
- F2 in unlicensed development test mode immediately completed the simulated
  card sale and cleared the cart.

Fix:
- F2 now opens a dedicated KARTENZAHLUNG · TEST confirmation window.
- ABBRECHEN keeps the receipt/cart open.
- KARTE BESTÄTIGEN completes the simulated test sale.
- No real ZVT payment is performed in unlicensed test mode.
- The shared R26 payment-in-progress guard remains active.

No changes to TSE, ZVT production protocol, DSFinV-K, database schema,
tax calculation, receipt numbering, or production card commit logic.
