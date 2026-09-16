# TOR POS R80 – Review-Fixes für R77-R79

## Kontext
Auf ausdrücklichen Wunsch: eine dedizierte, zweite Durchsicht des in
R77 (Küchenrouting), R78 (TSE-Sale-Signierung) und R79 (BON STORNO)
neu geschriebenen Codes, bevor weitere Features aufgesetzt werden.
Vier reale Probleme gefunden und behoben.

## 1. FiscalProcessData unterschied STORNO nicht von einem normalen Verkauf
`FiscalProcessData.BuildKassenbeleg` erzeugte für einen Storno-Bon exakt
dasselbe `"Beleg^..."`-Format wie für einen normalen Verkauf - mit den
positiven, gespiegelten Artikelmengen sähe ein signierter Storno-Bon fiskal
wie ein zweiter, identischer Verkauf aus, nicht wie dessen Rückbuchung.

**Fix:** `Sale` trägt jetzt zusätzlich `OriginalReceiptNumber` (per
Self-Join in `LoadSaleAsync` befüllt). `BuildKassenbeleg` markiert einen
Storno jetzt als `AVBelegstorno` (statt `Beleg`) und hängt
`Referenz-Beleg-Nr:{original}` an.

## 2. Storno-Rennbedingung (Race Condition) bei gleichzeitigem Zugriff
`RecordStornoAsync`s eigene "schon storniert?"-Prüfung (`SELECT COUNT(*)`
vor dem `INSERT`, gleiche Transaktion) ist für sich genommen nicht
race-sicher gegen zwei gleichzeitige BON-STORNO-Versuche auf denselben
Bon von unterschiedlichen Kassen/Nutzern.

**Fix:** neuer partieller Unique-Index
`ux_sales_one_storno_per_original` (Schema V9) macht einen Doppel-Storno
auf Datenbankebene unmöglich, unabhängig vom Timing - dasselbe Muster wie
das bereits bestehende `ux_checkout_one_open`. Ein Verstoß wird im Code
in dieselbe freundliche Fehlermeldung übersetzt wie der normale Fall.

## 3. Indexverschiebungs-Fehler beim Einbau von Fix 1 selbst gefunden
Beim Hinzufügen der neuen `OriginalReceiptNumber`-Spalte in
`LoadSaleAsync`s SELECT verschoben sich alle nachfolgenden Spaltenindizes
um eins - `TseOutage` las dabei zunächst versehentlich denselben Index wie
`TseLogTime`. Vor dem Testlauf bemerkt und korrigiert; die bestehende
R78-Testsuite hätte diesen Fehler ohnehin sofort aufgedeckt.

## Tests
`R80ReviewTests.cs` - 6 neue Prüfungen: Schema V9, ProcessData-Marker für
normalen Verkauf vs. Storno, `OriginalReceiptNumber`-Rundlauf über die
Datenbank, und der datenbankseitige Doppel-Storno-Schutz. Safety-Ziel
jetzt `ALL 406 CHECKS PASSED` (400 + 6). Schema jetzt
`SchemaMigrationService.TargetSchemaVersion = 9`.

## Unverändert
Alle bestehenden Sicherheitsschranken (`FiscalRelease.Enabled=false`,
`FiscalComplianceService`-Konstanten) bleiben unangetastet - diese Fixes
verbessern nur die Korrektheit des bereits gesperrten Codes.
