# TOR POS – Verfahrens-/Systemdokumentation Statusmatrix

Stand: R180 · 2026-09-21

Zweck dieser Datei ist **nicht**, die endgültige Verfahrensdokumentation zu ersetzen. Sie bildet den aktuellen technischen Nachweisstand ab und trennt bereits im Code bestätigte Funktionen von noch offenen Hardware-, Fiskal- und Organisationsnachweisen.

Status:
- **GEPRÜFT** – im aktuellen R180-Code bzw. in den zugehörigen Tests nachgewiesen
- **TEILWEISE** – wesentliche Implementierung vorhanden, aber Restprüfung/Dokumentation offen
- **EXTERNER NACHWEIS** – Softwarepfad vorhanden, reale Hardware-/Fiskalabnahme fehlt
- **OFFEN** – für die finale Verfahrensdokumentation noch systematisch zu erfassen

| Nr. | Dokumentationsbereich | Status | Aktueller technischer Nachweis |
|---:|---|---|---|
| 1 | Zweck und Geltungsbereich | TEILWEISE | Systemgrenze Desktop/Cloud dokumentiert; finaler Betreiber-/Mandantenbezug noch zu formulieren |
| 2 | Produkt- und Versionsidentität | GEPRÜFT | `TorRelease`, Manifest, App-Projekt, Installer, README und CHANGELOG werden per CI abgeglichen |
| 3 | Systemarchitektur | GEPRÜFT | Core / Application / Infrastructure / App / Cloud getrennt dokumentiert |
| 4 | Installationsverfahren | TEILWEISE | Inno-Setup, Program-Files-Installation und bestehende Installationserkennung vorhanden |
| 5 | Erstinbetriebnahme | TEILWEISE | Edition- und Admin-Initialisierung vorhanden; finale Betreiberanweisung noch zu erstellen |
| 6 | Benutzer und Rollen | GEPRÜFT | Admin + Mitarbeiter, Funktionsrechte und Training-Rolle im Code vorhanden |
| 7 | Authentifizierung | GEPRÜFT | PBKDF2-SHA256, 600.000 Iterationen, Salt/Hash, Lockout und Pflichtänderung von Startdaten |
| 8 | Berechtigungskonzept | GEPRÜFT | Sale, Discount, Storno, Parken, Kassenbewegung, Z, Artikel, Bon-Historie, Training |
| 9 | Artikelstammdaten | TEILWEISE | Artikel, Nummern, EAN/SKU, Gruppen, Preise, Bestand vorhanden; finale Bedienerdoku offen |
| 10 | Warengruppen und Steuersätze | TEILWEISE | Warengruppe als fachliche VAT-Basis implementiert; finale Fachprüfung separat |
| 11 | Menü-/Bundle-Preislogik | TEILWEISE | Komponenten-/VAT-Snapshots und gewichtete Preis-/Rabattlogik vorhanden; finale Fachabnahme offen |
| 12 | Angebote / Promotionen | GEPRÜFT | Zeitraum, Prozent, cent-exakte Verteilung und Z-/Betriebstagbezug getestet |
| 13 | Lager / Inventur | TEILWEISE | Bestand, Mindestbestand, Einkaufspreis und Inventurpfade vorhanden; Prozessbeschreibung offen |
| 14 | Scanner / Gewichtsartikel | EXTERNER NACHWEIS | HID-Scannerpfad R180 technisch gehärtet (sichtbares Fokusziel, tolerantere Timing-Logik); reale Scanner-Hardware und echte Waagenanbindung separat abzunehmen |
| 15 | Verkauf und Checkout | GEPRÜFT | unveränderlicher Checkout-Snapshot, zentrale Zahlungsvorbereitung und Commit-Grenzen |
| 16 | Storno- und Retourenverfahren | GEPRÜFT | Tagesregel, SALE-only, Refund-vor-DB, Doppelrefund-Schutz, Mengenverfolgung, TSE/DSFinV-K-Abbildung |
| 17 | Zahlungsarten Bar/Karte/Gemischt | GEPRÜFT | Cash/Card/Mixed-Anteile, Journal und Reconciliation vorhanden |
| 18 | Kartenterminal-Sicherheit | GEPRÜFT | SENT/UNKNOWN/APPROVED-Zustände, kein automatischer Replay bei unklarem Status |
| 19 | Kartenerstattung | GEPRÜFT | persistenter `card_refund_attempts`-Lock verhindert zweiten Refund bei UNKNOWN |
| 20 | Bestellung / Parken | TEILWEISE | Bestellung, Änderungen, Storno, Park-/Pickup-Nummern und TSE-Vorgänge vorhanden; reale Fiskalabnahme offen |
| 21 | TSE-Architektur | TEILWEISE | direkte Swissbit-WORM-Integration plus fiskaltrust Middleware/SCU-Code auf `main` |
| 22 | TSE Start/Finish | EXTERNER NACHWEIS | Softwarepfad vorhanden; reale zertifizierte TSE-E2E-Abnahme noch offen |
| 23 | TSE-Ausfall | GEPRÜFT | Ausfall wird dokumentiert; fehlende TSE-Werte werden nicht erfunden oder nachträglich ersetzt |
| 24 | Beleg / Pflichtfelder | TEILWEISE | zentrale Feldprüfung und Drucksperren vorhanden; §6-/Hardware-Abnahmeflag bleibt geschlossen |
| 25 | TSE-QR | EXTERNER NACHWEIS | Anhang-I-Payload wird nur aus vollständigen Daten gebildet; reale QR-Prüfung offen |
| 26 | Digitalbeleg | TEILWEISE | digitaler Belegpfad vorhanden; finale Betriebs-/Datenschutzbeschreibung noch offen |
| 27 | Bon-Historie | GEPRÜFT | Tagesansicht ohne 201er Mehrtageslimit; Datums-/Zahlart-/Bonnummerfilter getestet |
| 28 | X-Bericht | GEPRÜFT | Zwischenbericht ohne Abschluss/Z-Zähleränderung |
| 29 | Z-Bericht / Tagesabschluss | GEPRÜFT | Zeitraum letzter Abschluss → neuer Abschluss; immutable Z-Archiv und Daily Closing |
| 30 | Kassenjournal | GEPRÜFT | Belege und Kassenbewegungen mit Bedienerbezug auswertbar |
| 31 | Kassenbewegungen | TEILWEISE | Einlage/Entnahme, Audit und TSE-Pfad vorhanden; reale TSE-Abnahme offen |
| 32 | DSFinV-K 2.4 Export | TEILWEISE | Von/Bis, Preflight, vollständige Z-Zeiträume, USB/E-Mail-ZIP vorhanden; externer Validatornachweis offen |
| 33 | DATEV / Steuerberater-Übergabe | TEILWEISE | DATEV-/Exportpfade vorhanden; direkte produktive API-/Vertragsintegration separat zu qualifizieren |
| 34 | Drucker | EXTERNER NACHWEIS | Epson/Star-Erkennung, RAW-Protokolle, Queue-Sicherheit vorhanden; konkrete Hardwareabnahme offen |
| 35 | Kassenschublade | EXTERNER NACHWEIS | R180 vereinheitlicht Aktivierung, gespeicherten DK-Ausgang und Zahlungsweg; Epson/Star-Protokolle vorhanden, reale Hardwareabnahme bleibt offen |
| 36 | Datensicherung | GEPRÜFT | DB-Snapshot + Assets + PrintJobs + Manifest/Hashes; täglicher Scheduler konfigurierbar |
| 37 | Wiederherstellung | GEPRÜFT | neues Ziel, Pfadschutz, Manifest-/Hash-Prüfung und DB-Lesetest |
| 38 | Audit / Unveränderbarkeit | GEPRÜFT | Audit append-only; Sales, SaleItems, Bediener, Z-Archiv und Daily Closings gegen UPDATE/DELETE geschützt |
| 39 | Software-Update | TEILWEISE | HTTPS/SHA-256/Authenticode-Prüfung implementiert; Signer-Thumbprint noch nicht produktiv hinterlegt |
| 40 | Test, Freigabe und Nachweise | EXTERNER NACHWEIS | 1118 Safety-Checks; physische TSE-E2E-, DSFinV-K-, §6-, Pfand- und unabhängige Fiskalprüfung bleiben geschlossen |

## §16 Storno- und Retourenverfahren – präzisierter R180-Stand

### 16.1 Grundsätze

- TOR POS erlaubt BON STORNO und Teilretoure im aktuellen Implementierungsstand nur am Verkaufstag.
- Die Gegenbuchung ist nur gegen einen regulären `SALE`-Ursprungsbeleg zulässig.
- Bei einem Kartenanteil muss die Terminal-Erstattung bestätigt sein, bevor die Storno-/Retourenbuchung in der Verkaufsdatenbank angelegt wird.

Die Verkaufstagsbegrenzung wird als **TOR-POS-Systemregel** dokumentiert und nicht als allgemeine gesetzliche Aussage formuliert.

### 16.2 Schutz gegen Doppelstorno / Doppelerstattung

- Vollständiger Storno nach bereits erfolgter Teilretoure ist gesperrt.
- Teilretoure nach vollständigem Storno ist gesperrt.
- Mehrere Teilretouren sind nur bis zur noch verfügbaren Restmenge möglich.
- Ein ungeklärter Kartenrefund bleibt persistent gesperrt und verhindert einen zweiten automatischen Refund-Versuch.

### 16.3 Mengen- und Betragsverfolgung

Bereits retournierte Mengen werden je Ursprungsposition summiert und von der ursprünglichen Menge abgezogen.

Beispiel: 10 Stück verkauft, 4 Stück bereits retourniert → höchstens 6 Stück verbleiben für weitere Teilretouren.

Bei anteiligen/kampagnenbeeinflussten Retouren verwendet TOR die kumulative Cent-Verteilung, damit die Summe aller Teilretouren den ursprünglichen Betrag nicht überschreitet und die letzte Teilretoure den verbleibenden Cent-Rest übernimmt.

### 16.4 Crash-Sicherheit

Der Refund-Lock wird vor dem Terminal-Refund angelegt. Nach bestätigtem Refund bleibt er bestehen, bis die zugehörige Storno-/Retourenbuchung erfolgreich in der Datenbank committed wurde.

Ein Crash oder Datenbankfehler zwischen Terminal-Refund und Gegenbuchung führt daher nicht automatisch zu einem zweiten Refund.

### 16.5 TSE-Signatur und DSFinV-K-Abbildung

- Storno und Retoure werden als eigener Vorgang mit `Kassenbeleg-V1` abgesichert.
- Der Vorgangstyp bleibt `Beleg`.
- Umsatz- und Zahlungsbeträge werden für Storno/Retoure mit umgekehrtem Vorzeichen abgebildet.
- Vollstorno wird im DSFinV-K-Datensatz zusätzlich mit `BON_STORNO = 1` gekennzeichnet.
- Storno und Retoure werden über `Bon_Referenzen` mit dem ursprünglichen Beleg bzw. dessen Kassenabschluss verknüpft.
- Die Referenz auf den Ursprungsbeleg ist **nicht Bestandteil der TSE-processData**; sie wird im DSFinV-K-Export geführt.

## Offene Freigabepunkte

Die Statusmatrix darf erst in eine finale Betreiber-Verfahrensdokumentation überführt werden, wenn die noch offenen realen Nachweise abgeschlossen und die konkrete Kunden-/Kassenumgebung (Betreiber, Standort, eingesetzte TSE, Drucker, Terminal, Backup-Ziel, Verantwortlichkeiten) eingetragen ist.

Die technischen Produktions-Gates bleiben in R180 unverändert geschlossen.
