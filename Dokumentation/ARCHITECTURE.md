# TOR POS – Architecture Overview

Stand: R149-Basis, 2026-09-19

## Hauptkomponenten

### Desktop/src/TorPos.Core

Domänen- und fiskalische Kernlogik:
- Verkaufs-/Warenkorbmodelle
- VAT-/Preislogik
- `Kassenbeleg-V1` / `Bestellung-V1` ProcessData
- Beleg-/QR-Datenmodelle
- Schnittstellen für Persistenz und Geräte

Core soll keine UI- oder konkrete Hardwareabhängigkeit enthalten.

### Desktop/src/TorPos.Application

Use-Case-/Orchestrierungsebene, insbesondere Checkout-Abläufe. UI-Entscheidungen bleiben in App, konkrete Persistenz-/Geräteimplementierungen in Infrastructure.

### Desktop/src/TorPos.Infrastructure

Konkrete technische Implementierungen:
- SQLite
- Swissbit WORM/TSE
- ZVT-Terminal
- Druck
- Backup
- DSFinV-K Export
- Audit/Repositories
- externe Dienste

### Desktop/src/TorPos.App

Avalonia-Windows-Anwendung:
- Kassiereroberfläche
- Diagnose
- Einstellungen
- Dialoge
- DI/Window-Composition

### Cloud

Separater Node.js-Dienst für TOR-Cloud-Funktionen. Cloud-Verfügbarkeit darf den lokalen Kassiervorgang nicht zu einer Online-Abhängigkeit machen.

## Fiskalische Grenze

Auf `main` ist die direkte Swissbit-WORM/TSE-Architektur die maßgebliche Produktionsvorbereitung.

Der fiskaltrust-Code liegt in einer getrennten Sandbox-/Integrationsbranch und darf nicht als Bestandteil der R149-Produktionsarchitektur beschrieben werden, solange er nicht ausdrücklich qualifiziert und integriert wurde.

## Checkout- und Recovery-Prinzip

Zahlung und fiskalische Vorgänge werden so geführt, dass unklare Zustände nicht blind wiederholt werden. Insbesondere darf ein unklarer Terminalzustand nicht zu einer zweiten Belastung führen.

Für BAR gilt kein Terminal-`PREPARED → SENT`-Ablauf; der Cash-Journalpfad unterscheidet sich bewusst vom Kartenpfad.

## TSE-Vorgang

Der TSE-Vorgang beginnt mit dem fachlichen Vorgang und wird mit dem passenden ProcessType/ProcessData abgeschlossen. TSE-Ausfall, offene Vorgänge und Neustartfälle werden separat behandelt und dürfen nicht durch erfundene Signaturdaten „repariert“ werden.

## Experimentelle Branches

Folgende Arten von Branches bleiben bewusst von `main` getrennt, bis ihre Voraussetzungen erfüllt sind:
- fiskaltrust Middleware Sandbox
- Swissbit SDK Probe
- noch unvollständige R150+-Fiskaländerungen

## Verifikation

Automatisierte Tests, Hardware-Abnahme und fiskalische Produktionsfreigabe sind drei getrennte Nachweisarten. Sie werden nicht gegenseitig ersetzt.
