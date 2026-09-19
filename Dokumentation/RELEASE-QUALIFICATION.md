# TOR POS – Release Qualification

Stand: 2026-09-19

Dieses Dokument trennt **Entwicklungsstand**, **automatische Tests**, **Hardware-Abnahme** und **Produktionsfreigabe**. Ein grüner Build allein ist keine fiskalische Freigabe.

## Verbindliche Versionsquelle

Die Softwareversion wird ausschließlich in `Desktop/src/TorPos.Core/ReleaseInfo.cs` geführt. CI prüft die Spiegelwerte in Manifest, App-Projekt, Installer, README und CHANGELOG.

## Qualifikationsstufen

### 1. Development Revision

Eine Änderung darf entwickelt und per PR geprüft werden. Draft-Branches für Hardware-/Integrationsarbeit sind ausdrücklich keine Produktionsfunktion.

Beispiele:
- `integration/fiskaltrust-sandbox`
- `validation/swissbit-sdk-probe`
- `r150/mixed-vat-menu-guard`

### 2. Automated Qualified

Voraussetzungen:
- Windows Release Build erfolgreich
- Safety/Regression Suite vollständig erfolgreich
- UI-Snapshot-Check erfolgreich
- Cloud Syntax/Test Suite erfolgreich
- Versionskonsistenz erfolgreich
- keine neuen ungeklärten Build-Warnings

Ergebnis: Code ist automatisiert geprüft, aber noch nicht hardware- oder fiskalisch freigegeben.

### 3. Hardware Acceptance

Für Funktionen mit realer Hardware müssen die vorgesehenen Geräte tatsächlich angeschlossen werden.

Für Swissbit/TSE mindestens:
1. SDK/WORM API laden und Gerät erkennen
2. TSE-Identität/Seriennummer erfassen
3. kontrollierten BAR-Testvorgang ausführen
4. Start/Finish und Signaturzähler prüfen
5. ProcessType/ProcessData gegen TOR vergleichen
6. DSFinV-K-Anhang-I-QR prüfen
7. 80-mm-Beleg prüfen
8. TAR-Export prüfen
9. USB-/Geräteausfall dokumentieren
10. Neustart/Recovery prüfen

Die Ergebnisse werden unter `verification/` abgelegt. Vorlage:
`verification/HARDWARE-E2E-TEMPLATE.md`.

### 4. Release Candidate

Erst nach grüner automatischer Prüfung und allen für den Release relevanten Hardware-Abnahmen.

Zusätzlich:
- offene fiskalische Release-Blocker prüfen
- Third-Party-/Lizenzinventar prüfen
- Setup-Hash archivieren
- Release Notes erstellen
- Update-Signatur/Authenticode-Regel prüfen

### 5. Production Release

Ein GitHub Release oder Kunden-Setup darf nur dann als **Produktionsrelease** bezeichnet werden, wenn alle für diesen Build erforderlichen automatischen und realen Abnahmen abgeschlossen sind.

Vorherige Builds dürfen höchstens eindeutig als Development/Validation/Pre-Release bezeichnet werden.

## Aktueller Stand

R149 ist der dokumentierte Software-Basisstand. BAR TESTBON-Vorbereitung ist als nicht-fiskalische Diagnose-/Abnahmevorbereitung integriert. Die reale physische Swissbit-TSE-End-to-End-Abnahme bleibt separat erforderlich.

## Release-Tags

Für freigegebene Builds sollten Release-Tags/Release-Commits signiert werden, sobald der verwendete Git-Workflow dafür eingerichtet ist. Diese Git-Signatur verbessert die Software-Lieferkette, ersetzt aber niemals TSE-Signaturen oder gesetzliche Kassendaten.
