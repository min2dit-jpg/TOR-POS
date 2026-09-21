# TOR POS – Release Qualification

Stand: R181 · 2026-09-21

Dieses Dokument trennt **Entwicklungsstand**, **automatische Tests**, **Hardware-Abnahme** und **Produktionsfreigabe**. Ein grüner Build allein ist keine fiskalische Freigabe.

## Verbindliche Versionsquelle

Die Softwareversion wird ausschließlich in `Desktop/src/TorPos.Core/ReleaseInfo.cs` geführt. CI prüft die Spiegelwerte in Manifest, App-Projekt, Installer, README und CHANGELOG.

Aktueller Release-Stand dieser Dokumentation:

**R181 · Merd-M · 0.7.33.881**

## Qualifikationsstufen

### 1. Development Revision

Eine Änderung wird entwickelt und gegen die bestehenden Sicherheits-, Fach- und UI-Verträge geprüft. Integrationscode kann auf `main` vorhanden sein, ohne dadurch produktiv freigegeben zu sein.

### 2. Automated Qualified

Voraussetzungen:
- Windows Release Build erfolgreich
- Safety/Regression Suite vollständig erfolgreich
- UI-Snapshot-Check erfolgreich
- Cloud Syntax/Test Suite erfolgreich
- Versionskonsistenz erfolgreich
- Repository-Hygiene und fiskalische Release-Gates erfolgreich geprüft

Ergebnis: Code ist automatisiert geprüft, aber noch nicht hardware- oder fiskalisch freigegeben.

**R181-Baseline:** 1122 Safety-/Regression-Checks.

### 3. Hardware Acceptance

Für Funktionen mit realer Hardware müssen die vorgesehenen Geräte tatsächlich angeschlossen und mit dem konkreten Build geprüft werden.

Für Swissbit/TSE mindestens:
1. Gerät/Provider eindeutig erkennen
2. TSE-Identität und Seriennummer erfassen
3. Client-Registrierung mit der vorgesehenen Kasse prüfen
4. kontrollierten BAR-Testvorgang ausführen
5. Start/Finish und Signaturzähler prüfen
6. ProcessType/ProcessData mit TOR vergleichen
7. DSFinV-K-Anhang-I-QR und Belegdaten prüfen
8. TAR-Export prüfen
9. TSE-/USB-Ausfall dokumentieren
10. Neustart/Recovery prüfen

Die Ergebnisse werden unter `verification/` abgelegt. Vorlage:
`verification/HARDWARE-E2E-TEMPLATE.md`.

Der read-only fiskaltrust/Swissbit-Probe vom 21.09.2026 ist ein Diagnose-Nachweis, aber **keine** vollständige Hardware Acceptance.

### 4. Release Candidate

Erst nach grüner automatischer Prüfung und allen für den Release relevanten Hardware-Abnahmen.

Zusätzlich:
- unabhängige fachliche/technische Prüfung für Änderungen an VAT, TSE, DSFinV-K, Beleg- oder Recovery-Logik dokumentieren
- offene fiskalische Release-Blocker prüfen
- Third-Party-/Lizenzinventar prüfen
- Setup-Hash archivieren
- Release Notes erstellen
- Update-Signatur/Authenticode-Regel prüfen

### 5. Production Release

Ein Build darf nur dann als **fiskalisch produktionsfreigegeben** bezeichnet werden, wenn die dafür definierten automatischen und realen Nachweise vollständig vorliegen.

Bis dahin bleibt der Build Development/Validation/Pre-Release für fiskalische Echtbuchungen, auch wenn Setup, UI und Funktionscode technisch erstellt werden können.

## Aktueller Fiskalstatus

R181 ist der aktuelle dokumentierte Entwicklungs-/Validierungsstand. Die frühere Angabe „R149 Produktions-Basisstand / 881 Checks“ ist veraltet.

Die zentralen `FiscalRelease`-Nachweise stehen weiterhin auf **false**:

- `DsfinvkValidated`
- `KassenSichVReceiptValidated`
- `ParkedOrderTseValidated`
- `PfandTaxValidated`
- `PhysicalTseE2EValidated`
- `IndependentFiscalReviewValidated`

Daher bleibt die produktive fiskalische Buchung gesperrt.

Zusätzlich bleibt der produktive Remote-Updatepfad gesperrt, solange kein TOR/Demirkaan-Code-Signing-Zertifikat als `UpdateSignerThumbprint` fest hinterlegt ist.

## R181 Prüfziel

R181 korrigiert Retouren-, Cloud-, Scanner- und Kassenschubladenpfade gegenüber R178. Die fiskalischen Produktions-Gates bleiben unverändert geschlossen; Die Kassenschublade wurde am Zielsystem real bestätigt; der Scannerpfad wurde nach dem ersten realen R179-Test in R181 korrigiert und benötigt die erneute reale Gegenprobe.

Bei grüner CI müssen mindestens nachgewiesen sein:
- Version R181 / 0.7.33.881 in allen Versionsspiegeln
- vollständiger Build und Demo-Build
- 1122/1122 Safety-/Regression-Checks
- UI-Snapshot-Prüfung
- Cloud-Checks
- erzeugtes Windows-Testpaket
- erzeugtes Kunden-Setup
- ZIP des exakt committed Source-Trees

## Release-Tags und Artefakte

Freigegebene Release-Commits sollten signiert werden, sobald der verwendete GitHub-/Signing-Prozess dafür vollständig eingerichtet ist.

CI-Artefakte sind Build-Nachweise und Verteilpakete; sie ersetzen keine Hardware- oder Fiskalabnahme.
