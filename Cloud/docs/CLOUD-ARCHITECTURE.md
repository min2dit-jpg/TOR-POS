# R43 current status

The current executable scope is documented in ../README.md and API-EXAMPLES.md.
The following foundation design contains future work as well as existing components.

# TOR POS Cloud – Architektur v0.1

## Ziel
Betreiber sollen ihre Kasse online beobachten können, ohne den Verkaufsbetrieb von einer Internetverbindung abhängig zu machen.

## Datenfluss
TOR POS Pro (lokal) -> Cloud Outbox -> HTTPS API -> TOR Cloud -> Kundenportal

### Local-first
1. Verkauf wird lokal vollständig abgeschlossen.
2. Cloud-Ereignis wird lokal durable gespeichert.
3. Hintergrund-Sync sendet das Ereignis.
4. Server bestätigt die Event-ID.
5. POS markiert das Outbox-Ereignis als SYNCED.
6. Bei Internetfehler bleibt das Ereignis lokal und wird später wieder versucht.

Cloud darf niemals Voraussetzung für BAR/KARTE, Bonnummer, TSE oder lokalen Datenbank-Commit sein.

## Mandantenmodell
- Business / Kunde
  - Branch / Filiale
    - Register / Kasse

Jede Portalabfrage ist an `business_id` gebunden. Jede Gerätesynchronisierung ist an genau eine `register_id` gebunden.

## Events v0.1
- `heartbeat`
- `sale.completed`
- `cash.movement`
- `z.closed`
- `stock.snapshot`

Alle Events haben eine eindeutige `event_id`. Der Server akzeptiert ein bereits empfangenes Ereignis nur als `duplicate`, erzeugt aber keinen zweiten Datensatz.

## Portal Phase 1 – Read only
- Dashboard
- Verkauf/Bons
- Berichte
- Bestand
- Filialen & Kassen
- Gerätestatus

## Phase 2 – kontrollierte Rückrichtung
Erst nach stabiler Read-only-Synchronisierung:
- Artikel-/Preisänderung im Portal
- POS lädt versionierte Stammdatenänderungen
- Konfliktregeln
- Freigabe/Audit

Keine Remote-Änderung abgeschlossener Verkaufs-/Fiskaldaten.
