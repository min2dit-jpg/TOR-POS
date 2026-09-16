# R37 – SumUp Solo 1,00 € device checkout test

Purpose: verify the missing link TOR POS → SumUp Cloud → selected Solo without integrating SumUp into the normal sales flow yet.

Changes:
- Settings / Devices SumUp admin window now has `3 · 1,00 € TEST AN SOLO SENDEN`.
- The button calls the official Reader Checkout endpoint with a fixed amount of EUR 1.00 (`value=100`, `minor_unit=2`).
- The returned checkout ID is shown only for diagnostics and is not persisted.
- `4 · TEST ABBRECHEN` calls the official Reader terminate endpoint.
- Normal `KARTE TEST` on the cash-register screen remains simulation and is unchanged.
- API key stays masked and memory-only; no response body is exposed on HTTP errors.
- This is a physical-device connectivity test, not a production payment integration.

Safety rule for the physical test:
1. Send EUR 1.00.
2. Verify that EUR 1.00 appears on Solo.
3. Do NOT present a card.
4. Immediately press TEST ABBRECHEN or cancel on Solo.

Official API reference checked 2026-09-07:
- https://developer.sumup.com/api/readers
- https://developer.sumup.com/terminal-payments/cloud-api
