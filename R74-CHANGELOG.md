# TOR POS R74 – Database / WAL Stress & Health

## Ausgangslage
R73 wurde auf Windows erfolgreich bestätigt:
`ALL 321 CHECKS PASSED`

R74 ändert keine fachliche Kundendatenstruktur.
Schema bleibt V5.

## Diagnose: SQLITE / WAL
SYSTEMSTATUS / DIAGNOSE enthält eine neue Zeile:
`SQLITE / WAL`

Gemessen werden:
- journal_mode
- synchronous
- busy_timeout
- foreign_keys
- wal_autocheckpoint
- page_count
- page_size
- freelist_count / Prozent
- echte DB-Dateigröße
- WAL-Dateigröße
- SHM-Dateigröße
- PASSIVE Checkpoint: busy / log / checkpointed frames
- PRAGMA quick_check

Soll:
- WAL
- synchronous FULL
- busy_timeout mindestens 3000 ms
- foreign_keys ON
- quick_check ok

Diagnose führt absichtlich KEIN:
- VACUUM
- wal_checkpoint(TRUNCATE)

auf der Produktionsdatenbank aus.

PASSIVE ist nicht-destruktiv und wartet nicht aggressiv auf Reader.

Eine WAL-Datei >= 256 MB wird nur als Hinweis `WAL GROSS` angezeigt.
Sie wird nicht automatisch abgeschnitten.

## Safety
Neue R74 Safety Checks:
12 assert + 0 reject = 12

Neues Ziel:
`ALL 333 CHECKS PASSED`

Geprüft:
- WAL aktiv
- synchronous FULL
- busy_timeout / foreign keys
- Page-/Freelist-Metriken
- DB/WAL/SHM-Größen
- PASSIVE Checkpoint-Werte
- quick_check
- Health-Probe verändert keine Daten
- zwei konkurrierende Writer:
  Writer B wartet bei kurzem Write-Lock und schreibt danach innerhalb
  des 3000-ms-busy_timeout erfolgreich
- beide Commits bleiben vorhanden
- DB bleibt danach gesund

## Separater echter Windows-Lasttest
Neu:
`8-DATENBANK-WAL-LASTTEST.bat`

WICHTIG:
Der Lasttest öffnet NIE die echte TOR-POS-Produktionsdatenbank.
Er erzeugt ausschließlich eine neue Wegwerf-Datenbank unter `%TEMP%`.

Default:
- 10.000 Artikel
- 500.000 Verkäufe
- 500.000 SaleItems
- 5.000 Sales pro Commit-Batch
- 1.000 Barcode-Lookups
- 500k Sales-Aggregation
- 750-ms Writer-Contention-Test
- PASSIVE WAL Health
- PRAGMA quick_check
- SQLite Backup Benchmark

Ergebnisdatei:
`Desktop\verification\R74\DB-STRESS-WINDOWS-....txt`

Geschwindigkeit wird gemessen und berichtet.
Es gibt bewusst keine willkürliche Aussage:
"unter X ms = gut, darüber = kaputt".

PASS/FAIL gilt für:
- Datenanzahl
- DB-Integrität
- WAL/FULL/Foreign-Key-Pragmas
- Busy-Timeout-Verhalten
- Backup-Erstellung

## Betriebsprinzip
KIOSK und IMBISS benutzen weiterhin denselben SQLite-/WAL-/Backup-Kern.
R74 ist gemeinsame Infrastruktur und verändert keine edition-spezifischen
KIOSK- oder IMBISS-Funktionen.

## Produktionsstatus
Unverändert:
`production_allowed=false`
