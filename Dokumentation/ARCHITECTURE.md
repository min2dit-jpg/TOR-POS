# TOR POS – Architecture Overview

Stand: R179 · 2026-09-21

## Hauptkomponenten

### Desktop/src/TorPos.Core

Domänen- und fiskalische Kernlogik:
- Verkaufs-/Warenkorbmodelle
- VAT-/Preislogik
- `Kassenbeleg-V1` / `Bestellung-V1` ProcessData
- Beleg-/QR-Datenmodelle
- Checkout-/Fiskalregeln und Release-Gates
- Schnittstellen für Persistenz und Geräte

Core enthält keine konkrete UI- oder Hardwareimplementierung.

### Desktop/src/TorPos.Application

Use-Case- und Orchestrierungsebene, insbesondere Checkout-Abläufe. UI-Entscheidungen bleiben in App; konkrete Persistenz-, Fiskal- und Geräteimplementierungen liegen in Infrastructure.

### Desktop/src/TorPos.Infrastructure

Konkrete technische Implementierungen:
- SQLite, Schema-Migrationen und zentrale FIFO-IoQueue
- Swissbit WORM/TSE
- fiskaltrust Middleware Queue-/Sign-Client und read-only Swissbit/SCU-Diagnose
- ZVT-Terminal und Terminal-Sicherheitsjournal
- Druck, Druckjournal und Kassenschublade
- Backup/Restore
- DSFinV-K 2.4 Export
- Audit/Repositories
- externe Dienste

### Desktop/src/TorPos.App

Avalonia-Windows-Anwendung:
- Kassiereroberfläche
- Bestell-/Parken-Abläufe
- Diagnose
- Einstellungen
- Benutzer-/Rechteverwaltung
- Dialoge
- DI/Window-Composition

### Cloud

Separater Node.js-Dienst für TOR-Cloud-Funktionen. Cloud-Verfügbarkeit darf den lokalen Kassiervorgang nicht zu einer Online-Abhängigkeit machen.

## Fiskalische Systemgrenze

Auf `main` sind zwei technische TSE-Wege vorbereitet:

1. direkte Swissbit-WORM/TSE-Integration,
2. fiskaltrust Middleware mit Swissbit-SCU sowie lokalem Queue-v1-Client.

Der fiskaltrust-Code ist seit R175/R176 Bestandteil von `main`; er ist daher kein separater Sandbox-Stand mehr. Das bedeutet jedoch **keine produktive Freigabe**. Der reale fiskaltrust-Transaktionspfad, die physische TSE-E2E-Abnahme und die übrigen Fiskalnachweise bleiben durch die zentralen `FiscalRelease`-Gates gesperrt.

Ein erfolgreicher Diagnose-/Echo-Test, eine erreichbare SCU oder ein erkannter Swissbit-Datenträger ersetzen keine reale Start-/Finish-/TAR-/DSFinV-K-Abnahme.

## Checkout- und Recovery-Prinzip

Zahlung und fiskalische Vorgänge werden so geführt, dass unklare Zustände nicht blind wiederholt werden. Insbesondere darf ein unklarer Terminalzustand nicht zu einer zweiten Belastung führen.

Kartenzahlungen werden über ein persistentes Checkout-Journal abgesichert. Ein nach Terminalübermittlung ungeklärter Zustand bleibt `UNKNOWN`, bis er ausdrücklich abgeglichen wurde.

Kartenerstattungen verwenden einen separaten persistenten Refund-Lock. Ein ungeklärter Refund blockiert weitere Erstattungsversuche für denselben Ursprungsbeleg.

Für BAR gilt kein Terminal-`PREPARED → SENT`-Ablauf; der Cash-Pfad unterscheidet sich bewusst vom Kartenpfad.

## TSE-Vorgang

Der TSE-Vorgang beginnt mit dem fachlichen Vorgang und wird mit dem passenden ProcessType/ProcessData abgeschlossen. TSE-Ausfall, offene Vorgänge und Neustartfälle werden separat behandelt und dürfen nicht durch erfundene Signaturdaten „repariert“ werden.

Storno und Retoure werden fiskalisch als eigener `Beleg` mit `Kassenbeleg-V1` geführt. Die Gegenbuchungsbeträge werden mit umgekehrtem Vorzeichen abgebildet; die Referenz zum Ursprungsbeleg wird im DSFinV-K-Datensatz über `Bon_Referenzen` geführt und ist nicht Teil der TSE-processData.

## Datenintegrität

Abgeschlossene Verkäufe, Positionen, Bedienerzuordnungen, Tagesabschlüsse, Z-Archive und Audit-Ereignisse besitzen Datenbankseitige Schutzmechanismen gegen nachträgliches UPDATE/DELETE. Korrekturen erfolgen als neue Gegenbuchung, nicht als Überschreiben des Ursprungs.

## Backup und Update

Backups enthalten einen konsistenten SQLite-Snapshot sowie definierte lokale Assets und ein Hash-Manifest. Restore wird in ein neues Ziel entpackt und gegen das Manifest verifiziert.

Remote-Updates sind in Release-Builds an HTTPS, SHA-256 und einen fest hinterlegten Authenticode-Signer gebunden. Solange `TorRelease.UpdateSignerThumbprint` leer ist, bleibt der produktive Remote-Updatepfad absichtlich gesperrt.

## Verifikation

Automatisierte Tests, Hardware-Abnahme und fiskalische Produktionsfreigabe sind drei getrennte Nachweisarten. Sie werden nicht gegenseitig ersetzt.

R179 führt 1114 automatisierte Safety-/Regression-Checks aus. Die reale Hardware-/Fiskalabnahme bleibt separat.
