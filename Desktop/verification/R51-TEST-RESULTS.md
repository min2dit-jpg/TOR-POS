# TOR POS R51 – Verification

## Ergebnis in der Generierungsumgebung

- Cloud Syntaxcheck: **PASS** (`npm run check`)
- Cloud Tests: **16/16 PASS** (`npm test`)
- XML/XAML/Projektdateien: **10 strukturell gültig**
- JSON-Dateien (ohne node_modules): **10 gültig**
- C# Quellen: **61 strukturell ausgeglichen** (Kommentare/String-Literale beim Klammercheck ausgeschlossen)
- Login-Sprache: neuer `LoginWindow(auth, settings)`-Pfad statisch geprüft
- Stammdaten-Löschen: Interface + Repository für Gruppe/Warengruppe/Artikel vorhanden
- IMBISS Produktbilder: **57/57 PNG vorhanden und lesbar**, alle im R51-Starterkatalog referenziert
- IMBISS/KIOSK Scope-Reparatur statisch geprüft
- R51 Safety-Testfälle registriert
- SumUp: beide relevanten Quell-Dateien gegen R50 byte-identisch
- Fiskalstatus: `TEST_TSE_NOT_CONNECTED`, `production_allowed=false` unverändert

## Nicht in dieser Umgebung möglich

- .NET 10 SDK ist hier nicht installiert. Daher wurde der Windows/Avalonia Desktop **nicht kompiliert** und die neuen R51 C# Safety-Tests konnten hier nicht ausgeführt werden.
- Kein echter Windows-Bondrucker, Küchendrucker, Scanner, Kartenleser oder TSE-Hardwaretest.

## Windows-Abnahme R51

1. `Desktop\1-SETUP-ERSTELLEN.bat` ausführen.
2. Anmeldung öffnen und DE → TR → EN → DE wechseln; Auswahl muss sofort sichtbar und gespeichert sein.
3. IMBISS anmelden: `Schnellwahl`/`Snacks` dürfen nicht als KIOSK-Warengruppen erscheinen.
4. Prüfen: Speisen → Döner/Burger/Fingerfood/Pizza; Getränke → Getränke.
5. Mehrere Starterartikel öffnen: Bildvorschau in Artikelverwaltung und Bild auf Kassenkachel prüfen.
6. Eigenes Produktbild wählen, neu anmelden: eigenes Bild darf nicht vom Startertemplate überschrieben werden.
7. Admin: Test-Artikel → `ARTIKEL LÖSCHEN`; Test-Warengruppe → `WARENGRUPPE LÖSCHEN`; Test-Gruppe → `GRUPPE LÖSCHEN`.
8. R50 ORDER/Abholnummer-Workflow unverändert testen.
9. Bon/Küchenbon/Abholschein/Berichte müssen unabhängig von UI-Sprache Deutsch bleiben.
