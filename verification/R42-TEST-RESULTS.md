# TOR POS R42 – Prüfergebnis

Stand: 08.09.2026  
Basis: R41 Desktop + Cloud

## Ergebnis

- Cloud Syntaxprüfung: **bestanden** (`npm run check`).
- Cloud automatisierte Tests: **9/9 bestanden** (`npm test`).
- Desktop XAML/XML: **4 Dateien strukturell gültig**.
- JSON: **3 Dateien gültig**.
- C# Quellstruktur: **49 Dateien** tokenbasiert auf Klammerstruktur geprüft.
- MainWindow-XAML: **61 Click-Handler** im C#-Code gefunden.
- Berichtsdruck-Schnittstelle und Star-Windows-Druckerimplementierung vorhanden.
- SumUp-Quelldateien gegenüber R41 byteweise unverändert: **2/2**.

## R42-spezifisch geprüft

- Berichtsdruck verwendet dieselbe persistente, begrenzte Hintergrund-Druckwarteschlange wie Bon/Fehlerprotokoll.
- Berichtsformat kann 58 mm, 80 mm oder A4 sein; der konkrete Windows-Drucker wird im Berichtfenster gewählt.
- Drucker und Format werden je Berichtstyp in `app_settings` gespeichert.
- Produktabfrage lädt `stock_quantity`; normale Artikel-Stammdaten-Speicherung schreibt den Bestand absichtlich nicht mit.
- Nur bei tatsächlich geändertem Bestandsfeld wird der Bestand separat aktualisiert.
- Inventur-/Artikel-Bestandsänderung verwendet einen erwarteten Altbestand; bei zwischenzeitlicher Änderung wird der neuere Bestand nicht still überschrieben.
- Inventur zeigt Name, EAN, SKU/Artikel-Nr., Bestand, Preis, Gruppe/Warengruppe und unterstützt Scanner-ENTER-Suche.
- Cloud-Test deckt R42-Metadaten SKU, Barcode, Kategorie, Preis und Einheit explizit ab.

## Nicht in dieser Umgebung geprüft

Auf dem Generierungsrechner ist kein .NET-10-SDK installiert. Deshalb wurde der Avalonia/Windows-Desktop hier **nicht kompiliert**. Ebenfalls nicht physisch geprüft:

- echter 58-mm-Bondrucker,
- echter 80-mm-Bondrucker,
- A4-Windows-Drucker,
- Cutter/Papierlänge bei langen Berichten,
- tatsächlicher HID-Barcode-Scanner,
- reale TSE-Hardware.

## Windows-Abnahme

1. `Desktop\1-SETUP-ERSTELLEN.bat` ausführen.
2. Artikel mit Anfangsbestand anlegen; danach nur Preis ändern und Bestand kontrollieren.
3. Inventur mit Scanner-EAN testen.
4. X-Bericht, Monatsbericht, Warenbestand und archivierten Z-Bericht jeweils mit 58 mm / 80 mm / A4 testen.
5. Druckwarteschlangen-Prüfung bei absichtlich nicht verfügbarem Drucker testen.
6. Cloud-Artikel/Bestand übertragen und Portal-Metadaten kontrollieren.

Fiskalische Produktionsfreigabe bleibt unverändert gesperrt; R42 ändert SumUp, ZVT-Zahlungslogik oder TSE-Transaktionslogik nicht.
