# TOR POS R59 · Druckererkennung + Berichte per E-Mail

Version: **0.7.33.590**

## Neu

- BAR/KARTE prüft vor Beginn der Zahlung, ob der konfigurierte Bondrucker von Windows erkannt und als bereit gemeldet wird.
- Wenn der Bondrucker fehlt/offline ist, wird die Zahlung zunächst abgebrochen und eine klare Warnung gezeigt.
- Bewusstes Fortfahren ohne Bondruck ist nur über **OHNE DRUCKER FORTFAHREN** möglich; dann wird für genau diesen Vorgang kein Druckauftrag gestartet.
- Jeder Bericht kann über **PDF SPEICHERN** an einen frei gewählten Speicherort geschrieben werden.
- Berichtszentrum enthält zusätzlich Monatsumsatz, Warenbestand und Z-Archiv.
- **ALLE BERICHTE ALS PDF SPEICHERN** exportiert ein komplettes PDF-Paket in einen gewählten Ordner.
- Neue Einstellungen **Berichte & E-Mail**:
  - Empfänger-E-Mail
  - Absender-E-Mail
  - SMTP-Server / Port / TLS
  - SMTP-Benutzer
  - SMTP App-Passwort (mit Windows-Benutzerschutz verschlüsselt gespeichert)
  - monatlicher Versandtag (1–28)
  - Versandzeit (Standard 00:15)
- Monatlicher Versand sendet automatisch den abgeschlossenen Vormonat und holt einen verpassten Versand nach Programmstart nach.
- Fehlgeschlagener Versand setzt keinen Erfolgsmarker; erneuter Versuch nach 15 Minuten.
- Monatspaket enthält: Monatsübersicht, Kassenjournal, Verkaufsstatistik, Bedienerabrechnung, Stornobericht, vorhandenes Z-Archiv und aktuellen Warenbestand-Snapshot.
- E-Mail-/PDF-Erstellung erzeugt **keinen Z-Bericht, keinen Tagesabschluss und keinen Kassenabschluss**.

## Hinweis

Der Warenbestand im Monatspaket ist ein aktueller Snapshot zum Erstellungszeitpunkt. TOR POS erfindet keinen rückwirkenden Monatsendbestand, solange keine historischen Bestands-Snapshots gespeichert werden.
