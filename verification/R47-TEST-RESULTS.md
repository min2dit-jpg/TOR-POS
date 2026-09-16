# R47 Verification Results

Stand: 2026-09-08

## Automatische Prüfungen
- Cloud `npm run check`: **PASS** (`server.js`, `public/app.js`).
- Cloud `npm test`: **14/14 PASS**.
- Enthaltene Regressionen prüfen u. a. Tenant-Trennung, Event-Idempotenz, Rollback, Stock-Snapshot-Reihenfolge, R42 Stammdatenfelder, **R47 Mindestbestand + Einkaufspreis**, R46 Sale→Bestand, Berlin/DST, Update-API, TOTP-2FA und Browser-Origin-Schutz.
- Desktop XAML: **4/4 XML-Dateien strukturell gültig**.
- Desktop C#: **51 Quelldateien** mit Klammer-/String-/Kommentar-Struktur-Smokecheck ohne Fehler.
- MainWindow XAML Event-Wiring: **63 Click-Handler gefunden**; alle in den MainWindow-Partialklassen vorhanden.
- `manifest.json`: gültiges JSON.
- SumUp: `SumUpConnectionService.cs` und `SumUpConnectionWindow.cs` sind per SHA-256 **bitgleich zu R46**.

## Nicht ausgeführt
- Ein echter .NET-10/Avalonia-Windows-Build war in der Generierungsumgebung nicht möglich, weil kein .NET SDK installiert ist.
- Reale Scanner-, 58/80-mm-Bondrucker-, A4- und Touch-Abnahme muss auf dem Kassen-PC erfolgen.
- `tests/portal-dom.cjs` wurde nicht als Release-Gate verwendet, weil dessen optionale `jsdom`-Abhängigkeit nicht im Projektpaket enthalten ist. Die eingebauten Node-Regressionstests und `npm run check` sind erfolgreich.

## Empfohlene Windows-Abnahme
1. `Desktop\1-SETUP-ERSTELLEN.bat` ausführen.
2. `SCHNELLARTIKEL`: freie Bezeichnung/Preis/USt. in den Warenkorb; kein Stammdaten-/Bestandsartikel darf entstehen.
3. Artikel: Bestand, Mindestbestand und Einkaufspreis speichern; nur Preisänderung darf Bestand nicht überschreiben.
4. Hauptkasse: bei `Bestand <= Mindestbestand` muss `BESTAND · n NIEDRIG` erscheinen; Klick öffnet Inventur.
5. Inventur: Barcode → Menge → ENTER → Suchfeld wieder scannerbereit.
6. Warenbestand-Bericht: EK/VK und Warenwert auf Bon-Drucker und A4 prüfen.
7. Cloud: Portal zeigt EK, Mindestbestand, Low-Stock und Warenwert EK korrekt.
