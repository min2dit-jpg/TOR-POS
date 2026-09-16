# R134 — Einlage / Entnahme: Business Case and TSE Signature

Second open fiscal decision from R130, decided by what the rules say.

## The rules

- **AEAO zu § 146a Nr. 1.10.2** lists as Geschäftsvorfälle, among others:
  *Privatentnahme, Privateinlage, Wechselgeld-Einlage, Lohnzahlung aus der Kasse,
  Geldtransit*.
- **Nr. 1.8 / 2.2.2:** a Vorgang has to be secured by the TSE — start of the
  protocol "unmittelbar mit Beginn", end at its completion.
- **Nr. 2.5.5:** no receipt has to be issued for them — *"Von der
  Belegausgabepflicht sind z. B. Entnahmen und Einlagen ausgenommen."*
- **DSFinV-K Anhang C** gives the GV_TYP values (Geldtransit, Privateinlage,
  Privatentnahme, Lohnzahlung, Einzahlung, Auszahlung); **Anhang I** gives the
  processData, e.g. *"Privateinlage 100 bar"* →
  `Beleg^0.00_0.00_0.00_0.00_100.00^100.00:Bar`.

## What was wrong

A movement was only `EINLAGE` or `ENTNAHME` with a reason text, was always
stored as a test entry (`fiscal_mode TEST_ONLY` hard-coded) and was never
signed. The R131 export could only call them a generic Einzahlung/Auszahlung
and said they were not secured.

## The fix

- **Type is required.** The *Kassenbewegung* dialog asks what the movement is:
  - Einlage: *Geldtransit (Wechselgeld / aus Bank oder Tresor)*, *Privateinlage*,
    *Sonstige Einzahlung*;
  - Entnahme: *Geldtransit (zur Bank oder in den Tresor)*, *Privatentnahme*,
    *Lohnzahlung aus der Kasse*, *Sonstige Auszahlung (ohne USt)*.
  The repository refuses a movement without a type or with a type of the other
  direction. A Kassensturz count is not a cash flow and has no type.
- **Real bookings are fiscal.** In a released build a movement is stored as
  `PRODUCTION` and signed right away by `CashMovementFiscalSigningService`:
  one Kassenbeleg-V1 transaction, start without data, finish with the Anhang I
  processData (0 % container, Bar, negative for an Entnahme). A production
  movement is refused while the fiscal circuit breaker is off — the same breaker
  as every real sale. In the test build nothing changes except that the type is
  recorded.
- **One final TSE record.** `cash_movement_tse_signatures` (migration 14): the
  signature or the outage with its reason, UPDATE/DELETE blocked, a second
  result refused — the same rule as for sales (R122/R129). A TSE that is not
  active is reported as an outage.
- **No receipt** is printed, as Nr. 2.5.5 allows; the status line shows the
  type and whether it was signed.
- **Export.** GV_TYP is the chosen type, the receipt name its label,
  `TSE_Transaktionen` carries the signature and processData or the stored
  outage reason. Test entries are left out (as in R132's closing guard).
  Hints only for movements from before R134 (no type / no TSE result).

## Testing

`R134ReviewTests` (13 checks): migration; allowed types per direction;
processData equals the Anhang I examples; refusal without type, with the wrong
direction and as production with the breaker off, nothing stored; test entry
keeps its type; inactive TSE → documented outage, no transaction; Geldtransit
signed with empty start and Kassenbeleg-V1 finish; second TSE record refused;
TSE failure stored with its reason; export: test entry left out, GV_TYP and
name, TSE records, no hints.

`R131ReviewTests` now writes its cash movement as a fiscal movement from before
R134 (the repository no longer creates production entries with the breaker
off); `R132ReviewTests` passes a type.

Safety suite **749/749** (736 before) under en-US, de-DE and tr-TR.
