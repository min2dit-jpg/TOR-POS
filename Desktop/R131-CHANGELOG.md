# R131 — DSFinV-K 2.4 Export

Until now the menu item *DSFinV-K Export* always answered "GESPERRT": the
service listed why no export existed and refused to write one. It now writes
the complete file set of DSFinV-K 2.4 for every Kassenabschluss (Z-Bericht) in
the chosen period. Basis: the official specification of the BZSt
(`dsfinv_k_v_2_4.zip`, document of 15.12.2023, VAT key appendix of 05.12.2024),
read in full; the TSE data format was corrected first (R130).

## What is written

A folder `DSFinV-K_<Kassen-Seriennummer>_<von>-<bis>_<Zeitpunkt>` containing

- the **20 CSV files** of the Einzelaufzeichnungs-, Stammdaten- and
  Kassenabschlussmodul;
- **`index.xml` and `gdpdu-01-09-2004.dtd` exactly as the BZSt publishes
  them** (embedded byte for byte, `-text` in `.gitattributes`, SHA-256 checked
  by the tests);
- `TOR-EXPORTPROTOKOLL.txt`: software version, till, closings with their
  number of Vorgänge, record count and SHA-256 per file, and every hint below.

The folder is written under a `.unvollstaendig` name and renamed when complete,
so an interrupted export never looks finished.

## Format

The table layout is not copied by hand: column order, text lengths and numeric
accuracy are read from the official `index.xml` at runtime. Every value is
checked against its column before it is written — text within its length, a
number where a number is declared, amounts with two decimals (DSFinV-K 4.1),
quantities with three. A value that does not fit is an error, never cut.
UTF-8 without BOM, `;` between fields, CR LF between records, text in double
quotes, `,` as decimal symbol, column names in the first line (`Range From 2`).
Byte-identical output on a German, English or Turkish Windows.

## How TOR's data is represented

| TOR | DSFinV-K |
|---|---|
| Kassenabschluss | one Z-Bericht in `z_report_archive`; its Vorgänge are selected exactly as the Z-Bericht counted them |
| Sale | `Beleg`, BON_ID = receipt number |
| Storno | `Beleg`, BON_STORNO = 1, amounts reversed, `Bon_Referenzen` → original receipt in its closing (4.2.2) |
| Retoure | `Beleg` with negative amounts, `Bon_Referenzen` → original (4.2.5) |
| VAT per receipt | the printed amounts (Rechnungsdoppel) from the same calculator the receipt uses; keys 1 (19 %) and 2 (7 %) of Anlage 2 |
| Pfand | own position, GV_TYP `Pfand`, at the article's rate (Anhang C: Warenumschließung) |
| Angebot | reduced price, `base_amount` and `discount` in `Bonpos_Preisfindung` (4.2.4) |
| Manual discount | position GV_TYP `Rabatt`, split onto the VAT rates as the receipt prorates it (4.2.4) |
| Payments | `Bar` / `Unbar` ("Karte"), a mixed payment as both |
| Einlage / Entnahme | `Beleg` GV_TYP `Einzahlung` / `Auszahlung`, key 5 (nicht steuerbar), BON_ID `KB-<id>` |
| IMBISS order handed to the TSE | `AVBestellung`, payment `Keine`, BON_ID `BE-<Park-Nr>`, same `Bonkopf_AbrKreis` as the receipt it was paid with (2.7.1) |
| TSE signature | `TSE_Transaktionen` with transaction number, log time in the Anhang E format, signature counter, signature and the Anhang I processData |
| TSE outage | listed with the documented reason from `tse_outage_log` in `TSE_TA_FEHLER` — not left out, not signed afterwards (R129) |
| Closing totals | `Z_GV_Typ`, `Z_Zahlart`, `Z_Waehrungen` summed from the Belege only (4.1) |

## What blocks the export

A validation runs the complete export in memory first. It refuses when:
company name, street, postcode or city is missing; neither Steuernummer nor
USt-IdNr. is set (§ 14 Abs. 4 Nr. 2 UStG); a Vorgang uses a VAT rate without a
DSFinV-K key (the receipt is named); a Storno or Retoure refers to a receipt in
no closing; there is no closing in the period; or any value does not fit its
column. Nothing is written then.

## What TOR does not record yet — stated, not hidden

Shown before the folder is chosen and written into the protocol:

- `BON_START` and `TSE_TA_START` are not stored and stay empty;
- TSE master data (algorithm, time format, public key, certificate) is not
  stored — `Stamm_TSE` has serial number and encoding only;
- master data is read from the current settings, not stored per closing;
- Im Haus / Außer Haus is not stored per sale (the VAT rates are);
- training is not recorded at all (no `AVTraining`) — open decision from R130;
- Einlage/Entnahme are not TSE-secured and only generically classified — open
  decision from R130;
- orders are exported with their last stored positions; a cancelled order has
  no secured counter-booking;
- the export of a build without fiscal release is a test data set;
- Vorgänge after the last closing belong to no Z-Bericht yet and follow with
  the next one (DSFinV-K 1.1.3).

## Z-Bericht fix found on the way

`Umsatz nach Storno/Retouren` was `Math.Max(0, Umsatz − Storno − Retouren)`.
A period with more cancelled or returned than sold — the Storno of yesterday's
large receipt, for instance — printed **0,00** and archived `gross_cents` 0,
while the VAT lines of the same Z-Bericht showed the correct negative amounts.
It is now the real, possibly negative value,
matching the DSFinV-K closing. Same for the monthly report, which uses the same
period summary. Found because the test compares every exported closing with
the printed Z-Bericht.

## UI

*DSFinV-K Export* (admin): blocking reasons as before; if the export is
possible, the hints are shown, then the target folder is chosen. The export is
recorded in the audit log (`DSFINVK_EXPORT`).

## Testing

`R131ReviewTests` (24 checks) builds a till with two closings — sales at 19 %
and 7 %, Pfand, an Angebot, a manual discount, a mixed payment, a TSE outage,
an Entnahme, a signed and paid order, a Storno and a Retoure of receipts from
the first closing, and a sale after the last closing — and checks:

- layout read from `index.xml`; `index.xml` and DTD byte-identical to the BZSt
  files;
- every file against `index.xml`: encoding, CR LF, header, field count, text
  length, numeric form, Z_ keys in every record;
- header = VAT split = positions = payments for every Vorgang;
- closing totals = its Belege, and hand-calculated values for both closings;
- each closing equals the printed Z-Bericht — including the second closing,
  where Storno and Retoure exceed the sales (fails against the old clamp);
- Storno/Retoure references into the first closing, Pfand, Preisfindung,
  Rabatt split, signed/outage/unsecured TSE records, the order and its
  Abrechnungskreis, master data with quote and semicolon intact;
- identical bytes under tr-TR;
- refusal without Steuernummer/USt-IdNr. (and nothing written), without a
  closing, and for a 16 % receipt.

Safety suite **713/713** (689 before) under en-US, de-DE and tr-TR.

## Still required before production

A real certified TSE and its master data, the open fiscal decisions listed
above, and a check of an export with the auditors' software (IDEA / AmadeusVerify)
or by a Steuerberater. This is a technical implementation of the published
specification, not a certification.
