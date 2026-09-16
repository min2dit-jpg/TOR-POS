# R49 Verification Summary

Basis: Benutzerpaket `R48.1-StabilityReview`.

## Geprüft in dieser Erstellungsumgebung
- Cloud `npm run check`: PASS.
- Cloud Tests: 16/16 PASS.
- C# Struktur-/Delimiter-Scan: PASS (58 Dateien, 0 Fehler).
- Avalonia AXAML/XML Parse: PASS (4 Dateien, 0 Fehler).
- JSON Parse: PASS.
- R49 Release-/Manifestkennung: PASS.
- Menü/Combo Schema + Persistenz-Testfall vorhanden.
- ORDER-Abholnummer: Annahme vergibt Nummer; Änderung erhält dieselbe Nummer.
- Abholnummer-Modi: OFF / SALE / ORDER.
- Separater Küchendrucker + persistente Küchen-/Abholschein-Druckjobs vorhanden.
- Bedienoberfläche: DE / TR / EN; Druckmodelle verwenden den UI-Übersetzer nicht.
- Bon, Küchenbon, Abholschein und fiskale Drucktexte bleiben Deutsch.
- SumUp-Verbindungsdateien gegenüber R48.1 SHA-256 identisch.

## Noch zwingend auf Windows prüfen
In dieser Umgebung ist kein .NET 10 SDK / Windows-Avalonia-Runtime vorhanden. Daher wurde hier **kein echter Desktop-Compile** und kein physischer Druckertest ausgeführt.

Auf dem TOR Build-PC:
1. `Desktop\1-SETUP-ERSTELLEN.bat`
2. IMBISS / ORDER: Bestellung annehmen -> Abholnummer -> offene Bestellung -> aufrufen -> BAR/KARTE-Testbon.
3. Küchendrucker: Testdruck + automatische Bestellung.
4. Bon-Drucker: Abholschein und finaler Bon; Texte müssen Deutsch bleiben.
5. Programmsprache TR/EN: Hauptkasse, Login, Einstellungen und Artikelmaske prüfen.
6. Menü/Combo: Bestandteile speichern und Bestand/Cloud-Abzug prüfen.

`production_allowed=false` und `TEST_ONLY` bleiben unverändert.
