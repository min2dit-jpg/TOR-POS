# TOR POS Pro – DSFinV-K 2.4 Implementierungsplan

Ziel: standardisierter Prüferexport nach der vom BZSt veröffentlichten DSFinV-K.

Stand R131: implementiert (`DsfinvkExportService`, `DsfinvkClosingBuilder`), siehe
R131-CHANGELOG.md für die Abbildung der TOR-Daten und die offen genannten Lücken. Nicht
produktiv freigegeben - die Validierung unten steht noch aus (Punkte 2, 3, 5 sind durch
R131ReviewTests abgedeckt, 1, 4, 6, 7 benötigen Prüfsoftware bzw. reale TSE).

## Muss aus TOR exportierbar werden
- Kasseneinzelbewegungen
- Stammdaten Kasse/Software
- Artikel-/Steuerinformationen soweit DSFinV-K erforderlich
- Zahlarten
- Kassenabschlüsse
- Referenzen zu TSE-Transaktionen
- geparkte/langanhaltende Vorgänge gemäß Bestellung/Referenzlogik
- Storno/Rückgabe
- Einlagen/Entnahmen entsprechend Geschäftsvorfall-Typen
- index.xml und vorgeschriebene CSV-Struktur/Reihenfolge

## Validierung vor Freigabe
1. Export gegen offizielle BZSt-Muster/Definition prüfen.
2. Mehrere Steuersätze 7/19 testen.
3. BAR/KARTE/Mischfälle testen.
4. Parken über Tagesgrenze testen.
5. Storno/Rückgabe testen.
6. TSE-Referenzen gegen reale Swissbit-Transaktionen prüfen.
7. Kassen-Nachschau-Testexport auf separatem PC ohne TOR-Installation lesbar machen.
