# R130 — TSE processData Follows DSFinV-K Anhang I

First step of the DSFinV-K 2.4 export. Reading the official specification
(BZSt, `dsfinv_k_v_2_4.zip`, document of 15.12.2023) against the code showed
that the data TOR hands to the TSE was not the format the specification
prescribes. The export has to repeat exactly that data (`TSE_TA_VORGANGSART`,
`TSE_VORGANGSDATEN`), so this had to be right first.

## What was wrong

Anhang I defines what goes into the TSE:

- **Kassenbeleg-V1:** `<Vorgangstyp>^<Brutto-Steuerumsätze>^<Zahlungen>` —
  five gross amounts in a fixed order (19 %, 7 %, two § 24 UStG averages, 0 %),
  then the payments as `Betrag:Bar` / `Betrag:Unbar`.
- **Bestellung-V1:** one line per position, `Menge;"Bezeichnung";Preis`,
  lines separated by CR.
- **StartTransaction** carries neither processType nor processData.

TOR instead produced a format of its own:

```
Beleg^2026-01-15T12:30:00.000+01:00^Betrag-Summe:19.99_Bar:19.99^UStNormal:16.99_UStErmaessigt:3.00^Beleg-Nr:1
Bestellung^2026-03-01T11:00:00.000+01:00^Betrag-Summe:12.50^UStNormal:12.50^Park-Nr:777
```

and passed the full data at Start as well. A **Storno was signed as
`AVBelegstorno`**, which Anhang B and I exclude for any till secured by a TSE:
the receipt is already signed before it could be flagged, so a Storno must be a
second `Beleg` with reversed signs. R121 had explicitly defended the
`AVBelegstorno` choice.

The class carried a "draft, not yet legally validated" warning and the fiscal
circuit breakers keep it from production, so nothing was ever signed this way
for real. It would, however, have been the first thing a real TSE and the
export received.

## The fix

`FiscalProcessData` now builds exactly the Anhang I strings:

```
Beleg^16.99_3.00_0.00_0.00_0.00^19.99:Bar
Beleg^-10.00_0.00_0.00_0.00_0.00^-4.00:Bar_-6.00:Unbar     (Storno of a mixed payment)
1;"Döner · Groß";8.50
```

- Storno and Retoure: `Beleg` with negative amounts. TOR keeps storing them
  with positive amounts plus `transaction_type`; the sign is applied when the
  data is built. The link to the original receipt is not part of processData
  (Anhang I has no such field) — DSFinV-K carries it in `Bon_Referenzen`,
  built later from `original_sale_id`.
- Gross per rate from the shared, discount-prorating `VatSummaryCalculator`
  (R108), so the containers add up to the payments.
- Payments from `EffectiveCash/CardPortionCents` (R101/R102), cash first, a
  payment of 0.00 left out.
- A rate other than 19 %, 7 % or 0 % throws `UnsupportedVatRateException`:
  without knowing its legal basis it cannot be put into a container. The
  signing service checks this **before** opening a TSE transaction and records
  a documented TSE outage naming the rate, so the receipt carries the outage
  note instead of a transaction with invalid data. TOR's editor only offers
  7 % and 19 %.
- Amounts and quantities are culture-independent (`.`, no thousands
  separator); quantities with at most three decimals, cut off, not rounded.
- `LineText` names a position the way the printed and digital receipts do
  (`Name · Variante`), for reuse by the export.
- `SaleFiscalSigningService` and `OrderFiscalSigningService` start the
  transaction with empty processType/processData.

## Old tests that pinned the invented format

Seven tests asserted the old strings or the `AVBelegstorno` marker as correct:
R78, R80, R82, R83, R101, R108, R121. Each was rewritten to the official form
while keeping what it was protecting (discount reflected in the tax amounts,
both parts of a mixed payment, a Storno not looking like a duplicate sale, a
Retoure not signed as an abort). This is the sixth time in this project that
an old test held a defect in place.

## Testing

`R130ReviewTests` (18 checks):

- the worked examples printed in Anhang I itself, reproduced byte for byte
  (100 € at 19 % cash; 50 € + 50 € card; the QR-code example
  `Beleg^4.05_3.00_0.00_0.00_0.00^7.05:Bar`; the Eisbecher/Eiskaffee order with
  doubled quotes and CR line breaks);
- 0 % in the fifth container, zero payments omitted, Storno of a mixed payment
  fully reversed, unsupported rate refused, quantity formatting, variant naming;
- identical bytes under tr-TR and de-DE;
- through the real signing services with a recording TSE: Start empty, Finish
  with `Kassenbeleg-V1` / `Bestellung-V1` and the Anhang I data; an unsupported
  rate never opens a transaction and is documented as an outage.

Safety suite **689/689** (671 before) under en-US, de-DE and tr-TR.

## Found on the way — decisions still needed

Reading the specification against the code also showed gaps that change what
is recorded or signed. They are listed in `ROADMAP.md` and not changed here:

- **Training mode records nothing.** Training sales never reach the database or
  the TSE. DSFinV-K 4.2.6 and Anhang B require training to be recorded and
  secured as `AVTraining` (without effect on the closing).
- **Cash movements** (Einlage/Entnahme) are not TSE-secured and are not
  classified into the DSFinV-K business-case types (Geldtransit,
  Privatentnahme, Privateinlage, Einzahlung, Auszahlung, …).
- **Start of the TSE transaction.** TOR starts and finishes it together after
  payment. § 2 KassenSichV asks for the transaction to be started
  "unmittelbar" at the beginning of the Vorgang (first position).
- **Master data changes** (company, VAT, TSE) must be preceded by an automatic
  closing (DSFinV-K 3.2).
