# R137 — Order Changes and Cancellations Secured on Their Own

The remaining order gap from the R131 export hints (BESTELLUNG, BESTELLSTORNO)
and the DSFinV-K 2.7.2 item left open in R136, decided by what the rules say.
On the way a bug was found that affected every test till in IMBISS order mode.

## The rules

- **DSFinV-K 4.2.3:** *"Bestellungen sind bei Anwendung der
  Vereinfachungsregelung nach Tz. 2.7 gesondert abzusichern, da es sich bei
  diesen Vorgängen um eigenständige Vorgänge handelt. Im Falle einer Stornierung
  einer ganzen Bestellung darf das Feld P_STORNO nicht verwendet werden, sondern
  es muss für eine Stornierung ein neuer Datensatz mit umgekehrtem Vorzeichen
  erzeugt werden, der wiederum abgesichert werden muss."* Once signed, a position
  is never changed (*"Sobald die Transaktion in der TSE signiert ist, darf das
  Feld P_STORNO nicht mehr verwendet werden"*); a removed position is a record
  with negated quantity.
- **BON_STORNO (Anhang, Feldbeschreibung):** with a TSE, the reversing record is
  marked BON_STORNO = 1, the original stays unchanged.
- **DSFinV-K 2.7.1:** order transactions and the receipt are linked through
  ABRECHNUNGSKREIS.
- **DSFinV-K 2.7.2:** with secured orders the Kassenbeleg transaction may start
  at payment and finish right away — provided *"Der Start-Zeitpunkt der ersten
  Transaktion ‚Bestellung' muss zusätzlich auf dem Beleg abgedruckt werden."*
- **Anhang I, Bestellung-V1:** `<Menge>;"<Bezeichnung>";<Preis>`, lines separated
  by CR.

## What was wrong

- Only the acceptance of an order was signed. A change (re-parked with other
  positions) and a cancellation (C on a recalled order, deleting it from the
  parked receipts) left nothing in the TSE. The export showed each order once,
  with its last saved positions.
- The order positions in the export carried the base VAT rate even for an Im Haus
  order.
- **Bug:** a test till also signed every accepted order. With the TSE inactive this
  logged a TSE outage for each order (TSE-AUSFALL badge), and since R132 these
  signatures counted as Vorgänge waiting for a closing. As a result, changing the
  company data or starting a new TOR version on a test till created an automatic
  Z-Bericht. Sales (R113), cash movements (R134) and training (R135) already
  followed the rule that a test till records nothing fiscal. Orders did not.
- The receipt of a paid order did not show when the order began.

## The fix

- **Order records.** Acceptance, change and cancellation are each their own
  Bestellung-V1 transaction, stored once and immutable in `order_bestellungen`
  and `order_bestellung_items` (migration 17), with positions and TSE result.
  What the TSE secured for an order is the sum of its records
  (`OrderBestellungDelta`).
  - **Acceptance:** all positions, at the rate that applies (Im Haus).
  - **Change:** only the difference; added positions are positive, removed ones
    negative. Switching Im Haus is secured too, as a rate change.
  - **Cancellation:** everything secured so far, with reversed sign.
  - **Pickup/payment:** if the paid positions differ from the secured order
    (something left out, something added), that change is secured before the
    receipt is booked.
- **Which transaction.** A recalled order starts nothing while it is only looked
  at. Its first change starts the Vorgang, which then ends as the change or
  cancellation record. Paid unchanged, the Kassenbeleg starts and ends at
  payment (2.7.2). A change that is undone before saving signs nothing, and its
  Vorgang ends as aborted.
- **Test till.** Orders are secured only on a till that books for real. For
  orders signed before R137, the closing guard only counts those that were paid
  with a real sale, which a test till cannot produce.
- **Receipt.** Printed and digital receipts of a paid order show
  *Bestellbeginn*: the TSE start time of the first order transaction.
- **Export.** Every record is an AVBestellung `BE-<Park-Nr>-<n>`:
  - BON_NAME Bestellung / Bestelländerung / Bestellstorno.
  - A cancellation has BON_STORNO 1 and a Bon_Referenzen record to the
    acceptance.
  - Amounts are signed, positions carry the Im Haus rate.
  - All records share the Abrechnungskreis of the receipt.
  - No effect on the closing totals.
  - The hints BESTELLUNG/BESTELLSTORNO now only name orders from before R137.
- **Orders from before R137.** Before their first R137 record, the acceptance is
  written down with the signature the order received then and the positions it
  had, so a later cancellation reverses all of it.

## Testing

`R137ReviewTests` (16 checks):
- **Delta logic:** migration; acceptance/change/cancellation adding up; Im Haus
  rate change and negative Anhang I quantity; recalled order starts no Vorgang
  until it changes.
- **Signing flow:**
  - acceptance on its own transaction with the order row kept;
  - change finishing the change Vorgang with only the difference;
  - unchanged save signs nothing and aborts the Vorgang;
  - cancellation reversed, net zero;
  - records immutable;
  - paid order carries Bestellbeginn with the Im Haus rate;
  - pre-R137 order transcribed then reversed completely.
- **Closing guard:** a test till's old order signature forces no closing.
- **Export:** three AVBestellung with names, BON_STORNO, signed amounts; reference
  and own transactions; shared Abrechnungskreis, Im Haus key, closing untouched;
  no order hints.

Safety suite **794/794** (778 before) under en-US, de-DE and tr-TR. UI layout
check passed.

## Still open

- Parked KIOSK receipt: its Kassenbeleg-V1 transaction stays open until payment
  (R136). Confirm with the tax advisor.
- Training in IMBISS order mode stays a simulation (R135).
- Kassensturz difference (DifferenzSollIst) not booked yet.
- Real hardware: Swissbit TAR export, many open transactions.
