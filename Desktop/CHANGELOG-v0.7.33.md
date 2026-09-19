> **HISTORISCHES DOKUMENT.** Dieses Dokument entstand in einer früheren Phase von v0.7.33 und ist nicht der aktuelle Release-Index. Aktueller Stand: siehe `../CHANGELOG.md` und `src/TorPos.Core/ReleaseInfo.cs`.

# TOR POS v0.7.33 – Änderungsliste

## Bedienung
- Einstellungen auf 8 verständliche Hauptbereiche reduziert.
- Erweitert / Techniker aus dem normalen Bedienweg getrennt.
- Techniker-Zugang mit Service-Passwort 4909 und 5/5-Minuten-Sperre.

## Sicherheit
- Benutzer-Login: 5 Fehlversuche -> 5 Minuten Sperre (Passwort und PIN gemeinsam).
- Sperrstatus wird in SQLite gespeichert und über Neustarts hinweg beachtet.
- Neue Mitarbeiter-Slots sind standardmäßig deaktiviert und müssen vor Aktivierung mit eigenem Passwort + PIN konfiguriert werden.
- Legacy-Mitarbeiter mit unverändertem 1234/1234 werden beim Upgrade sicher erkannt und deaktiviert.

## Fiskal / Bon
- KDV/MwSt.-Gruppen werden beim Rabatt proportional verteilt; die letzte Gruppe übernimmt die Rundungsdifferenz, damit die Gruppensumme exakt dem Bon-Brutto entspricht.

## Prüfung
- Quellcode-Sanity-Checks durchgeführt.
- In dieser Umgebung ist kein .NET SDK installiert; ein echter Windows/Avalonia-Build muss auf dem Build-PC erfolgen.

## Einstellungen Phase 2 – Bedienkomfort / Techniker-Trennung
- Normale Einstellungen auf Betreiberaufgaben reduziert.
- Kasse & Bedienung zeigt nur Start, Anzeige und täglich benötigte Funktionsschalter.
- ZVT-IP, Port, Timeout und Terminalprofile vollständig in Erweitert / Techniker verschoben.
- Windows-Druckername, DK-Anschluss, Kundenanzeige-COM-Port, A4-Systemdrucker und Scannerprotokoll in Erweitert / Techniker verschoben.
- DATEV-Konten/Kennzeichen aus dem normalen Steuerbereich entfernt und in Techniker verschoben.
- Touch-Raster, Kassennummer, Theme-Feinabstimmung und Stornogründe in Techniker verschoben.
- Geräte-Seite für Betreiber auf Aktivieren → Suchen → Testen reduziert.
- Techniker-Schutz: 5 Fehlversuche → 5 Minuten Sperre; Sperrstatus bleibt über Programmneustart erhalten.

## Frontend / Sales Flow R3
- Editionsabhängige Leerbon-Hinweise für KIOSK und IMBISS.
- Checkout-Aktionen nur aktiv, wenn ein Bon kassierbar ist.
- Einstellungs-Hauptmenü auf die neuen 8 Gruppen reduziert.
- STAMMDATEN als klarere Bezeichnung im Hauptmenü.
- Zahlungsbuttons visuell als Abschlussaktion hervorgehoben.


## R6 – Warengruppen-Farben
- Jede Warengruppe kann eine eigene Kachelfarbe bekommen.
- 12 Schnellfarben plus eigener HEX-Wert.
- Farbe wird dauerhaft in SQLite gespeichert.
- Neue Warengruppen bekommen möglichst automatisch eine noch unbenutzte Farbe.
- Hauptkasse rendert die Warengruppen-Taste mit der gespeicherten Farbe.

## R17 Hauptkasse Safe Layout Hotfix
- R16 numpad structural Grid conversion reverted.
- R15 working UniformGrid/payment structure retained.
- Only safe sizing/layout adjustments kept for more product space and a more compact header.
