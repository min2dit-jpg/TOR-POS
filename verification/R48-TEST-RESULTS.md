# R48 Verification Results

Stand: TOR POS Pro `0.7.33.48` / `R48-Artikelnummer-Abholnummer`.
Basis: R47 KIOSK Schnellartikel / Mindestbestand / Warenwert.

## Automatische Artikel-Nr.
- Neue Artikel erhalten bei leerer SKU automatisch eine fortlaufende Nummer; Startbereich ist 100000.
- Beim Upgrade werden bestehende Artikel ohne Artikel-Nr. nachnummeriert.
- Bestehende manuelle/importierte Nummern bleiben erhalten.
- Ein SQL-Verhaltenstest mit bestehenden numerischen und alphanumerischen Nummern ist bestanden; nächste Nummer und Backfill kollidieren nicht mit dem höchsten numerischen Bestand.

## IMBISS Abholnummer
- Additive DB-Spalte `sales.pickup_number`.
- IMBISS-only Einstellung `imbiss.pickup_number.enabled`.
- Tagesbezogene Sequenz `pickup.YYYYMMDD`; Test bestätigt: gleicher Tag zählt hoch, neuer Tag startet bei 1.
- Abholnummer bleibt getrennt von der fiskalen Bonnummer.
- Bon-Druck, Bon-Historie und Cloud-Payload/Portal enthalten die Abholnummer.
- TRAINING verwendet nur eine Sitzungssequenz und verbraucht keine Produktivnummer.

## Cloud / Portal
- `npm run check`: bestanden.
- `npm test`: **15/15 bestanden**.
- Enthalten ist ein eigener R48-Test, der `pickup_number` persistiert und im Bon-Detail wieder ausliest.

## Regression / Schutz
- Die beiden SumUp-Quelldateien sind gegenüber R47 SHA-256-identisch.
- JSON-Dateien und Avalonia/Projekt-XML wurden erfolgreich geparst.
- R48 Release-/Installer-/Manifest-Markierungen sind konsistent auf `0.7.33.48` / `R48` gesetzt.

## Nicht in dieser Umgebung geprüft
- Kein .NET-10-SDK vorhanden: Desktop/Avalonia wurde hier **nicht kompiliert**.
- Kein echter Windows-PC-/Scanner-/58/80-mm-Drucker-/Kartenterminal-/TSE-Hardwaretest.
- Produktivbetrieb bleibt bewusst `TEST_ONLY` / `production_allowed=false` bis zur realen Fiskalabnahme.

Details: `r48-static-checks.log`, `r48-node-check.log`, `r48-cloud-tests.log`.
