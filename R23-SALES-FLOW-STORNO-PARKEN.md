# R23 Sales Flow – Storno / Parken

Safe cashier shortcuts added on top of the confirmed R22 baseline:

- F1 = BAR
- F2 = KARTE
- F3 = PARKEN
- F4 = SOFORT STORNO (selected open-cart position only)

The existing click/touch handlers are reused. No fiscal, TSE, ZVT, database schema,
receipt calculation or completed-sale storno logic was changed.
