# TOR POS Pro R50 – IMBISS Sortiment + Abholnummer Training

Basis: R49 Combo / Küchendrucker / Mehrsprachigkeit / ORDER.

## IMBISS-Stammdaten

- IMBISS-Starterkatalog wird **nur nach Auswahl IMBISS** angelegt.
- Neue IMBISS-Warengruppen: Döner, Burger, Fingerfood, Pizza; Getränke bleibt als gemeinsame Warengruppe nutzbar, die neuen Starterartikel sind IMBISS-only.
- Döner: Döner, Big Döner, Dürüm Döner, Döner Box Klein/Groß.
- Burger: Hamburger, Cheeseburger, Chickenburger, Doppelburger, Chili-Cheeseburger.
- Fingerfood: Pommes Klein/Groß, Nuggets, Wings, Mozzarella Sticks, Onion Rings, Chili Cheese Nuggets.
- Pizza: 9 Sorten, jeweils zwei wählbare Größen: Klein 26 cm / Groß 32 cm.
- Getränke: Coca-Cola, fritz-kola/fritz-limo, Ayran, Wasser sowie gängige Berliner/Deutschland-Biere in 0,33-l- und 0,5-l-Reihenfolge.
- Neue Starterartikel erhalten die automatische Artikelnummer aus der R48-Sequenz.
- Starterpreise sind editierbare Musterwerte; Barcode, Einkaufspreis, Bestand und Mindestbestand bleiben kundenspezifisch.
- Pfand startet bei den neu angelegten Getränke-Musterartikeln bewusst mit 0 Cent und muss passend zur tatsächlich verwendeten Dose/Flasche gesetzt werden; so wird kein falscher Verpackungs-Pfandwert erzwungen.
- Die Vorlage wird pro Datenbank nur einmal angewendet und überschreibt danach keine vom Kunden geänderten Preise oder Bestände.

## KIOSK/IMBISS-Trennung

- `categories.edition_scope` und `products.edition_scope` wurden additiv ergänzt.
- Neue Artikel/Warengruppen werden der beim Login gewählten Edition zugeordnet.
- IMBISS-Starterartikel erscheinen nicht in KIOSK, Inventur, CSV-Export oder Cloud-Bestand der KIOSK-Sitzung.
- Alte R49-Demo-Warengruppen Döner/Pizza werden ohne Löschen als IMBISS-Daten markiert.

## Abholnummer / ORDER

- Abholnummer-Einstellung in `Einstellungen → Bedienfunktionen` als eigener, deutlich sichtbarer Abschnitt.
- OFF / SALE / ORDER bleibt auswählbar.
- ORDER kann nun auch mit dem TRAINING-Benutzer getestet werden.
- Trainingsbestellungen erhalten eine getrennte Tagessequenz und werden von echten offenen Bestellungen getrennt gehalten.
- Trainingsbestellungen blockieren keinen echten Z-Abschluss.

## Unverändert

- Bon, Küchenbon, Abholschein, Berichte und fiskalrelevante Ausdrucke bleiben Deutsch.
- SumUp-Code wurde in R50 nicht funktional geändert.
- Fiskale Produktionsfreigabe bleibt gesperrt.
