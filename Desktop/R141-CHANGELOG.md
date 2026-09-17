# R141 — X/Z-Bericht: Payment Types Net of Reversals, Cash Movements Shown

Taken before "training in IMBISS order mode" because it concerns the correctness
of every Z-Bericht on a till with cash Stornos or returns. Training orders
already are secured, with no fiscal effect; only how they are classified is
still open.

## Why it matters

The Z-Bericht is the daily closing record (Tagesendsummenbon) the Kassenbuch is
built on. Its figures have to agree with each other and with the DSFinV-K
Kassenabschluss. Z_Zahlart there is net and includes every cash Beleg: sales,
Storno, Retoure, Einlage, Entnahme and Kassendifferenz.

## What was wrong

- "ZAHLARTEN Bar / Karte" summed sales only. On a day with a cash Storno the
  Z-Bericht showed more cash than was taken, and Bar + Karte did not match the
  "Umsatz nach Storno/Retouren" printed a few lines above.
- Einlagen, Entnahmen and, since R139, Kassendifferenzen did not appear in the
  report at all.

## The fix

- **Payment types.** Bar and Karte are net: a Storno/Retoure is taken off the
  payment types it was paid back in (R102 split). Bar + Karte = turnover after
  Storno/Retouren. The archived Z-Bericht stores these net figures.
- **Cash movements.** A new section "KASSENBEWEGUNGEN (BAR)" lists the Einlagen,
  Entnahmen and Kassendifferenzen of the period by business case, followed by
  "Bar-Saldo des Zeitraums (Bar-Umsatz + Kassenbewegungen)". Test entries never
  count.

## Testing

`R141ReviewTests` (3 checks):
- cash, card and mixed sales with a cash Storno and a card Retoure give
  Bar 3,00 / Karte 8,00 = 11,00 after reversals;
- Einlage Geldtransit, Privatentnahme and Kassendifferenz are listed with the
  cash balance, the test entry is left out;
- the archived Z stores the net figures.

Safety suite **816/816** (813 before) under en-US, de-DE and tr-TR.

## Note

Z-Berichte archived before R141 keep the figures they were printed with (the
archive is immutable).
