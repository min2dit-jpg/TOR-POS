# R146 — Positions Cancelled Before an Order Is Accepted Belong to Its Record

The open point left by R143, checked against the spec first.

## The rules

- **DSFinV-K 4.2.3:** a position cancelled during capture is documented either with
  P_STORNO = 1 or by *"ein zusätzlicher Positionsdatensatz …, bei dem MENGE mit
  negiertem Vorzeichen dargestellt wird"*.
- **DSFinV-K 4.2.3, orders:** *"Bestellungen sind … gesondert abzusichern, da es sich
  bei diesen Vorgängen um eigenständige Vorgänge handelt."*
- **AEAO zu § 146a Nr. 1.11.1:** the Sofort-Stornierung is among the Vorgänge needed
  to document complete recording.

## What was wrong

- **Lost at parking.** The Vorgang tracker (R136/R143) collects every position
  cancelled while a cart is captured, but parking cleared that list. When an order
  was accepted (R137, every parked receipt since R138), its record held only what
  the order contained at that moment.
  - Neither the TSE processData (Bestellung-V1) nor the DSFinV-K export showed the
    cancelled positions.
- **Change and cancellation.** The same happened when a recalled order was changed
  or cancelled with C.
- **Aborts.** Parking on a till that stopped booking for real (Vorgang ends as
  aborted) dropped the cancelled positions too.

## The fix

- **Captured with the Vorgang.** At parking, and at C on a recalled order, the
  cancelled positions are taken with the Vorgang.
- **Appended to the record.** `OrderFiscalSigningService`
  (`SignInVorgangAsync`, `SecureChangeAsync`, `SecureCancellationAsync`) appends
  them behind the record's own positions:
  - each as the captured position plus its negated counterpart;
  - in the Bestellung-V1 processData and in the stored record, so TSE data and
    export agree.
- **Totals unchanged.** The pairs add up to nothing. What the records add up to
  (BMF Kassen-FAQ: orders = invoice total), the net secured positions and the
  record amounts stay as they were.
- **No net change.** A change that ends where it began still secures no record; its
  aborted Vorgang now keeps the cancelled positions (as every abort since R143).
- **Aborts at parking.** The abort paths at parking carry them too.

## Testing

`R146ReviewTests` (5 checks):
- acceptance with a lowered quantity and a SOFORT STORNO before it (processData,
  net secured positions);
- a change that nets nothing (no record, aborted Vorgang with the pair, total 0);
- a change (difference plus pairs);
- a cancellation with C (reversal plus pairs, records add up to nothing);
- DSFinV-K export: MENGE rows of acceptance and cancellation, P_STORNO 0,
  UMS_BRUTTO unchanged.

Safety suite **836/836** (831 before) under en-US, de-DE and tr-TR.
