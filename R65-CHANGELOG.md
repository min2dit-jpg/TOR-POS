# TOR POS R65 – Checkout Freeze Fix

## Problem
Auf Test-PCs konnte BAR/KARTE beim Prüfen eines nicht angeschlossenen oder
langsam antwortenden Windows-Druckers 10–15 Sekunden wie eingefroren wirken.

## Ursache
`PrinterSettings.InstalledPrinters` wurde im Checkout-Pfad synchron aus dem
Avalonia-UI-Thread aufgerufen. Windows-Spooler und alte/offline Netzwerkdrucker
können diese Abfrage stark verzögern. Zusätzlich wartete der Simulationsverkauf
auf den optionalen Testdruck.

## Änderungen
- Keine synchrone `InstalledPrinters`-Abfrage mehr im BAR/KARTE Checkout-Pfad.
- Drucker-Probe wird asynchron und maximal 2 Sekunden abgewartet.
- Bei Timeout startet die Zahlung NICHT; verständlicher Dialog erscheint.
- Konfigurierter Drucker wird direkt geprüft, ohne zuerst alle Windows-Drucker
  aufzulisten.
- Nach abgeschlossenem Testverkauf läuft der optionale Testbon im Hintergrund.
- Status `ZAHLUNG WIRD VORBEREITET` / `BONDRUCKER WIRD GEPRÜFT` wird vor
  Geräte-I/O sichtbar gemacht.
- 2 neue Safety Checks für bounded device waits.
- Ziel unter Windows: `ALL 246 CHECKS PASSED`.

## Sicherheitsregel
Ein Drucker-Timeout darf niemals automatisch als "Drucker vorhanden" gelten.
Die Zahlung wird vor dem Start gestoppt, außer der Bediener wählt bewusst
`OHNE DRUCKER FORTFAHREN`.

## Windows-Abnahme
1. Bondrucker AUS / USB abgezogen -> BAR drücken.
2. Innerhalb von ca. 2 Sekunden muss Warnung erscheinen; UI darf nicht 10–15 s hängen.
3. `ABBRECHEN` -> Warenkorb bleibt unverändert.
4. `OHNE DRUCKER FORTFAHREN` -> Testbetrieb darf ohne Druck fortgesetzt werden.
5. Drucker EIN -> BAR erneut; Kassenfenster muss normal weiterlaufen.
6. `7-SICHERHEITSTESTS.bat` -> ALL 246 CHECKS PASSED.
7. `1-SETUP-ERSTELLEN.bat` -> echter Windows Build.
