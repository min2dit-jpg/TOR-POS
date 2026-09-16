# Verfahrensdokumentation – Vorlage TOR POS Pro
Stand: 04.09.2026

Diese Vorlage muss für den konkreten Betrieb ergänzt und aktuell gehalten werden.

## 1. Betrieb
- Firma:
- Inhaber:
- Anschrift:
- Steuernummer:
- Betriebsstätte:

## 2. Kassensystem
- Hersteller: TOR Kassensysteme
- Software: TOR POS Pro
- Version:
- KIOSK / IMBISS:
- eAS-Seriennummer:
- Inbetriebnahme:
- Außerbetriebnahme:
- Setup-Datei / SHA-256:
- TOR Kunden-Nr.:
- Installations-ID (Lizenz):
- PC-Gerätecode (Lizenz):
- Lizenz-ID / Gültigkeit:
- Windows-Version / Gerätename:

## 3. TSE
- Hersteller: Swissbit
- Produkt:
- Bauform:
- TSE-Seriennummer:
- BSI-Zertifizierungs-ID:
- Inbetriebnahme:
- Zertifikatsablauf:
- Austauschdatum / Grund:
- SDK-/WORM-API-Version:
- SDK-Datei / SHA-256:
- TSE-Client-ID:
- letzter Self-Test:

## 4. Bedienung und Berechtigungen
- Admin:
- Kassierer:
- Passwort/PIN-Verfahren:
- Storno-Berechtigungen:
- Rabatt-Berechtigungen:
- Z-Abschluss-Berechtigungen:
- Verfahren für Benutzeranlage/-sperrung:
- regelmäßige Rechteprüfung:

## 5. Geschäftsvorfälle
Beschreiben:
- Verkauf BAR
- Verkauf KARTE / ZVT
- Storno / Rückgabe
- Rabatt
- Parken / Wiederaufnahme
- Einlage
- Entnahme
- Kassenladenöffnung
- Ausfallverfahren

## 6. Tagesabschluss / Kassensturz
- Startgeld:
- Einlagen/Entnahmen:
- Soll-Bestand:
- Ist-Bestand:
- Differenzbehandlung:
- Z-Abschluss:

## 7. Beleg
- Bondrucker:
- Papierbreite:
- Pflichtangaben:
- elektronischer Beleg (falls genutzt):
- QR-Code (falls genutzt):

## 8. Datenexport
- DSFinV-K Version:
- Exportweg:
- TSE-TAR-Export:
- Speicherort:
- Verantwortliche Person:
- letzter vollständiger Probeexport:
- Prüfergebnis / verwendeter Validator:
- Aufbewahrungsort der Exportprotokolle:

## 9. Datensicherung
- Sicherungsintervall:
- Sicherungsort:
- Aufbewahrung:
- Wiederherstellungstest:
- Verantwortliche Person:

## 10. Softwareänderungen
Für jedes Update:
- Datum:
- alte Version:
- neue Version:
- Änderung:
- Installateur:
- Prüfung nach Update:
- Datenbanksicherung vor Update:
- Freigabe-/Abnahmeprotokoll:
- geänderte TSE-/DSFinV-K-relevante Funktionen:

## 11. TSE-/Systemausfälle
Je Ausfall:
- Beginn:
- Ende:
- Ursache:
- betroffene Kasse:
- Ersatzverfahren:
- Nachweis / Serviceticket:

## 12. §146a-Mitteilung
- Meldestatus:
- Meldedatum:
- Übermittlungsweg:
- Nachweis/Aktenablage:

## 13. Systembeschreibung und Datenfluss

Dokumentieren:
- Eingabe/Scan → Warenkorb → Zahlart → unveränderbarer Verkauf
- TSE Start/Update/Finish und gespeicherte Rückgabefelder
- Bon-/Kopiedruck und Verhalten bei ausgeschaltetem Automatikdruck
- ZVT-Kommunikation und Behandlung eines ungeklärten Timeouts
- Parken/Wiederaufnahme und fiskaler Vorgangstyp
- Audit-Log, TSE-Ausfall-Log, TAR und DSFinV-K

## 14. Internes Kontrollsystem

- tägliche Kontrollen:
- Kassensturz/Differenzen:
- Storno- und Rabattkontrolle:
- Kontrolle offener geparkter Bons:
- TSE-Status-/Zertifikatskontrolle:
- Lizenz- und Versionskontrolle:
- Verantwortliche Personen und Vertretung:

## 15. Drittanbieter und Verträge

- Swissbit TSE Produkt/Zertifikat/Bezugsquelle:
- Swissbit SDK-/Runtime-Nutzungsrecht:
- Bondruckertreiber/Version:
- Kartenterminal/Netzbetreiber/ZVT-Freigabe:
- Liste Open-Source-Komponenten/Notices:
- Ablage der Verträge und Lizenznachweise:

## 16. Dokumentenlenkung

- Verantwortlicher für diese Dokumentation:
- Dokumentversion:
- Freigabedatum:
- Änderungsverlauf:
- Speicherort / Zugriffsschutz:
- regelmäßiger Prüftermin:
