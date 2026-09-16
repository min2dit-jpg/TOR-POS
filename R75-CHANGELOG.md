# TOR POS R75 – Application Layer Foundation

## Ausgangslage
R74 Safety wurde auf Windows bestätigt:
`ALL 333 CHECKS PASSED`

R75 ist ein kontrollierter Architektur-Refactor.
Schema bleibt V5.

## Neue Schicht
Neu:
`TorPos.Application`

Target:
`net10.0`

Abhängigkeit:
`TorPos.Application -> TorPos.Core`

Keine Referenz auf:
- TorPos.Infrastructure
- Avalonia
- Microsoft.Data.Sqlite

## Core Boundary
Neu:
`ICheckoutJournal`

`CheckoutJournal` implementiert dieses Interface.
Der ZVT-Adapter hängt ebenfalls am Interface statt an der konkreten SQLite-Klasse.

## Aus MainWindow extrahiert
`CheckoutApplicationService` übernimmt:
- Fiskal-Preflight
- Checkout Begin
- Cash -> CASH_READY
- Card Terminal Call
- PREPARED -> NOT_SENT/NOT_CHARGED
- APPROVED Validierung
- DECLINED/CANCELLED -> NotCharged
- UNKNOWN -> Unresolved
- manuelle Reconciliation
- Fiskal-Recheck nach MANUAL_PAID

MainWindow behält UI-Verantwortung:
- Dialoge
- Statusmeldungen
- Warenkorb
- Druck
- Simulation
- KIOSK/IMBISS Layout

## Ergebnisobjekt
Application liefert typisiert:
- ReadyToCommit
- NotCharged
- Unresolved
- FiscalBlocked

Zusätzlich:
- CheckoutOperation
- TerminalResult
- FiscalReadiness
- technische Timing-Werte

Damit kann der nächste MVVM-Schritt auf demselben Use Case aufbauen.

## Performance
Die bisherigen Metriknamen bleiben erhalten:
- checkout.fiscal_preflight
- checkout.journal_begin
- terminal.payment_roundtrip

Application misst nur Zeitwerte und kennt PerformanceCounters nicht.

## KIOSK / IMBISS
R75 ist gemeinsame technische Infrastruktur.
Edition-spezifische Funktionen bleiben getrennt.

## Safety
R75 neue Checks:
14 assert + 1 reject = 15

Neues Windows-Ziel:
`ALL 348 CHECKS PASSED`

## Produktionsstatus
Unverändert:
`production_allowed=false`
