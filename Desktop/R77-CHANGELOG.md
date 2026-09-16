# TOR POS R77 – Küchenrouting nach Warengruppe/Station

## Neu
IMBISS-Warengruppen können jetzt optional einer festen Küchenstation
zugeordnet werden: Grill, Fritteuse, Getränke oder (Standard).

- Artikelverwaltung -> Warengruppe: neues Feld „Küchenstation".
- Einstellungen -> Geräte -> Küchendrucker: pro Station optional ein
  eigener Windows-Drucker aktivierbar (gleiche Auswahl/Testdruck-UI
  wie beim bestehenden Küchendrucker).
- Bestellzeilen werden beim Drucken nach der Küchenstation ihrer
  Warengruppe gruppiert. Comboartikel übernehmen die Station ihres
  Hauptartikels.
- Eine Warengruppe ohne gewählte Station (oder eine Station ohne
  aktivierten eigenen Drucker) bleibt unverändert auf dem einen
  konfigurierten Standard-Küchendrucker.

## Betroffene Druckwege
- Sofortverkauf (DIREKTVERKAUF): `MainWindow.PrintKitchenForSaleAsync`
  druckt jetzt einen eigenen Kitchen-Print-Job je Station.
- IMBISS ORDER-Modus (Bestellannahme/Änderung/Storno): die
  persistente Druckwarteschlange `OrderPrintOutbox` queued jetzt
  einen eigenen Auftrag je Station statt eines einzigen Auftrags.

## Datenbank
Neue Tabelle `category_kitchen_data(category_id, station)`, per
`CREATE TABLE IF NOT EXISTS` beim Start angelegt - keine
Schema-Versionserhöhung nötig, bestehende Datenbanken erhalten die
Tabelle automatisch beim nächsten Start.

## Unverändert
- Ohne jede Stationszuordnung verhält sich das System exakt wie vor
  R77: genau ein Küchen-Druckauftrag auf dem Standard-Küchendrucker.
- Küchenbons bleiben Deutsch und als KEIN STEUERBELEG gekennzeichnet.
- Safety-Ziel jetzt `ALL 378 CHECKS PASSED` (367 + 11 neue R77-Tests
  für Normalisierung, Repository-Rundlauf und Druckrouting).
- production_allowed bleibt unverändert an TSE/DSFinV-K gekoppelt;
  diese Änderung betrifft nur die Küchenbons, keine Fiskalbelege.
