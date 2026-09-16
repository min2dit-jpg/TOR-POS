# R48 – Automatische Artikelnummer / IMBISS Abholnummer

Basis: R47 KIOSK Schnellartikel / Mindestbestand / Warenwert.

## Artikelnummer
- Jeder neue Artikel erhält automatisch eine fortlaufende numerische Artikelnummer ab `100000`.
- Bestehende Artikel ohne Nummer werden beim Datenbank-Update einmalig nummeriert; vorhandene Nummern werden nicht geändert.
- Vor der Vergabe wird der höchste vorhandene numerische SKU-Wert berücksichtigt. Dadurch kollidiert die Automatik auch nach CSV-/TOR-Importen nicht mit einer höheren importierten Nummer.
- Artikelnummer im normalen Artikel-Editor ist schreibgeschützt.
- CSV-/TOR-Import: vorhandene Nummer wird übernommen; neuer Artikel ohne Nummer bekommt automatisch eine. Bei Aktualisierung eines bestehenden Artikels mit leerer Nummer wird seine bestehende Nummer nicht gelöscht.

## IMBISS Abholnummer
- Neue Einstellung unter `Einstellungen → Bedienfunktionen`: `Abholnummer automatisch vergeben`. Standard für IMBISS: aktiv.
- Echte abgeschlossene IMBISS-Verkäufe erhalten transaktional eine tägliche Abholnummer `001, 002, 003 …`.
- Die Tagessequenz wird nach lokalem Kalendertag getrennt; am nächsten Tag startet sie automatisch wieder bei `001`.
- Die Abholnummer ist nur eine operative Bestell-/Ausgabenummer. Fiskale Bonnummer, TSE-Transaktion und Z-Auswertung bleiben davon getrennt.
- Abholnummer wird groß auf 58/80-mm-Bon gedruckt, nach dem Verkauf in der Kassenstatuszeile angezeigt und beim Bon-Nachdruck wiederverwendet.
- Bon-Historie zeigt die Abholnummer ebenfalls.
- TRAINING/Entwicklung verwendet nur eine separate Sitzungs-Testnummer und verbraucht keine produktive Tagessequenz.

## TOR POS Cloud
- `sale.completed` enthält optional `pickup_number`.
- Cloud speichert und validiert die Abholnummer additiv und zeigt sie in Dashboard/Verkäufe/Bons/Bon-Details.
- KIOSK-Verkäufe bzw. alte Datensätze zeigen `–` / 0.

## Bewertung / nächste Prioritäten
Die externe R47-Bewertung wurde in den Roadmap-Abgleich aufgenommen. Hohe nächste Prioritäten bleiben: Menü/Combo, zweiter Küchenbon/Küchendrucker, Mehrsprachigkeit und reale Hardware-/Lasttests. Lieferplattformen werden als eigene Integrationsschicht geplant, nicht direkt in den Kassier-Hot-Path eingebaut.

## Nicht geändert
- SumUp Reader / Checkout / Connection-Code.
- ZVT Zahlungslogik.
- TSE-/DSFinV-K Produktionssperren.
- R43 Parkbon/Varianten/Bonlogo.
- R45 Update/Lizenz/2FA, R46 Cloud-Sync, R47 Mindestbestand/Warenwert.
