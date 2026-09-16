# TOR POS R107 · Kassensicherheit & digitale Belege

Version **0.7.33.807** (Revision R107). Installation/Build: `1-SETUP-ERSTELLEN.bat`.

Dieses Dokument beschrieb ursprünglich nur R62 (Google OAuth QR + Gmail API);
dieser Abschnitt bleibt unten unverändert erhalten. Seither hinzugekommen,
u. a.: Mixed Payment (R101), Karten-Storno/Reversal über ZVT (R102), digitaler
QR-Beleg über einen lokalen Kassen-Webserver (R103), Kundendisplay (R104),
automatische Bildschirmanpassung (R105), sowie drei aus einer eigenen
Quellcode-Prüfung gefundene Korrekturen an Rückgabe/Storno-Berechnungen und
-Reihenfolge (R106, R107). Siehe die einzelnen `R*-CHANGELOG.md`-Dateien und
`ROADMAP.md` für den vollständigen, laufend gepflegten Stand.

## R62 · Google OAuth QR + Gmail API (Original-Abschnitt)

R62 baut auf R61 auf. ORDER-Direktverkauf, Bestellmonitor, tägliche Sicherung, Drucker-Preflight, PDF-Berichte und der R61 SMTP-Fallback bleiben erhalten.

## Neu in R62

Unter `Einstellungen > Berichte & E-Mail` gibt es **MIT GOOGLE ANMELDEN (QR)**. Der Betreiber scannt einen einmaligen QR-Code mit dem Handy und bestätigt die Berechtigung direkt bei Google. TOR POS fordert für den Mailversand nur `gmail.send` an; Google-Passwort oder App-Passwort werden für diesen Transport nicht benötigt.

Der Google Refresh Token wird nicht auf dem Kassen-PC gespeichert. TOR POS Cloud verwahrt ihn verschlüsselt und gibt der authentifizierten Kasse nur kurzlebige Access Tokens. Bericht-PDFs werden direkt vom Kassen-PC an die Gmail API gesendet und nicht über TOR POS Cloud übertragen.

## Voraussetzung

Die QR-Anmeldung benötigt einen öffentlich erreichbaren **TOR POS Cloud HTTPS-Server** mit Google OAuth Web Client. Siehe `../Cloud/R62-GOOGLE-OAUTH-SETUP.md`. Ohne diese Serverkonfiguration zeigt die Kasse eine verständliche Konfigurationsmeldung; der SMTP-Fallback bleibt verfügbar.

## Test

1. `7-SICHERHEITSTESTS.bat` – aktuelles Ziel: `ALL 554 CHECKS PASSED` (R62-Stand war 240; die Prüfungszahl wächst mit jeder Revision, siehe `tests/TorPos.SafetyTests/Program.cs`).
2. `1-SETUP-ERSTELLEN.bat` – echter Windows Build/Setup.
3. Cloud: `npm test` – R62 Generation: 20/20 bestanden.

In der Erzeugungsumgebung war kein .NET SDK vorhanden; deshalb ist der Windows-Build hier nicht als bestanden behauptet.
