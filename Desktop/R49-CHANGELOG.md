# R49 – IMBISS Menü/Combo · Küchendrucker · UI-Sprache · Bestell-/Abholnummer

Basis: vom Benutzer hochgeladene und bestätigte `R48.1-StabilityReview`-Fassung.

## 1. Menü / Combo
- IMBISS-Artikel können direkt im Artikel-Editor als Menü/Combo gepflegt werden.
- Beispiel: `Döner Menü = Döner + 1x Pommes + 1x Getränk`.
- Der Verkaufspreis bleibt am Menü-Artikel.
- Im Warenkorb/Bon bleibt der Menü-Artikel der sichtbare Verkaufsartikel.
- Beim realen Verkaufscommit wird der Bestand der hinterlegten Komponenten reduziert; der Menü-Elternartikel wird dabei nicht zusätzlich abgezogen.
- TOR Cloud erhält für den Lagerabgleich `stock_consumption`, während der sichtbare Umsatzartikel unverändert bleibt.
- Gemischte MwSt.-Menüs werden nicht mit erfundener Fiskallogik behandelt; Produktivfreigabe bleibt bis zur fachlichen/fiskalen Validierung gesperrt.

## 2. Küchendrucker / 2. Drucker
- Unter `Einstellungen → Geräte` kann IMBISS einen separaten Windows-Küchendrucker aktivieren, auswählen und testen.
- Automatische Küchenbons können separat ein-/ausgeschaltet werden.
- Küchenaufträge laufen über dieselbe persistente, timeout-geschützte TOR-Druckwarteschlange wie andere sichere Druckaufträge.
- Küchenbon enthält Bestell-/Abholnummer, Parkbon, Zeit, Bediener und Artikel; Menübestandteile werden eingerückt mit ausgegeben.
- Küchenbon ist immer Deutsch und klar `KEIN STEUERBELEG`.

## 3. Programmsprache
- Neue Einstellung `Programmsprache`: `DE`, `TR`, `EN`.
- Die Übersetzung betrifft ausschließlich die Bedienoberfläche.
- Bon, Küchenbon, Abholschein, Berichte, Fiskal-/TSE-/DSFinV-K-relevante Ausdrucke bleiben bewusst Deutsch.
- R49 lokalisiert die wichtigsten täglichen Kassen-, Login-, Einstellungen-, Artikel- und Bestelltexte. Weitere technische Dialoge können schrittweise in denselben UI-Layer aufgenommen werden.

## 4. Abholnummer / Bestellablauf – wählbar
Unter `Einstellungen → Bedienfunktionen → Abholnummer / Bestellablauf`:
- `OFF`: keine Abholnummer.
- `SALE`: bisheriger Ablauf; Nummer wird erst beim finalen Kassieren vergeben.
- `ORDER`: gewünschter IMBISS-Ablauf:
  1. Artikel aufnehmen.
  2. `BESTELLUNG ANNEHMEN (F3)`.
  3. TOR vergibt sofort die Tages-Abholnummer und speichert die Bestellung als offen.
  4. Optional: Küchenbon an 2. Drucker + Abholschein für den Kunden.
  5. Es entsteht **noch kein finaler Verkaufsbon**.
  6. Wenn die Bestellung aufgerufen wird: `OFFENE BESTELLUNGEN → BESTELLUNG AUFRUFEN`.
  7. Erst `BAR` / `KARTE` erzeugt später den finalen Bon/Verkauf und verwendet dieselbe Abholnummer.
- Die Abholnummer bleibt rein operativ und ist niemals Ersatz für die fiskalische Bonnummer.
- Ein angenommener Auftrag behält seine Nummer auch bei späterer Änderung.

## 5. Sicherheit / Abgrenzung
- SumUp-Verbindungsdateien wurden gegenüber R48.1 nicht verändert (SHA-256 Vergleich im Verification-Ordner).
- R49 schaltet keine fiskale Produktion frei; `TEST_ONLY` / `production_allowed=false` bleibt erhalten.
- Automatischer Artikelnummern- und R48.1-Stabilitätsstand bleiben Basis.

## Verifikation in dieser Erstellungsumgebung
- Cloud: `npm run check` erfolgreich.
- Cloud: 16/16 Tests erfolgreich, inklusive Combo-Lagerverbrauch.
- Desktop: AXAML/XML-Parsing und C#-Delimiter-/Strukturchecks erfolgreich.
- R49 Desktop Safety-Testfälle wurden ergänzt (Combo-Persistenz, ORDER-Abholnummer, UI-Sprachen, persistente Küchen-/Abholschein-Druckjobs).
- Ein echter .NET-10/Avalonia-Build ist in dieser Umgebung nicht möglich, da kein .NET SDK installiert ist. Der verbindliche Compile-/Windows-Test erfolgt mit `Desktop\1-SETUP-ERSTELLEN.bat` auf dem Build-PC.
