# R138 — Parked Receipts as Orders, Booked Sales Never Aborted

The user reviewed the parking behaviour against AEAO zu § 146a Nr. 2.2.2 and
2.2.3.6.2 and the BMF Kassen-FAQ. There are three cases:

- A short park followed by payment may keep the transaction open.
- A paid receipt must not leave a TSE transaction open.
- A receipt that waits long or into the next day belongs in the
  Bestellung / linked-records structure.

The user chose to secure every parked receipt as an order.

## The rules (checked against the texts)

- **AEAO Nr. 2.2.2:** start *"unmittelbar mit Beginn"*, end *"Bei Beendigung des
  Vorgangs"*.
- **AEAO Nr. 2.2.3.3:** *"Vor einer Belegausgabe oder zum Zeitpunkt eines
  Kassenabschlusses ist der Vorgang zwingend zu beenden."*
- **AEAO Nr. 2.2.3.6.2:** *"Langanhaltende Bestellvorgänge … werden als
  eigenständige Vorgänge realisiert. Deshalb sind diese über die Art des
  Vorgangs ‚Bestellung' abzubilden."* Invoice or payment: Kassenbeleg.
- **BMF Kassen-FAQ, "Geschäftsvorfälle, die länger als einen Tag andauern":**
  *"Nicht abgeschlossene Geschäftsvorfälle werden entweder als Bestellungen in
  eigenen Transaktionen oder als ‚andere Vorgänge' abgesichert, die in der
  DSFinV-K über den Abrechnungskreis oder eine Referenzierung miteinander
  verknüpft sind."*
- **BMF Kassen-FAQ, later price changes:** *"Alle Veränderungen müssen
  nachvollziehbar in Form einer Bestellung abgebildet werden. Die Summe aus der
  Menge multipliziert mit dem Bruttopreis aller Bestellungen muss dem
  Gesamtbruttobetrag der entsprechenden Rechnungen entsprechen."*

## What was wrong

- **Case 3.** A parked KIOSK receipt kept its Kassenbeleg-V1 transaction open
  until payment or deletion (R136). The Z-Bericht is blocked while receipts are
  parked, but a till without a Z-Bericht could keep that transaction open for
  days.
- **Case 2, a bug.** If the till stopped between booking the sale and ending its
  TSE transaction (crash, or a database error in signing), the next start ended
  the open transaction as **AVBelegabbruch**. A paid sale was documented as an
  aborted Vorgang and kept no TSE record.
- **Discount.** A manual receipt discount on a parked receipt or order was not
  part of the order records. The orders then did not add up to the gross amount
  of the receipt.

## The fix

- **Every parked receipt is an order.** Parking ends the running transaction as
  Bestellung-V1, with positions and discount, in any mode (R137 order records).
  - Re-parking with changes secures only the difference.
  - Deleting the receipt, or C on a recalled one, secures the reversal.
  - Paying starts and ends the Kassenbeleg at payment (DSFinV-K 2.7.2), with
    *Bestellbeginn* on the receipt.
  - Order and receipt share the Abrechnungskreis.
  - No transaction stays open while a receipt waits, however long it waits, and
    no time limit had to be invented.
  - A receipt parked under R136 still continues its open transaction and is
    secured the first time it is parked or paid again.
- **Booked sale with an open transaction.** At start-up, before a Z-Bericht, and
  right after a checkout error once the sale is found booked, TOR looks up open
  Vorgänge whose sale was committed. The payment journal holds the Vorgang id.
  - Such a transaction is ended with the data of that sale (Kassenbeleg-V1) on
    its own transaction number, and the sale gets its TSE record.
  - If the sale already has a TSE record, only the Vorgang is closed.
  - Only after that are Vorgänge without a booked sale ended as aborted.
- **Discount as order positions.** The receipt discount is recorded as `Rabatt`
  positions of the order, one per VAT rate, split exactly as on the receipt.
  - A changed discount at payment is secured as an order change before the
    receipt is booked.
  - All orders together equal the gross amount paid.
  - In the export these positions have GV_TYP Rabatt.

## Testing

`R138ReviewTests` (6 checks):
- discount split per rate, adding up to the amount paid;
- parking ends the transaction as Bestellung-V1 with positions and discount;
- a changed discount is its own order change, and orders equal the receipt;
- a booked sale is finished with its own data on its own transaction, and an
  already signed one only has its Vorgang closed;
- only a Vorgang without a booked sale is aborted;
- Rabatt positions appear in the export.

Safety suite **800/800** (794 before) under en-US, de-DE and tr-TR. UI layout
check passed.

## Still open

- Training in IMBISS order mode stays a simulation (R135).
- Kassensturz difference (DifferenzSollIst) not booked yet.
- A training sale whose recording failed after its Vorgang began still ends as
  an aborted training Vorgang (no fiscal effect).
- Real hardware: Swissbit TAR export, many transactions.
