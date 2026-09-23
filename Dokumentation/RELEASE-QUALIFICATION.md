# TOR POS – Release Qualification

Stand: R182 · 2026-09-23

Dieses Dokument trennt **Entwicklungsstand**, **automatische Tests**, **Hardware-Abnahme** und **Produktionsfreigabe**. Ein grüner Build allein ist keine fiskalische Freigabe.

## Verbindliche Versionsquelle

Die Softwareversion wird ausschließlich in `Desktop/src/TorPos.Core/ReleaseInfo.cs` geführt. CI prüft die Spiegelwerte in Manifest, App-Projekt, Installer, README und CHANGELOG.

Aktueller Release-Stand dieser Dokumentation:

**R182 · Merd-D · 0.7.33.882**

## Qualifikationsstufen

### 1. Development Revision

Eine Änderung wird entwickelt und gegen die bestehenden Sicherheits-, Fach- und UI-Verträge geprüft. Integrationscode kann auf `main` vorhanden sein, ohne dadurch produktiv freigegeben zu sein.

### 2. Automated Qualified

Voraussetzungen:
- Windows Release Build erfolgreich
- Safety/Regression Suite vollständig erfolgreich
- UI-Snapshot-Check erfolgreich – für den gemeinsamen Build **und** für jedes
  dedizierte Produkt. Bauen und Paketieren allein beweist nicht, dass ein
  dediziertes Produkt startet: R182 wurde in einem Zustand gebaut und
  paketiert, in dem beide Produkte beim Laden des Anmeldefensters abbrachen.
- Cloud Syntax/Test Suite erfolgreich
- Versionskonsistenz erfolgreich
- Repository-Hygiene und fiskalische Release-Gates erfolgreich geprüft

Ergebnis: Code ist automatisiert geprüft, aber noch nicht hardware- oder fiskalisch freigegeben.

**R182-Baseline:** 1236 Safety-/Regression-Checks.

### 3. Hardware Acceptance

Für Funktionen mit realer Hardware müssen die vorgesehenen Geräte tatsächlich angeschlossen und mit dem konkreten Build geprüft werden.

Für Swissbit/TSE mindestens:
1. Gerät/Provider und TSE-Generation (1 / 1.1 / 2) eindeutig erkennen
2. TSE-Identität, Seriennummer und exakten Zertifikatsablauf erfassen
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

R182 ist der aktuelle dokumentierte Entwicklungs-/Validierungsstand. Die frühere Angabe „R149 Produktions-Basisstand / 881 Checks“ ist veraltet.

Die zentralen `FiscalRelease`-Nachweise stehen weiterhin auf **false**:

- `DsfinvkValidated`
- `KassenSichVReceiptValidated`
- `ParkedOrderTseValidated`
- `PfandTaxValidated`
- `PhysicalTseGeneration1E2EValidated`
- `PhysicalTseGeneration11E2EValidated`
- `PhysicalTseGeneration2E2EValidated`
- `CloudTseRelease.FiskaltrustValidated`
- `CloudTseRelease.FiskalyValidated`
- `CloudTseRelease.DeutscheFiskalValidated`
- `IndependentFiscalReviewValidated`

Daher bleibt die produktive fiskalische Buchung gesperrt. Physische TSE-Abnahmen
sind generationsgebunden: Gen 1, Gen 1.1 und Gen 2 besitzen getrennte
E2E-Freigaben. Eine Abnahme darf keine andere Generation freigeben; eine
unbekannte Gerätegeneration bleibt fail-closed. Cloud-TSE-Freigaben sind davon
getrennt und werden pro Anbieter geführt; eine bestandene Anbieter-Abnahme darf
keinen anderen Cloud-TSE-Anbieter freischalten.

Zusätzlich bleibt der produktive Remote-Updatepfad gesperrt, solange kein TOR/Demirkaan-Code-Signing-Zertifikat als `UpdateSignerThumbprint` fest hinterlegt ist.

## R182 Prüfziel

R182 trennt **TOR Einzelhandel** und **TOR Gastro** als eigenständige Produkte aus demselben geprüften Quellcode. Die gespeicherten Editionscodes KIOSK/IMBISS und damit die Kompatibilität von Datenbank, Lizenzen und Cloud-Payloads bleiben unverändert. Die fiskalischen Produktions-Gates bleiben geschlossen.

Fachlich zusätzlich abgesichert: backup-first Datenübernahme aus R181 einschliesslich einer unterbrochenen Kasse mit WAL-Inhalt, Rückfallbewertung vor und nach dedizierten Schreibvorgängen, Produktidentität gegen Umgebungs-/Konfigurationsmanipulation, Demo-Recht einmal pro PC und Produkt sowie der arbeitsbereite Auslieferungszugang ohne erzwungenen Zugangsdialog.

Bei grüner CI müssen mindestens nachgewiesen sein:
- Version R182 / 0.7.33.882 in allen Versionsspiegeln
- vollständiger Build und Demo-Build
- Release-Build je dediziertem Produkt (KIOSK und IMBISS)
- 1236/1236 Safety-/Regression-Checks
- UI-Snapshot-Prüfung des gemeinsamen Builds
- gerenderte UI-Prüfung **jedes dedizierten Produkts**, damit ein nicht startfähiges Produkt in der CI und nicht an der Kasse auffällt
- Cloud-Checks
- erzeugtes Windows-Testpaket
- erzeugtes Kunden-Setup
- erzeugte Setups `TOR-Einzelhandel-Setup.exe` und `TOR-Gastro-Setup.exe`
- ZIP des exakt committed Source-Trees

Die erste physische Windows-Installationsabnahme wird über `verification/R182-SPLIT-INSTALL-ABNAHME.md` geführt und ist nicht Teil der automatischen Qualifikation.

## Release-Tags und Artefakte

Freigegebene Release-Commits sollten signiert werden, sobald der verwendete GitHub-/Signing-Prozess dafür vollständig eingerichtet ist.

CI-Artefakte sind Build-Nachweise und Verteilpakete; sie ersetzen keine Hardware- oder Fiskalabnahme.
