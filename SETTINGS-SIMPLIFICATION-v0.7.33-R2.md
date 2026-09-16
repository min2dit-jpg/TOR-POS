# TOR POS v0.7.33 – Einstellungen R2

Ziel: Ein Betreiber soll die Kasse bedienen und normale Geschäftsangaben ändern können, ohne Netzwerk-, Treiber-, ZVT-, TSE- oder Buchhaltungsparameter verstehen zu müssen.

## Betreiber sichtbar
- Kasse & Bedienung: Kassenname, Startansicht, Darstellungsgröße, wenige Verkaufsfunktionen, Bestandswarnung, Z-Druck.
- Firma & Bon: Firmendaten, Bontexte, automatischer Bondruck.
- Artikel & Steuern: 19/7-Grundwerte und Standard für neue Artikel.
- Zahlung: Bar/Karte/Rechnung und sichtbare Bezeichnungen.
- Geräte: Drucker aktivieren/suchen/testen, Kassenlade, Kundenanzeige, A4-Drucker.
- Personal und Datensicherung.

## Passwortgeschützt / Techniker
- Kassennummer, Theme, Touch-Raster, Währung/Startgeld, Stornogründe und technische Kassenlogik.
- ZVT Terminalprofil, IP, Port, Timeouts und Registrierungstest.
- Windows-Druckername, Auto-Cut, DK-Anschluss, COM-Port, Scanner HID/COM und Scanner-Wartezeit.
- DATEV-Zuordnungen.
- TSE, Fiskalstatus, Lizenzierung und Systemdiagnose.

## Sicherheitsregel
Technikerzugang bleibt nach 5 Fehlversuchen 5 Minuten gesperrt. Fehlversuche und Sperrzeit werden persistent gespeichert, sodass ein Neustart die Sperre nicht aufhebt.
