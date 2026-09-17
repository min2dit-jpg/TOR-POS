# R148 — Deposit of the PFAND / LEERGUT Key Is Business Case "Pfand"

Found while checking the open roadmap point "Pfand-Steuerlogik fachlich final
validieren" against the spec.

## The rules

- **DSFinV-K Anhang C, Pfand:** *"Im Geschäftsvorfalltyp ‚Pfand' werden alle
  Pfandeinnahmen aus Handelsgeschäften dargestellt."*
- **Bottle deposit** (Warenumschließung) is a dependent ancillary supply and takes
  the rate of the goods: milk 7 %, its bottle deposit 7 %.
- **Crate deposit** (Transporthilfsmittel) is a supply of its own at the general
  rate (§ 12 Abs. 1 UStG).
- **PfandRueckzahlung** documents returned deposit items and the deposit paid back.

## What was wrong

- **Article deposit was already right.** Deposit set on an article was exported as
  its own Pfand position at the article's rate since R131.
- **The key's deposit was not.** Deposit added with the PFAND / LEERGUT key (8, 15,
  25 cent, crate empty/full) was exported as **Umsatz**, and the closing summed it
  with the turnover (Z_GV_Typ).

## The fix

- **Identified by id.** `PfandProducts` names the technical product ids of the key's
  positions.
- **Export.** The DSFinV-K export writes these positions as GV_TYP **Pfand**, so the
  closing shows deposit apart from turnover.
- **Nothing else changes.** Amounts, receipt and TSE data are unchanged.

## Testing

`R148ReviewTests` (3 checks):
- the ids;
- position business cases Umsatz/Pfand/Pfand;
- closing totals Pfand 1,75 / Umsatz 2,50.

Safety suite **842/842** (839 before) under en-US, de-DE and tr-TR.

## Open: needs a decision (not changed)

- **Returned empties.** They cannot be recorded at all: the cart accepts no negative
  quantity, and there is no Leergut-Rücknahme. A deposit paid back from the drawer
  therefore leaves no Geschäftsvorfall (PfandRueckzahlung).
- **Rate of the key's bottle deposit.** It is always 19 %. That is right for the
  usual beverages but not for deposit on a 7 % product such as milk (Warenumschließung
  follows the goods). Deposit set on the article itself already follows the article.
