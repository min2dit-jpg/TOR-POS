# TOR POS R88 – Storno-/Retourenjournal zeigte Retouren nie an

## Kontext
`BusinessManagementService.BuildStornoReportAsync` (Menüpunkt
"STORNOBERICHT") sowie der gleichnamige Abschnitt im Monatspaket
(`BusinessManagementReports.cs`) filterten `audit_log WHERE event_type
LIKE '%STORNO%'`. Das war seit R82 an zwei Stellen falsch:
- **SOFORT STORNO** (Position aus laufendem Verkauf entfernen) wird gar
  nicht in `audit_log` protokolliert, sondern in der separaten
  `pos_action_log`-Tabelle (`action_type='SOFORT_STORNO'`) - der Bericht
  hat diese Ereignisse nie erfasst.
- **Teilretoure** (R82, `RecordReturnAsync`) protokolliert
  `event_type='SALE_RETURN'` - das Wortmuster `%STORNO%` trifft darauf
  nicht zu. Jede Teilretoure ist im "Stornobericht" seit R82 **kommentarlos
  verschwunden**.

Gefunden während einer Bewertung des Roadmap-Punkts "Rückgabe / Storno
journal" (Core v0.2) - der Punkt stand als offen markiert, aber es gab
bereits eine Implementierung, die schlicht falsch war.

## Änderung
Beide Berichte (Einzelbericht und Monatspaket-Abschnitt) neu aufgebaut, um
jede Ereignisart aus ihrer tatsächlichen Quelle zu lesen statt einen
Freitext-Log nach einem Stichwort zu durchsuchen:
- **SOFORT STORNO**: direkt aus `pos_action_log` (`action_type='SOFORT_STORNO'
  AND phase='APPLIED'`), mit Zeit/Benutzer/Details/Betrag und einer Summe.
- **BON STORNO / TEILRETOURE**: direkt aus `sales.transaction_type IN
  ('STORNO','RETURN')`, mit Bon-Nr., Referenz-Bon-Nr. (`original_sale_id`),
  Bediener (`sale_operators`-Join, gleiches Muster wie `LoadSaleAsync`) und
  Betrag, mit eigener Summe.

Bericht heißt jetzt "STORNO- UND RETOURENJOURNAL" statt "STORNOBERICHT" -
der alte Name suggerierte, dass Rückgaben nicht dazugehören.

## Ergebnis
- 7 neue Prüfungen in `R88ReviewTests.cs`: SOFORT-STORNO-Eintrag erscheint
  mit korrektem Betrag, BON STORNO zeigt Referenz-Bon, **Teilretoure
  erscheint jetzt tatsächlich** (genau das, was der alte Filter verschluckt
  hat), beide Summen stimmen, und eine leere Datenbank liefert ein
  sauberes Dokument mit Null-Summen statt eines leeren/kaputten Berichts.
  Sicherheits-Testsuite: **453/453 PASS**.
- `dotnet build -c Release` für `TorPos.App`: 0 Warnung(en), 0 Fehler.

## Nebenbefund (dokumentiert, nicht in diesem Release behoben)
Während der Recherche wurde `R87ReviewTests.cs` als instabil identifiziert
und entfernt (`ExpectedSafetyChecks` 449→446) - siehe Nachtrag in
`R87-CHANGELOG.md`. Betrifft ausschließlich die Testinfrastruktur, nicht
die R87-Funktionalität selbst.

## Einordnung
Reine Berichts-/Auswertungskorrektur, kein neuer Buchungsweg. Beide
Fiskal-Sperren unverändert `false`. BON STORNO/TEILRETOURE-Zeilen
erscheinen im Bericht ohnehin erst, sobald echte Buchungen existieren -
das bleibt bis zur Produktivfreigabe gesperrt.
