# R147 — Training Receipt Linked to Its Training Order

The second open point from the handoff list, checked against the spec first.

## The rules

- **DSFinV-K 2.7.1:** when orders are secured as transactions of their own,
  *"ist sicherzustellen, dass das Feld ABRECHNUNGSKREIS in der Datei
  Bonkopf_AbrKreis … ein Kriterium … enthält über das ein inhaltlicher Zusammenhang
  hergestellt werden kann"*, so that each business transaction can be followed
  from its orders to the receipt.
- **DSFinV-K 2.7.2:** the same link is a condition for starting the Kassenbeleg at
  payment.
- **DSFinV-K Anhang B, AVTraining:** all training actions are documented and mapped
  in the DSFinV-K.

## What was wrong

- **Real receipts were linked.** They reach their order records through
  `parked_receipts.cashed_sale_id` and share its Abrechnungskreis (R137).
- **Training receipts were not.** Since R142 training orders are secured and
  exported as AVTraining. A training order that is paid is only marked SIMULATED,
  and the training receipt (TR-n) was stored with no reference to it, so the
  export could not link the two.

## The fix

- **Schema 19.** The immutable table `training_receipt_orders` records which
  training order a training receipt paid.
- **Written with the receipt.** The link is written in the same transaction as the
  training receipt, from the checkout's parked receipt id.
- **Export.** The training receipt gets the Abrechnungskreis of its training order
  records (`Bonkopf_AbrKreis`), exactly as a real receipt does. A training receipt
  without an order gets none.

## Testing

`R147ReviewTests` (3 checks):
- migration V19;
- link stored immutable, none without an order;
- export: TR-1 carries the Abrechnungskreis of its order records, TR-2 none.

Safety suite **839/839** (836 before) under en-US, de-DE and tr-TR.
