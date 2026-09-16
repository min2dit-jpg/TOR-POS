# TOR POS v0.7.33 – Frontend / Sales Flow R3

Ziel: Ein Kassierer soll ohne Schulung erkennen, was als Nächstes zu tun ist.

## Änderungen

- Hauptmenü `WARENVERWALTUNG` in `STAMMDATEN` umbenannt.
- `EINSTELLUNGEN` zeigt nur noch die acht neuen Gruppen:
  - Kasse & Bedienung
  - Firma & Bon
  - Artikel & Steuern
  - Zahlung
  - Geräte
  - Personal
  - Datensicherung
  - Erweitert / Techniker
- Alte Einstellungs-Handler bleiben intern kompatibel.
- Leerer Bon zeigt jetzt einen deutlichen, editionsabhängigen Bedienhinweis.
- KIOSK: Fokus auf Barcode-Scanner; Touch bleibt Schnellzugriff.
- IMBISS: Fokus auf Warengruppe/Artikel-Touch; Scanner bleibt immer aktiv.
- BAR/KARTE sind bei leerem Bon deaktiviert und werden erst bei vorhandenem Verkaufsinhalt aktiv.
- Rabatt, +1/-1, Sofort-Storno und Parken werden bei leerem Bon ebenfalls deaktiviert, soweit sinnvoll.
- Zahlungsbuttons zeigen zusätzlich `BEZAHLEN`, ohne einen zusätzlichen Dialogschritt einzubauen.
- Kartenzahlungs-UI stellt nach einem Terminalvorgang den korrekten Buttonstatus anhand des Warenkorbs wieder her.
- Editionsaktion verwendet jetzt auch ohne Installations-Lock den eingestellten Business-Modus korrekt (IMBISS=EXTRA, KIOSK=PFAND).

## UX-Prinzip

Normalverkauf:

1. Artikel scannen oder antippen
2. BAR oder KARTE
3. fertig

Technische und seltene Funktionen bleiben aus dem täglichen Blickfeld heraus.
