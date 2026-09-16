# TOR POS R90 – Bedienerabrechnung zählte Storno/Retoure als Umsatz

## Kontext
Gefunden bei einer weiteren Durchsicht der Berichtsabfragen nach R88 (das
gleiche Muster: eine Abfrage, die einen neueren `transaction_type`-Wert
nicht berücksichtigt). Zwei zusammenhängende Probleme:

1. **`BuildOperatorSettlementAsync`** (Menüpunkt "BEDIENERABRECHNUNG",
   Tages-Kassensturz je Bediener) und der gleichnamige Abschnitt im
   Monatspaket summierten **jede** Zeile aus `sales` für den Zeitraum, ganz
   ohne Rücksicht auf `transaction_type`. Eine BON STORNO oder Teilretoure
   wurde dadurch als **zusätzlicher Umsatz** zum Bediener addiert, statt
   ausgeschlossen oder gegengerechnet zu werden - genau das Gegenteil von
   dem, was ein Bediener-Kassensturz braucht. Der bereits bestehende
   X-/Z-Bericht (`GetPeriodSummaryAsync`) macht es seit jeher richtig:
   Bar/Karte/Bons kommen dort ausschließlich aus `transaction_type='SALE'`.
2. **`RecordStornoAsync`/`RecordReturnAsync` schrieben nie eine
   `sale_operators`-Zeile** für die Storno-/Retoure-Buchung selbst - wer
   sie tatsächlich bearbeitet hat, stand nur im Freitext von
   `audit_log.details`, nicht auf Ebene der `sales`-Zeile. Selbst mit Fix 1
   wäre der bearbeitende Admin also nirgends auswertbar gewesen.

## Änderung
- `RecordStornoAsync` und `RecordReturnAsync` schreiben jetzt zusätzlich
  `INSERT INTO sale_operators(sale_id, operator_name)` für die neue
  Storno-/Retoure-Zeile, mit demselben `actor`-Wert, der auch in
  `audit_log` landet - exakt das gleiche Muster wie `CommitAsync`.
- `BuildOperatorSettlementAsync` (und der Monatspaket-Abschnitt) filtert
  die Umsatz-/Bar-/Karte-Summe jetzt auf `transaction_type='SALE'`,
  passend zum bestehenden X-/Z-Bericht. Ein zweiter Abschnitt
  "STORNO/RETOURE NACH BEARBEITER" zeigt Storno/Retoure jetzt getrennt und
  korrekt dem tatsächlich bearbeitenden Benutzer zugeordnet (dank Fix
  oben), statt sie stillschweigend zu verlieren.

## Ergebnis
- 5 neue Prüfungen in `R90ReviewTests.cs`: ein Bediener-Umsatz bleibt
  korrekt, auch wenn sein Verkauf später von jemand anderem storniert
  wird; eine Teilretoure gegen einen Kartenverkauf verändert dessen
  eigene Abrechnung nicht; die stornierten/retournierten Beträge tauchen
  nirgends addiert in einer Bediener-Zeile auf; der Bearbeiter der
  Storno/Retoure erscheint korrekt im neuen Abschnitt; ein Tag ohne
  Buchungen liefert ein sauberes Dokument. Da
  `FiscalRelease.RequireProduction()` `RecordStornoAsync`/`RecordReturnAsync`
  in jedem Testbuild grundsätzlich blockiert (gleiche Einschränkung wie
  bei den bestehenden R79/R82-Tests dokumentiert), lässt sich der
  `sale_operators`-Insert selbst nicht Ende-zu-Ende auslösen - er ist per
  Codeprüfung sowie über die vom Bericht erwartete Datenform abgesichert.
  Sicherheits-Testsuite: **459/459 PASS**.
- `dotnet build -c Release` für `TorPos.App`: 0 Warnung(en), 0 Fehler.

## Einordnung
Reine Berichts-/Zuordnungskorrektur, kein neuer Buchungsweg. Beide
Fiskal-Sperren unverändert `false`. STORNO/RETURN-Zeilen erscheinen in
beiden betroffenen Berichten ohnehin erst, sobald echte Buchungen
existieren - das bleibt bis zur Produktivfreigabe gesperrt.
