# TOR POS R73 – Payment Outcome State Machine

## Ziel
R73 trennt zwei bisher zu stark vermischte Begriffe:

1. Checkout-Sicherheitsstatus
2. tatsächliches/ermitteltes Terminalergebnis

Die bestehende Sicherheitsregel bleibt erhalten:
Ein wirklich unklarer Vorgang darf niemals automatisch erneut kassiert werden.

## Checkout-Sicherheitsstatus
Weiterhin:
- PREPARED
- SENT
- UNKNOWN
- APPROVED
- CASH_READY
- COMMITTED
- NOT_CHARGED

Diese Zustände beschreiben, was TOR POS sicher tun darf.

## Terminal Outcome
Neu separat gespeichert:
- NONE
- APPROVED
- DECLINED
- CANCELLED
- NOT_SENT
- UNKNOWN

Beispiele:

Terminal sagt ausdrücklich abgelehnt:
`TerminalOutcome=DECLINED`
`CheckoutState=NOT_CHARGED`

Kunde/Terminal bricht ausdrücklich ab:
`TerminalOutcome=CANCELLED`
`CheckoutState=NOT_CHARGED`

Verbindung/Konfiguration scheitert vor Zahlungsauftrag:
`TerminalOutcome=NOT_SENT`
`CheckoutState=NOT_CHARGED`

PREPARED darf nicht manuell als bezahlt bestätigt werden, weil noch kein durable SENT-Marker existiert.

Timeout nach Zahlungsstart:
`TerminalOutcome=UNKNOWN`
`CheckoutState=UNKNOWN`

## Sicherheitsklassifizierung
Ein normal zurückgekehrter, aber erfolgloser ZVT-Aufruf wird nur dann als
DECLINED oder CANCELLED behandelt, wenn Status-/Fehlermeldung dies ausdrücklich
erkennen lässt.

Generisches `Error`, `UnknownFailure` oder sonstige unklare Antworten:
bleiben UNKNOWN.

Damit wird die Kasse bei klarer Ablehnung nicht unnötig gesperrt, aber ein
zweifelhaftes Ergebnis wird niemals optimistisch als "nicht belastet" behandelt.

## SENT vor externer Wirkung
Unmittelbar vor dem ZVT-Payment-Aufruf schreibt TOR jetzt durable:
`PREPARED -> SENT`
und:
`terminal_submitted=1`

Crash/Timeout danach bedeutet deshalb konservativ:
"Zahlung kann das Terminal erreicht haben."

Eine zweite Terminalübermittlung derselben OperationId ist blockiert.

## Reconciliation
Neu separat:
- AUTO_APPROVED
- AUTO_NOT_CHARGED
- MANUAL_PAID
- MANUAL_NOT_CHARGED

Ein manueller Prüfvorgang überschreibt NICHT das ursprüngliche TerminalOutcome.

Beispiel:
TerminalOutcome bleibt UNKNOWN,
aber nach Terminalbelegprüfung:
Resolution=MANUAL_PAID
CheckoutState=APPROVED.

Damit bleibt nachvollziehbar:
"Terminalantwort war unsicher, später wurde Zahlung mit Beleg bestätigt."

## Schema V5
checkout_operations erhält:
- terminal_outcome
- terminal_code
- terminal_message
- terminal_submitted
- resolution
- resolution_actor
- resolution_at

Bestehende R72.x offene SENT/UNKNOWN/APPROVED-Vorgänge werden beim Upgrade
konservativ als bereits gesendet markiert.
Bestehende UNKNOWN-Vorgänge erhalten TerminalOutcome UNKNOWN.
Für alte APPROVED-Vorgänge wird kein erfundenes automatisches Outcome gesetzt.

## Diagnose
SYSTEMSTATUS / DIAGNOSE zeigt neu:
`ZAHLUNGSJOURNAL`

Mögliche Zustände:
- FREI
- GESPERRT
- APPROVED / PREPARED etc.

Anzeige:
Checkout-State, TerminalOutcome, gesendet JA/NEIN, Resolution und OperationId.

## VOID / REVERSAL
R73 macht VOID bewusst NICHT zu einem neuen Zustand des ursprünglichen Verkaufs.

Eine spätere Stornierung/Reversal muss eine neue, referenzierte Gegenoperation
sein. Der Original-Payment-/Sale-Datensatz bleibt unverändert.

## Safety Tests
R73 neue Checks:
16 assert + 5 reject = 21

Neues Windows-Ziel:
`ALL 321 CHECKS PASSED`

## Produktionsstatus
Unverändert:
`production_allowed=false`

Reale Terminal-/TSE-/DSFinV-K-End-to-End-Freigabe bleibt offen.
