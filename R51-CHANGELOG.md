# TOR POS Pro R51 – IMBISS Bilder · Login-Sprache · Stammdaten-Löschen

Basis: R50 IMBISS Sortiment + Abholnummer.

## Anmeldung / Programmsprache
- Deutsch, Türkçe und English können direkt auf der Anmeldung gewählt werden.
- Die Auswahl wird als `ui.language` gespeichert und gilt danach für die Operator-Oberfläche.
- Belege, Küchenbons, Abholscheine, Berichte und fiskale Ausgaben bleiben bewusst Deutsch.

## IMBISS Warengruppen korrigiert
- `Speisen`: Döner, Burger, Fingerfood, Pizza.
- `Getränke`: Getränke.
- R50-Starterartikel werden beim einmaligen R51-Template-Lauf in die richtige Warengruppe verschoben, ohne Kundenpreise, EAN, Bestand oder eigene Bilder zu überschreiben.
- Alte KIOSK-Demogruppen `Schnellwahl` und `Snacks` werden im IMBISS ausgeblendet.
- Die alte Demo-Cola bleibt KIOSK-spezifisch; die R51-Getränkeartikel bleiben IMBISS-spezifisch.

## Produktbilder
- 57 lokale Starterbilder unter `Assets/ImbissProducts` eingebunden.
- Die Bilder werden beim IMBISS-Template in den TOR-Produktbildspeicher kopiert und den Starterartikeln zugeordnet.
- Keine Netzabhängigkeit im Verkauf.
- Eigene Kundenbilder werden nie automatisch überschrieben.
- Artikelverwaltung zeigt eine Bildvorschau; vorhandene Kassen-Produktkacheln verwenden `image_path` wie bisher.

## Sicheres Löschen von Stammdaten
- Admin-Aktionen: `GRUPPE LÖSCHEN`, `WARENGRUPPE LÖSCHEN`, `ARTIKEL LÖSCHEN`.
- Technisch als Soft-Deaktivierung implementiert. Historische Verkäufe/Fiskaldaten bleiben erhalten.
- Betroffene aktive Stammdaten verschwinden aus Kasse, Inventur und Cloud-Bestand; Audit-Eintrag wird geschrieben.

## Unverändert
- R50 ORDER/Abholnummer-Ablauf bleibt unverändert.
- SumUp-Quellen bleiben unverändert.
- `TEST_ONLY`, TSE-Produktionssperre und DSFinV-K-Sicherheitsgrenzen bleiben unverändert.
