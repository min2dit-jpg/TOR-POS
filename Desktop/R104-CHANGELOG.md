# R104 — Kundendisplay (Customer-Facing Second Screen)

Explicit user request, discovered while troubleshooting R103: the customer
asked "will the QR code show on the customer screen?" and expected it did
not appear there. Investigation found the app already had TWO different
secondary-display concepts, in very different states:

- **"Bestellmonitor" (`order_display.*`)** — fully implemented, IMBISS
  order/pickup-number board (`OrderCustomerDisplayWindow`). Not affected.
- **"Kundenanzeige" (`device.customer_display.*`)** — only Settings
  scaffolding existed (a toggle + a `port` text field suggesting a
  serial/COM device), with **no actual driver or window ever built**. The
  user's real hardware (an HP L7010t POS touch monitor) is a genuine
  second Windows display, not a serial text device, so the existing
  `port` field never applied to it anyway.

User chose the full option: a genuine customer-facing display that mirrors
the live cart during checkout, then shows a "Vielen Dank" screen (with the
R103 QR when applicable) — not just routing the QR alone.

## Implementation

- **`CustomerDisplayWindow`** (`TorPos.App`) — a new fullscreen window,
  modeled closely on the existing `OrderCustomerDisplayWindow` (same
  "position on a configured screen index, `WindowState.FullScreen`"
  mechanism), but showing genuinely different content — explicitly kept
  as a separate class, since `OrderCustomerDisplayWindow`'s own doc
  comment states it deliberately carries no cart/price/payment
  information. Three states: **idle** ("Willkommen"), **cart** (live
  line items + running total, updated on every `UpdateCart()`), **thank
  you** (final total, plus the R103 QR when a digital receipt applies to
  that sale). The thank-you state reverts to idle via its own internal
  20-second timer — not driven by `MainWindow`'s cart-empty state, since
  `ClearCompletedCart()` empties the cart immediately after a sale
  commits and would otherwise instantly overwrite the thank-you screen
  before the customer could see it.
- **Settings**: replaced the dead `device.customer_display.port` field
  with `device.customer_display.screen_index` (same "0 = auto-detect
  second screen, 1–4 = fixed" combo as `order_display.screen_index`).
  Section is collapsible and available in any business mode (unlike the
  IMBISS-only Bestellmonitor section).
- **`MainWindow`**: `RefreshCustomerDisplayWindow()` mirrors
  `RefreshOrderDisplayWindow`'s exact open/close-on-settings-change
  lifecycle. `UpdateCart()` pushes live cart updates only when the cart
  is non-empty (see the "why not on empty" note above).
  `CommitCheckoutAsync` shows "thank you" on every completed sale
  (whether or not paper printed), with the R103 QR routed there instead
  of the main-screen popup (`DigitalReceiptWindow`) whenever a
  Kundendisplay is configured — the popup remains the fallback for tills
  without one.
- Shared `QrCodeRenderer.PngBytes` helper extracted so both
  `DigitalReceiptWindow` (main-screen fallback) and `CustomerDisplayWindow`
  render QR codes identically.

## Testing

UI-only feature — like `OrderCustomerDisplayWindow` before it, this can't
be exercised inside `TorPos.SafetyTests` (a console host that must never
initialize Avalonia's real rendering platform, per an established R87
lesson). Verified by build only; **real second-monitor/HP L7010t testing
is the user's own next step** (added to
`HARDWARE-ABNAHMETEST-DE.md` §9) — I cannot verify actual window
positioning, fullscreen behavior on real hardware, or the QR's on-screen
legibility from this environment.

Full suite: **534/534 checks passed** (unchanged from R103 — this
increment touches no Core/Infrastructure logic the suite covers, beyond
one new harmless seeded settings default).
