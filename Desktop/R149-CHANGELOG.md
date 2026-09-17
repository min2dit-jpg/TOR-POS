# R149 — PFAND / LEERGUT Key Takes Back Empties; Sale Events Reach TOR Cloud

The owner's decision on the open R148 point: *"Pfand düğmesi yanlış, fiyatlar minus
olmalı satış olarak değil, kasadan para çıkıyor. Steuer'i de düzelt."* The key
takes back empties. Money leaves the till, and the tax is corrected.

## The rules

- **DSFinV-K Anhang C, PfandRueckzahlung:** documents *"alle Rückgaben von
  Pfandgegenständen sowie die Verrechnung des Pfandbetrages oder die Auszahlung an
  den Kunden"*.
- **The rate:**
  - A bottle is a Warenumschließung. It shares the fate of the main supply: milk
    7 %, its bottle deposit 7 %.
  - A crate is a Transporthilfsmittel, a supply of its own at the general rate
    (§ 12 Abs. 1 UStG).
  - The refund reduces the consideration at that rate.
- **DSFinV-K 4.2.5:** with a negative position *"lediglich das Vorzeichen für das
  Feld MENGE ändert sich"*.

## What was wrong

- **The key sold deposit.** It added deposit as a sale at a fixed 19 %. It could not
  take anything back: the cart refuses negative quantities, and a price below zero
  was cut to 0. A deposit paid back from the drawer left no Geschäftsvorfall.
- **Found on the way: TOR Cloud rejected every sale of a real till.**
  - The till sends the discount as `manual_discount_cents`; the Cloud required
    `discount_cents` and refused the event.
  - The Cloud accepted only CASH and CARD, not MIXED (R101).
  - The first rejected event blocks the outbox, which keeps its order, so nothing
    after it would have arrived either.
  - It never showed because a till that is not released for real bookings sends no
    sale events. A contract file on both sides now pins the event, as R145 did for
    the digital receipt.

## The fix

### Till

- **Pfand-Rückgabe dialog.** The PFAND / LEERGUT key opens "Leergut zurücknehmen".
  - Bottle deposit 8/15/25 cent comes with a rate choice:
    - **Getränke · 19 %** is the default;
    - **Milch / Milchgetränk · 7 %** for milk and milk drinks.
  - Crate empty/full is always 19 %.
  - The quantity is entered before, with the digit keys.
  - The position has a negative amount (`SaleEngine.AddDepositReturn`). It is not
    subject to Im Haus, and it is merged per deposit and rate.
- **Total** (`ReceiptTotals`):
  - A discount still never makes a purchase negative.
  - Returned deposit can make the receipt a **payout**.
  - A manual discount is not combined with returned deposit (refused in the cart,
    at RABATT and when booking).
- **Payout.**
  - Only in cash: card and GEMISCHT are refused.
  - It is confirmed in "PFAND AUSZAHLEN" once the money is handed over.
  - The receipt prints "PFAND-AUSZAHLUNG BAR".
- **Elsewhere at the till.**
  - Returned deposit cannot be selected for a Retoure.
  - Returned deposit is not printed on kitchen tickets.
  - The action log records a negative cart total as it was.
- **TSE.**
  - Kassenbeleg-V1 carries the negative amounts.
  - In an order record (Bestellung-V1) returned deposit is written with negative
    quantity and positive price. A discount position (R138) is unchanged.
- **DSFinV-K.**
  - Returned deposit is GV_TYP **PfandRueckzahlung**, with negative MENGE and
    positive STK_BR.
  - The closing shows it apart from turnover, and the payout under cash.

### TOR Cloud

- **Sale events.** They are accepted with `discount_cents` (still with
  `manual_discount_cents` alone from older tills), MIXED, and a negative total
  paid out in cash; a payout on card is refused. The till now sends
  `discount_cents` too.
- **Digital receipt.** The digital receipt (R145) of a payout is accepted by the
  same total rule.
- **Domain.** The examples in the deployment files, docs and tests use
  **torpos.de** (api.torpos.de, bon.torpos.de).

## Testing

- **`R149ReviewTests`** (11 checks):
  - rate rule;
  - cart position, merging, payout total, discount refused, Im Haus unchanged;
  - total rule;
  - cash/card split of a payout;
  - VAT per rate with mixed signs;
  - Kassenbeleg-V1 and Bestellung-V1 data;
  - card payout and discount+deposit refused before booking;
  - Retoure refused, negative action log total;
  - DSFinV-K rows and closing;
  - the sale event equals `Cloud/tests/fixtures/sale-completed-kasse.json`;
  - digital receipt of a payout.
- **Safety suite 853/853** (842 before) under en-US, de-DE and tr-TR.
- **UI layout check** passed. It now also renders the Pfand-Rückgabe and payout
  dialogs.
- **Cloud 41/41:**
  - the till's sale event;
  - MIXED;
  - an older till without `discount_cents`;
  - payout on card refused;
  - digital receipt of a payout.
