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

## Digitaler Kassenbon (R145) – zwei Sicherheitsbereiche
- `api.<domain>`: Kasse ↔ Cloud, Portal, Updates – Authentifizierung zwingend.
- `bon.<domain>`: Kundenkopie eines Bons hinter einem 256-Bit-Token – kein Login, keine Cookies, nichts außer Belegen.

Der digitale Kassenbon ist kein Teil des Fiskalarchivs: Kassen-DB, TSE und DSFinV-K bleiben auf der Kasse und
unterliegen den gesetzlichen Aufbewahrungsfristen; die Cloud-Kopie wird nach Ablauf gelöscht. Ist die Cloud nicht
erreichbar, gibt die Kasse den Papierbeleg aus – auch der digitale Bon ist also nie Voraussetzung für den Verkauf.
Details: `DIGITALER-KASSENBON.md`.

## Mandantenmodell
- Business / Kunde
  - Branch / Filiale
    - Register / Kasse

Jede Portalabfrage ist an `business_id` gebunden. Jede Gerätesynchronisierung ist an genau eine `register_id` gebunden.

## Events (Stand 26.09.2026 – alle werden von der Kasse gesendet)
- `heartbeat` – Status, Edition, Betriebsart, Sync-Rückstand, TSE-Zertifikat
- `sale.completed` – Verkauf, Storno, Retoure (Vorzeichen über `transaction_type`)
- `cash.movement` – echte Einlage/Entnahme und Kassendifferenz, mit DSFinV-K-Geschäftsvorfall
- `z.closed` – Kassenabschluss mit Zeitraum, Zahlarten, Storno/Retoure und USt-Gruppen
- `stock.snapshot`

Kasse und Cloud teilen die Vertragsdateien in `tests/fixtures/`; Desktop- und Cloud-Tests prüfen dieselben Dateien.

Alle Events haben eine eindeutige `event_id`. Der Server akzeptiert ein bereits empfangenes Ereignis nur als `duplicate`, erzeugt aber keinen zweiten Datensatz.

## Portal Phase 1 – Read only
- Dashboard
- Verkauf/Bons
- Berichte (Zeitraum, CSV, Z-Archiv, Einlagen/Entnahmen)
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
