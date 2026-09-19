# TOR POS R58 · UI / Printer Polish

Version: **0.7.33.580**  
Revision: **R58-UI-Printer-Polish**

## Artikel / Touch UI
- Sparse Produktseiten verwenden nur noch die tatsächlich nötigen sichtbaren Zeilen, mindestens drei (sofern konfiguriert), statt viele leere umrandete Zellen zu zeichnen.
- Seitengröße und Paging bleiben unverändert; es werden keine Artikel abgeschnitten.
- Einheitliche `producttile`-Darstellung für Größe, Rand, Radius, Padding und Touch-Zustände.
- Ohne echtes Bild: Initiale + Artikelname + Preis als eine zusammenhängende Kachel.
- Mit Bild: Bild, Name und Preis bleiben innerhalb derselben einheitlichen Kachel.
- Offene Bestellungen erhalten bei `count > 0` eine sichtbare gelbe Hervorhebung.

## Drucker
- Testdruck führt vor dem Senden eine Printer-Probe aus.
- Windows-Spoolerstatus wird per `GetPrinter(Level 6)` gelesen, sofern der Treiber ihn bereitstellt.
- OFFLINE / nicht verfügbar / Papier leer / Papierstau / pausiert / Abdeckung offen / Benutzereingriff werden verständlich gemeldet.
- Ist der gespeicherte Drucker nicht mehr in Windows installiert, wird kein 15-Sekunden-Timeout abgewartet.
- Nach einem Timeout bleibt die bestehende Anti-Doppel-Druck-Sperre erhalten; kein blinder Wiederholungsdruck.
- Fehlerdialog bietet `DRUCKEREINSTELLUNGEN` und bei unklarem Status `DRUCKWARTESCHLANGE PRÜFEN`.

## Prüfung
- 6 neue R58 Review-Checks; Ziel nach erfolgreichem Windows-Testlauf: **228 Checks**.
- In der Chat-Ausführungsumgebung ist kein `dotnet`/MSBuild vorhanden. Deshalb keine Behauptung eines erfolgreichen R58-Windows-Builds.
