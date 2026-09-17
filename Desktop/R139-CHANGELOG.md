# R139 — Kassensturz Books Its Difference; Calculated Cash Runs On

The first item of the open list in order of legal weight: the Kassensturz
difference (DSFinV-K DifferenzSollIst).

## The rules

- **DSFinV-K Anhang C, DifferenzSollIst:** *"Der Geschäftsvorfall
  ‚DifferenzSollIst' stellt die Abweichung zwischen einem errechneten und dem
  gezählten Kassenbestand dar, der bei Überprüfung der Kassensturzfähigkeit bzw.
  beim Kassensturz auftreten kann. Differenzen können so festgestellt,
  protokolliert und ausgeglichen werden. Es kann sich sowohl um Fehlbeträge als
  auch um positive Differenzen handeln."* 4.1.3 lists it among the GV types that
  affect only the cash.
- **DSFinV-K Anhang C, Anfangsbestand:** *"Wird im Rahmen des vorhergehenden
  Kassenabschlusses das Bargeld vollständig entnommen, beträgt der
  Anfangsbestand 0,00 … Das Auffüllen des Bargeldbestandes ist über den
  Geschäftsvorfalltyp ‚Geldtransit' zu erfassen."* Cash is counted on without a
  break at a closing: removals and refills are booked, not reset.
- **DSFinV-K 1 / 3.3.3:** the data are to ensure *"eine jederzeitige
  Kassensturzfähigkeit"*.
- **AEAO zu § 146a Nr. 2.2.3.6.1:** abgeschlossene Vorgänge affecting only the
  business itself (Eigenbelege über Ein- oder Auszahlungen) are secured as
  Kassenbeleg — as for Einlage/Entnahme since R134.

## What was wrong

- The Kassensturz stored only the counted amount (as a test entry). The
  difference appeared on the printout only: it was not booked, not TSE-secured,
  and absent from the DSFinV-K export. The next Kassensturz showed the same
  difference again.
- The calculated cash started again from the fixed "Startgeld" setting after
  every Z-Bericht. Cash left in the drawer overnight looked like a surplus, and
  money taken out without booking looked like a correct drawer.

## The fix

- **Continuous calculated cash** (`GetCashBalanceAsync`):
  - The starting point is the last confirmed Kassensturz, plus cash sales
    (Storno/Retoure reduce it), Einlagen and Entnahmen since then.
  - A Z-Bericht no longer resets it.
  - Before the first Kassensturz, the setting (now labelled "Anfangsbestand in
    Cent") is the starting point.
  - Real bookings and test entries are kept apart.
- **Confirmed Kassensturz** (`BookCashCountAsync`):
  - After entering the counted amount, the cashier sees Soll, Ist and the
    difference and chooses DIFFERENZ BUCHEN / BESTÄTIGEN, NEU ZÄHLEN or
    ABBRECHEN.
  - The dialog reminds them to book unrecorded removals (e.g. to the bank) as
    Entnahme "Geldtransit" first.
  - On confirmation, one database transaction books the difference as
    DifferenzSollIst (surplus = Einlage, shortfall = Entnahme) and stores the
    count as the new starting point, with an optional note.
  - Every confirmed count is logged with Soll, Ist and difference; an abandoned
    one is logged too.
- **TSE and export.**
  - On a till that books for real, the difference is signed like any cash
    movement (Kassenbeleg-V1, 0 % container, Bar).
  - The export shows GV_TYP DifferenzSollIst ("Kassendifferenz (Fehlbetrag /
    Überschuss beim Kassensturz)") in the Einzelaufzeichnung and the closing.
  - A test till books nothing fiscal. The circuit breaker guards the real
    booking.
- A difference cannot be entered as a manual Einlage/Entnahme.

## Testing

`R139ReviewTests` (9 checks):
- business case rules; manual difference refused;
- calculated cash across a Z-Bericht, without test entries;
- confirmed count as the new starting point;
- real booking refused while the breaker is off;
- shortfall, surplus and zero difference booked correctly;
- count logged with Soll/Ist/difference;
- TSE processData `Beleg^0.00_0.00_0.00_0.00_-3.50^-3.50:Bar`;
- export GV_TYP DifferenzSollIst in lines and closing.

Safety suite **809/809** (800 before) under en-US, de-DE and tr-TR. UI layout
check passed.

## Note for the business

Money taken to the bank or the safe has to be booked as an Entnahme
"Geldtransit" before it leaves the drawer. Otherwise the next Kassensturz shows
it as a shortfall — which is then correct documentation of an unbooked removal.

## Still open

- `GetExpectedCashCentsAsync` (cash flows since the last closing) remains for
  the old reports/tests; the till no longer uses it.
- Next items: receipt QR code in the DSFinV-K format (Anhang I), training in
  IMBISS order mode, Z-Bericht payment split of Storno/Retoure.
