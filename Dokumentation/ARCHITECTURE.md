# TOR POS – Architecture Overview

Stand: R182 · 2026-09-22

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

### Produktvarianten (R182)

Aus diesem einen geprüften Quellcode entstehen zwei Produkte und der gemeinsame
Build:

| | Edition (gespeichert) | Executable | AppData | Demo-/Lizenzidentität |
|---|---|---|---|---|
| TOR Einzelhandel | `KIOSK` | `TOR-Einzelhandel.exe` | `TOR-Einzelhandel` | `%PROGRAMDATA%\TOR-Einzelhandel` |
| TOR Gastro | `IMBISS` | `TOR-Gastro.exe` | `TOR-Gastro` | `%PROGRAMDATA%\TOR-Gastro` |
| gemeinsam (Rückfall) | frei wählbar | `TorPos.App.exe` | `TOR-POS-Pro` | `%PROGRAMDATA%\TOR-POS-Pro` |

Die Produktidentität ist eine Compile-Zeit-Konstante (`ProductBuild`) und steht
über jeder Laufzeitquelle: ein dedizierter Build lässt sich weder über eine
Umgebungsvariable noch über eine Konfiguration in die andere Edition versetzen.

Getrennt sind zusätzlich Windows-AppId, Installationsordner, Startmenü-/Desktop-
Identität, Prozess-Mutex und Crash-Log-Ordner, sodass beide Produkte parallel
installiert sein können und eine Deinstallation weder Daten noch das andere
Produkt berührt.

Die **gespeicherten** Editionscodes bleiben `KIOSK` und `IMBISS`. Datenbank
(`edition_scope`, `business.mode`), Lizenzen, Cloud-Payloads und
`edition.permanent.lock` auf Kundenrechnern tragen diese Werte; nur die nach
aussen sichtbaren Produktnamen sind neu.

### Bedienoberflächensprache (DE/TR/EN)

Die Bedienoberfläche kann auf Deutsch, Türkisch oder Englisch laufen
(Einstellungen → Alltag → Sprache, gespeichert als `ui.language`). Deutsch ist
die Vorgabe und bleibt es auch bei einem unbekannten oder leeren Wert.

Der deutsche Text bleibt im Fenster und ist zugleich der Nachschlageschlüssel
(`UiLanguage` / `UiTranslations`). Daraus folgen vier Eigenschaften:

- Ein fehlender Eintrag ist kein Fehler, sondern zeigt das deutsche Original.
  Eine unvollständige Übersetzung ist an einer echten Kasse damit harmlos.
- Eine Beschriftung, an der ein **Betrag** klebt - `GESAMT: 12,50 €`,
  `KARTENZAHLUNG · 12,50 €` - wird an `": "`, `" · "` und am Zeilenumbruch
  zerlegt und stückweise übersetzt. Die Beschriftung wird übersetzt, der Betrag
  bleibt exakt so, wie ihn das Fenster formatiert hat.
- **Die Daten der Betreiberin oder des Betreibers werden nicht übersetzt.**
  Zerlegt wird nur, wenn eine der beiden Seiten eine Zahl ist. `ARTIKEL ·
  GETRÄNKE` ist die Überschrift plus eine Warengruppe, die der Betrieb benannt
  hat und jederzeit umbenennen kann; sie bleibt unverändert, weil Bon,
  Warenliste und Berichte denselben Namen zeigen. Eine zweiteilige
  Beschriftung, die ganz aus Programmtext besteht, ist deshalb ein eigener
  Tabelleneintrag und keine Zerlegung.
- Deutsche Fachbegriffe der Kassenführung bleiben in jeder Sprache deutsch:
  Z-Bericht, X-Bericht, Z-Abschluss, DSFinV-K, TSE, DATEV, GoBD, § 146a. Mit
  diesen Wörtern spricht die Betreiberin oder der Betreiber mit Steuerberatung
  und Prüfung.

**Übersetzt wird ausschliesslich die Bedienoberfläche.** Bon, DSFinV-K-Export,
Z-Bericht, TSE-Prozessdaten und das Protokoll sind deutsche Aufzeichnungen und
entstehen in `TorPos.Core` / `TorPos.Infrastructure`. Beide Projekte
referenzieren die Sprachschicht nicht; eine Prüfung im Sicherheitslauf setzt
diese Grenze durch.

Jedes Bedienfenster rendert sich beim Öffnen in der gewählten Sprache. Sechs
Fenster tun das bewusst nicht, und die Prüfung nennt jedes davon mit Grund:
`TextReportWindow` und `ZArchiveWindow` zeigen einen Z- oder X-Bericht wörtlich,
die beiden Kundenanzeigen richten sich an die Kundschaft, und die beiden
Startfenster laufen, bevor die gespeicherte Sprache gelesen ist.

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

R182 führt 1160 automatisierte Safety-/Regression-Checks aus. Die reale
Hardware-/Fiskalabnahme bleibt separat.

Seit R182 rendert die CI zusätzlich die realen Fenster jedes dedizierten
Produkts, einschliesslich Startbildschirm und Anmeldung. Ein Build, der wegen
einer assemblygebundenen Ressourcenadresse nicht startet, fällt dadurch in der
CI auf und nicht erst an der Kasse.
