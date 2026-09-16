# R22 Sales Flow

After every successfully completed sale, TOR POS now uses the existing
`PrepareNextCustomer()` reset path.

This resets:
- scanner buffer/timer
- scanner processing state
- numeric input
- cart selection
- product paging

The same reset path is used after parking a receipt.

No TSE, ZVT, database schema, receipt totals, payment commit or fiscal logic
was changed.
