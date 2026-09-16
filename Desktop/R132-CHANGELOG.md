# R132 — Master Data per Closing, Automatic Closing Before a Change

First of the open fiscal decisions from R130/R131, decided by the rule itself.

## The rule

DSFinV-K 2.4, section 3.2: *"Zur Vermeidung von Redundanzen werden die
Stammdaten für jeden Kassenabschluss nur einmal gespeichert. Werden Änderungen
an den im Folgenden aufgeführten Stammdaten vorgenommen, ist zuvor automatisch
ein Abschluss zu erstellen."* Section 3 adds that it must be ensured *"dass vor
einer Stammdaten-Änderung ein Kassenabschluss erfolgt und erst anschließend
wieder neu gebucht wird"*. The listed Stammdaten include company name, address,
Steuernummer and USt-IdNr. (Stamm_Abschluss, Stamm_Orte) and the till's
software version (Stamm_Kassen, KASSE_SW_VERSION).

## What was wrong

A Z-Bericht stored no master data. The R131 export read the company data as
they were on the day of the export, so a closing made before a move was
exported with the new address — and nothing prevented the company data from
changing in the middle of a period, or TOR from being updated with Vorgänge
waiting for a closing.

## The fix

- **Snapshot per closing.** `z_report_archive.master_data` (migration 12)
  holds the master data every Z-Bericht was recorded under, as JSON: company
  name, street, postcode, city, country, Steuernummer, USt-IdNr., till brand,
  model and eAS serial, software brand and version.
- **Automatic closing before a change.** The settings screen saves through
  `DsfinvkMasterDataService`. When company master data change while a Vorgang
  is waiting for a closing, the Z-Bericht is created first — under the old
  data — then the new data are saved; the status line says which Z was
  created, the audit log records `Z_REPORT_AUTO_MASTER_DATA`. If a parked
  receipt blocks the Z-Bericht (the existing rule), nothing is closed and
  nothing is saved.
- **Guard for every other path.** `SettingsRepository.SaveManyAsync` refuses a
  changed company value while Vorgänge are waiting
  (`MasterDataChangeRequiresClosingException`). Saving the same value again,
  or any other setting, is not affected.
- **Software updates.** The version under which the open period is recorded is
  kept in `dsfinvk.software_version` and moves to the running version only at
  a closing. At start-up, if TOR was updated while Vorgänge were waiting, they
  are closed automatically under the version that recorded them
  (`Z_REPORT_AUTO_SOFTWARE_UPDATE`). This closing cannot be refused — the
  update has already happened.
- **Export.** Each closing is written with its own snapshot. Closings from
  before R132 have none; for them the current settings are used and the hint
  names them.

"Waiting for a closing" means a fiscal Vorgang after the last Tagesabschluss:
a sale, Storno or Retoure, an order handed to the TSE, or a cash movement that
is not a test entry. In the test build none of these are recorded as fiscal
(a Z-Bericht is not possible there), so changing company data stays possible on
a till in test mode.

## Other changes

- `R103ReviewTests` set the company name after inserting a sale; the order is
  now the realistic one (company data first).
- `R131ReviewTests` checks the missing-tax-id refusal in its own till, because
  the tax id can no longer be removed in the middle of a period.

## Testing

`R132ReviewTests` (11 checks): migration; direct change refused while a sale
waits, unrelated settings and unchanged values allowed; refused and nothing
saved while a parked receipt blocks the Z; automatic closing under the old data,
new data saved, audit entry; no extra closing when nothing waits; closing after
an update under the old version, exactly once; export writes each closing with
its own company data and software version; a pre-R132 closing is named in the
hint; a test-mode cash movement does not block, a fiscal one does.

Safety suite **724/724** (713 before) under en-US, de-DE and tr-TR.

Source: DSFinV-K 2.4 (BZSt),
https://www.bzst.de/DE/Unternehmen/Aussenpruefungen/DigitaleSchnittstelleFinV/digitaleschnittstellefinv_node.html
