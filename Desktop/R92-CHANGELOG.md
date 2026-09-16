# TOR POS R92 – Kassensturz-Sollbestand zählte Storno/Retoure als Bareinnahme

## Kontext
Vierter und letzter Fund der R88-Fehlerfamilie auf derselben Durchsicht:
`CashMovementRepository.GetExpectedCashCentsAsync` (`FiscalComplianceServices.cs`)
berechnet den erwarteten Bargeldbestand für den Kassensturz-Bildschirm -
den Wert, den der Kassierer beim tatsächlichen Auszählen der Kasse mit dem
System vergleicht. Die Abfrage summierte jede `sales`-Zeile mit
`payment_method='CASH'` für den heutigen Tag, ohne Rücksicht auf
`transaction_type`.

Anders als bei den Berichts-Bugs (R88/R90/R91), bei denen ein Ausschluss
richtig war, ist der korrekte Fix hier eine **Vorzeichenumkehr**: bei einer
BON STORNO oder Teilretoure wird echtes Bargeld an den Kunden
zurückgegeben - die Kasse hat also tatsächlich WENIGER Bargeld, nicht
mehr. Die alte Abfrage hat das Gegenteil gerechnet und den stornierten/
retournierten Betrag als zusätzliche Einnahme addiert. Ein Kassierer mit
einer bis auf den Cent korrekten Kasse hätte am Bildschirm einen
scheinbaren Fehlbetrag in exakter Höhe der Stornierung/Retoure gesehen -
und potenziell fälschlich verdächtigt worden, Geld zu vermissen.

## Änderung
Die Bargeld-Summierung berücksichtigt jetzt den Vorgangstyp: `SALE`-Zeilen
zählen positiv, `STORNO`/`RETURN`-Zeilen negativ, statt wie bisher
undifferenziert alle Zeilen zu addieren.

## Ergebnis
- 2 neue Prüfungen in `R92ReviewTests.cs`: ein Bar-Storno UND eine
  Bar-Teilretoure werden korrekt vom erwarteten Bestand abgezogen (nicht
  addiert), eine Kartenzahlung bleibt komplett unberücksichtigt; ein Tag
  ohne jede Buchung liefert exakt den Eröffnungsbestand zurück.
  Sicherheits-Testsuite: **464/464 PASS**.
- `dotnet build -c Release` für `TorPos.App`: 0 Warnung(en), 0 Fehler.

## Einordnung
Reine Rechenkorrektur in `CashMovementRepository`, nicht in
`FiscalComplianceService` (dem eigentlichen TSE-Freigabe-Gate trotz
gemeinsamer Datei) - keine Berührung mit Fiskalsignierung oder den beiden
Fiskal-Sperren, die unverändert `false` bleiben. Damit sind alle vier auf
dieser Durchsicht gefundenen Fälle des R88-Fehlermusters
(`BusinessManagementService.cs`, `BusinessManagementReports.cs`,
`FiscalComplianceServices.cs`) behoben.
