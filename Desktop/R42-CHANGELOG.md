# R42 – KIOSK Waren / Bestand / Berichte

Basis: **R41 Desktop + Cloud**. SumUp, ZVT-Zahlungslogik, TSE-Transaktionslogik und produktive Fiskalsperre wurden in R42 nicht verändert.

## WAREN-Menü
- Das bisher lange Warenmenü ist in **ARTIKEL**, **BESTAND / INVENTUR** und **DATENAUSTAUSCH** gegliedert.
- Artikelverwaltung nennt Scanner-Suche jetzt direkt im Menü.
- Inventur und Warenbestand liegen zusammen, damit KIOSK-Bediener nicht zwischen weit entfernten Menüs wechseln müssen.

## Artikelverwaltung
- Neuer Suchbereich **Artikel suchen / Scanner**: Name, EAN oder Artikel-Nr./SKU.
- Scanner mit ENTER wählt einen exakten EAN-/SKU-Treffer.
- **Bestand / Anzahl** wird direkt beim Anlegen oder Bearbeiten eines Artikels angezeigt.
- Ein neu angelegter Artikel kann sofort einen Anfangsbestand erhalten.
- Preis-/Name-/EAN-Änderungen verändern den Bestand nicht, solange der Bestandswert nicht geändert wurde.
- Bei Bestandsänderung wird der beim Öffnen gelesene Bestand als Vergleichswert verwendet. Hat zwischenzeitlich z. B. ein Verkauf oder eine andere Inventur den Bestand geändert, überschreibt TOR den neueren Wert nicht.

## Inventur / Warenbestand
- Gesamte aktive Artikelliste mit Name, EAN, Artikel-Nr., Bestand, Preis und Warengruppe.
- Suche über Name, EAN, SKU, Gruppe oder Warengruppe.
- Scanner-EAN + ENTER springt direkt zum Artikel und fokussiert die Mengenangabe.
- Bestandsänderungen besitzen denselben Konfliktschutz wie im Artikeleditor.
- Warenbestandsbericht enthält zusätzlich Artikel-Nr., Gruppe und Warengruppe.

## Berichte / Drucker
- BERICHTE ist jetzt in **TAGESKONTROLLE**, **VERKAUF / BESTAND**, **ARCHIV / PERSONAL** und **FINANZAMT / EXPORT** gegliedert.
- Textberichte (u. a. X-Bericht, Z-Bericht/Archiv, Monatsbericht, Umsatz, Warenbestand, Kassenjournal, Kassensturz, Verkaufsstatistik, Bediener- und Stornoberichte) können direkt gedruckt werden.
- Benutzer kann pro Bericht wählen:
  - **58 mm Bondrucker**
  - **80 mm Bondrucker**
  - **A4 Bürodrucker**
  - konkreten installierten Windows-Drucker
- TOR speichert Drucker und Papierformat **pro Berichtstyp**.
- Berichtsdruck läuft über das vorhandene persistente TOR-Druckjournal und die begrenzte Hintergrund-Warteschlange. UI-Thread wird nicht absichtlich mit dem Windows-Spooler blockiert.
- PDF bleibt als eigener Weg erhalten.
- Unklare Berichtsdrucke erscheinen jetzt in der vorhandenen Druckwarteschlangen-Prüfung mit Berichtsname statt leerer Bonnummer.

## TOR POS Cloud
- Manueller Artikel-/Bestandssnapshot aus der Kasse enthält zusätzlich SKU, EAN/Barcode, Gruppe, Warengruppe, Einheit und Verkaufspreis.
- Cloud-Datenbank migriert bestehende `stock_items` additiv; bestehende Datenbank muss nicht gelöscht werden.
- Portal-Artikelübersicht zeigt diese zusätzlichen Produktinformationen.
- Cloud v0.4.2 R42: Node-Testlauf **9/9 bestanden**.

## Noch real zu prüfen
- In dieser Entwicklungsumgebung ist kein .NET-10-SDK installiert; deshalb wurde der Windows/Avalonia-Desktop hier **nicht kompiliert**.
- Auf dem TOR-Windows-Test-PC: `1-SETUP-ERSTELLEN.bat` ausführen.
- Danach echten 58-mm-/80-mm-Bondrucker und A4-Drucker testen; insbesondere lange X/Monats-/Warenbestandsberichte und Seiten-/Papierlänge prüfen.
- Scanner im Artikel- und Inventurfenster mit dem tatsächlich verwendeten HID-Scanner prüfen.
- Fiskal-/TSE-Produktionsfreigabe bleibt unverändert gesperrt, bis reale TSE-Abnahme erfolgt.
