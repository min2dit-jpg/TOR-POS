# R47 – KIOSK Schnellartikel / Mindestbestand / Warenwert

Basis: R46 Software & Update / automatischer Cloud-Bestand.

## Hauptkasse
- `SCHNELLARTIKEL` direkt neben `EAN SUCHEN`: freie Bezeichnung, Preis und 7/19 % USt.
- Schnellartikel existiert nur im aktuellen Verkauf; es wird kein Artikelstammsatz und kein Bestand erzeugt.
- Eine vorgemerkte Menge wird auf den Schnellartikel angewendet.
- Mindestbestand-Warnung als kompakter Header-Badge; Klick öffnet Inventur.

## Artikel / Bestand
- Artikelstamm erweitert um `Einkaufspreis` und `Mindestbestand`.
- Additive SQLite-Migration: `purchase_price_cents`, `min_stock_quantity`; bestehende Kundendaten bleiben erhalten.
- `Mindestbestand = 0` deaktiviert die Warnung für den Artikel.
- Artikeländerungen ohne Bestandsänderung überschreiben weiterhin keinen parallel veränderten Bestand.
- CSV und TOR-Datenbank-Import/Export berücksichtigen die neuen Felder, soweit vorhanden.

## Inventur / Warenbestand
- Scanner-Ablauf: Barcode → Bestand → ENTER → nächster Barcode.
- Übersicht zeigt Bestand/Minimum, VK, EK, Einkaufs-Warenwert und Warengruppe.
- Warenbestand-Bericht enthält Warnungsanzahl sowie Einkaufs-/Verkaufs-Warenwerte.
- Niedrige Bestände werden artikelbezogen gegen den konfigurierten Mindestbestand geprüft.

## TOR POS Cloud
- Bestandssnapshot enthält `purchase_price_cents` und `min_stock_quantity`.
- Cloud-Schema wird additiv erweitert.
- Portal zeigt VK, EK, Bestand, Mindestbestand und Einkaufs-Warenwert.
- Niedrig-Bestand-Auswertung verwendet den individuellen Mindestbestand statt einer festen globalen Grenze.
- R46 Outbox/Coalescing/idempotente Sale-Bestandslogik bleibt erhalten.

## Nicht geändert
- SumUp Reader / Checkout / Connection-Code.
- ZVT-Zahlungslogik.
- TSE-/DSFinV-K-Fiskalsperren.
- R43 Parkbon-Fix, Varianten im Artikel und Bonlogo.
- R45 Update/Lizenz/2FA und R46 Software-&-Update/Cloud-Sync-Grundlogik.

## Prüfung
- Cloud: 14/14 automatische Tests.
- `npm run check`: JavaScript-Syntaxprüfung bestanden.
- Desktop: XAML-XML und C# Klammer-/Strukturprüfung bestanden.
- SumUp-Dateien per SHA-256 gegen R46 unverändert verifiziert.
- .NET 10 SDK steht in der Generierungsumgebung nicht zur Verfügung; Windows/Avalonia-Build und reale Scanner-/Druckerabnahme erfolgen auf dem Kassen-PC.
