# TOR POS R95 – Im Haus / Außer Haus MwSt.-Umschaltung (§12 UStG)

## Kontext
In Deutschland wird Essen, das vor Ort verzehrt wird ("Im Haus"), als
Bewirtungsleistung mit dem Regelsteuersatz (19 %) besteuert, während
dieselbe Speise zum Mitnehmen ("Außer Haus") dem ermäßigten Satz (7 %)
unterliegt. Getränke sind unabhängig vom Verzehrort bereits regulär mit
19 % belegt. TOR kannte bisher nur den festen, artikelgebundenen
Steuersatz aus dem Warengruppenstamm - für Imbiss-Betriebe fehlte die
Möglichkeit, diesen situativ (je Bon) umzuschalten. Auf ausdrücklichen
Wunsch umgesetzt.

## Änderung
- `TorPos.Core.ImHausVat.Effective(baseRate, imHaus)`: reine, pure
  Funktion - hebt einen 7 %-Satz auf 19 % an, wenn Im Haus aktiv ist;
  lässt jeden anderen Satz (19 %, 0 % bei reinem Pfand, ...) unangetastet.
- `CheckoutSnapshot.CopyLines(lines, imHaus)`: wendet die Umschaltung
  genau einmal an - beim Einfrieren des laufenden Warenkorbs zum
  Kassiervorgang. Alles Nachgelagerte (Bon-MwSt.-Aufschlüsselung,
  `BusinessManagementService`-Steuerberichte, `FiscalProcessData`s
  MwSt.-Klassenbildung für den TSE-Beleg) gruppiert bereits generisch
  nach `VatRate` - keine dieser Stellen musste geändert werden.
- **Der Bruttopreis ändert sich nie**: `CartLine.LineTotalCents` hängt
  ausschließlich von Menge × Verkaufspreis ab, nicht vom Steuersatz. Im
  Haus/Außer Haus verschiebt nur den internen Netto-/MwSt.-Anteil
  desselben Preises - exakt das steuerlich korrekte Verhalten bei
  gleichbleibendem Verkaufspreis.
- Neuer Umschalter "AUSSER HAUS" / "IM HAUS · 19%" in der Kopfzeile,
  sichtbar nur im IMBISS-Betrieb. Standard ist Außer Haus; wird nach
  jedem abgeschlossenen Bon automatisch zurückgesetzt, damit ein
  vergessener Umschalter nie in den nächsten Verkauf durchrutscht.
  Deaktiviert, solange der Warenkorb gesperrt ist oder die
  Verkaufsberechtigung fehlt - gleiche Regeln wie die übrigen
  Kassenaktionen.

## Ergebnis
- 8 neue Prüfungen in `R95ReviewTests.cs`: die reine Umschaltfunktion für
  7 %/19 %/0 %-Sätze, ein gemischter Warenkorb (Döner + Cola) mit und ohne
  Im Haus, Bestätigung dass der Gesamtpreis unverändert bleibt, und dass
  `CheckoutSnapshot.ImHaus` ohne explizite Angabe auf Außer Haus
  (Standard) steht. Sicherheits-Testsuite: **479/479 PASS**.
- `dotnet build -c Release` für `TorPos.App`: 0 Warnung(en), 0 Fehler.

## Einordnung
Dies ist eine echte steuerliche Berechnungsänderung, deshalb - anders als
die meisten Änderungen dieser Session - nur nach ausdrücklicher
Nutzerfreigabe umgesetzt. Betrifft nur den anzuwendenden MwSt.-Satz;
keine Berührung mit TSE-Signierung selbst, die weiterhin durch die beiden
Fiskal-Sperren (`FiscalRelease.Enabled=false`, Readiness-Flags in
`FiscalComplianceService`) inert bleibt. Vor echtem Produktivbetrieb
sollte diese Logik zusammen mit der übrigen MwSt.-Behandlung noch einmal
fachlich/steuerlich gegengeprüft werden (siehe `RECHTLICHE-ANFORDERUNGEN-DE.md`).
