# R135 — Training Sales Recorded and Secured as AVTraining

Third open fiscal decision from R130, decided by what the rules say.

## The rules

- **AEAO zu § 146a Nr. 1.11.1:** *"Hierunter fallen beispielsweise
  Trainingsbuchungen, Sofort-Stornierung ..., Belegabbrüche ..."* — "andere
  Vorgänge" that are needed to document complete recording and therefore have
  to be secured (Nr. 1.11.2 exempts only what the protection goals do not need,
  e.g. screen brightness).
- **DSFinV-K 4.2.6:** training operators were used in audits *"um
  steuerpflichtige Bareinnahmen nicht zu erfassen. Aus diesem Grund sind diese
  Buchungen auch zu protokollieren und abzusichern gemäß KassenSichV ... mit dem
  BON_TYP ,AVTraining' aufzunehmen"*; they cause no cash or VAT booking.
- **Anhang B:** AVTraining has no effect on the Kassenabschluss; payment types
  may be recorded for training purposes (the only AV type allowed to).

## What was wrong

A training sale left nothing but an audit line (`TEST_SALE_COMPLETED`). It was
neither recorded nor signed nor exported.

## The fix

- **When.** Exactly on a till that books for real
  (`SaleModePolicy.RecordsTrainingFiscally`: training user, active licence,
  fiscal release, production allowed). A till that is not released records
  nothing fiscal at all, training included — as for sales (R113) and cash
  movements (R134). `TrainingReceiptRepository.RecordAsync` refuses while the
  fiscal circuit breaker is off.
- **Where.** Own append-only tables (migration 15): `training_receipts`,
  `training_receipt_items`, `training_tse_signatures`. Never `sales`: no
  report, stock booking, Kassensturz or Z total can count a training sale.
- **Signed.** `TrainingFiscalSigningService`: one Kassenbeleg-V1 transaction,
  start without data, finish with `AVTraining^<tax containers>^<payments>`.
  One final TSE record per training sale; an inactive TSE or a failing call is a
  documented outage.
- **Closing.** A training sale is a Vorgang of the period: the R132 master data
  guard counts it, and the export writes it into the closing — `BON_TYP
  AVTraining`, positions, training payments, TSE record — without adding it to
  `Z_GV_Typ`, `Z_Zahlart` or the cash totals.
- **Receipt.** The training receipt stays the marked simulation receipt it was.
- The signing flow shared by cash movements and training is one helper
  (`TseKassenbelegSigner`).

## Testing

`R135ReviewTests` (12 checks): migration; policy; processData
`AVTraining^10.00_0.00_0.00_0.00_0.00^4.00:Bar_6.00:Unbar`; refusal with the
breaker off, nothing stored; signed with empty start and Kassenbeleg-V1 finish;
records final; inactive TSE gives an outage; master data guard counts training;
Z-Bericht counts only the real sale; export lists both trainings with payments,
closing totals contain only the real sale, TSE data or outage reason per
training.

Safety suite **761/761** (749 before) under en-US, de-DE and tr-TR.

## Still open

Training in IMBISS order mode (parked orders of a training user) stays a
simulation; only checkout is recorded. The start of the TSE transaction at the
beginning of the Vorgang follows in the next step.
