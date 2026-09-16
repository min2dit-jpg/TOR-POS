# R28 – Open Cart Recovery

Stability Offensive: crash/power-loss protection for the currently open receipt.

- The active cart is persisted to `%APPDATA%\TOR-POS-Pro\open-cart-recovery.json` after each cart update.
- The snapshot contains positions, quantities, prices, VAT data, discount and parked-receipt linkage.
- On the next cashier window startup, a non-empty recovery snapshot is restored automatically.
- A completed/cleared receipt removes the recovery snapshot because the cart becomes empty.
- Writes use a temporary file followed by replacement to reduce partial-write risk.
- A damaged recovery file must never block application startup.
- Existing parked-receipt save-on-close behavior remains unchanged.

No changes to TSE, ZVT, DSFinV-K, sale commit, receipt numbering, tax calculation or database schema.
