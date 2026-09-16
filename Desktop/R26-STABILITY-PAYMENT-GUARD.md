# R26 Stabilitäts-Offensive 1 – Payment Guard

Adds a shared checkout lock for BAR and KARTE.

Protected cases:
- fast double tap/click on payment
- F1/F2 repeated while checkout is already running
- trying BAR while a card terminal transaction is in progress
- trying KARTE while the cash dialog is open
- training and unlicensed test checkout use the same protection

The lock is always released in finally, including cancel/error paths.

Not changed:
- TSE/fiscal commit logic
- ZVT payment protocol
- DSFinV-K
- receipt numbering
- database schema
- totals/tax calculation
