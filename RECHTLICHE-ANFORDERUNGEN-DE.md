# TOR POS Pro – Rechtliche Anforderungen Deutschland
Stand: 05.09.2026

> Dieses Dokument ist eine technische Compliance-Checkliste und keine individuelle
> Steuer- oder Rechtsberatung.

## Rechtsrahmen

Für ein elektronisches/computergestütztes Kassensystem in Deutschland sind insbesondere
§§ 146, 146a, 146b, 147 AO sowie die KassenSichV relevant.

Offizielle Quellen:
- https://www.gesetze-im-internet.de/ao_1977/__146a.html
- https://www.gesetze-im-internet.de/kassensichv/
- https://www.bundesfinanzministerium.de/Content/DE/FAQ/FAQ-steuergerechtigkeit-belegpflicht.html
- https://www.bzst.de/DE/Unternehmen/Aussenpruefungen/DigitaleSchnittstelleFinV/digitaleschnittstellefinv_node.html

## Muss für den Produktivbetrieb vorhanden sein

### 1. Zertifizierte TSE
Jeder aufzeichnungspflichtige Geschäftsvorfall bzw. andere relevante Vorgang muss
über die zertifizierte TSE abgesichert werden. TOR plant Swissbit Hardware TSE 2 USB.

Benötigt werden u. a.:
- Vorgangsbeginn
- fortlaufende Transaktionsnummer
- Vorgangsart
- Vorgangsdaten
- Zahlungsart
- Vorgangsende/Abbruch
- Prüfwert
- eAS- und TSE-Seriennummer
- Signaturzähler

### 2. Unveränderte / nachvollziehbare Aufzeichnungen
Abgeschlossene Verkäufe dürfen im Programm nicht nachträglich überschrieben oder
gelöscht werden. Korrekturen müssen als nachvollziehbare Gegen-/Stornovorgänge
abgebildet werden.

TOR 0.7.19 sperrt UPDATE/DELETE auf abgeschlossenen Verkäufen programmintern per DB-Trigger.
Die echte Manipulationssicherheit ersetzt dies nicht: hierfür bleibt die TSE zwingend.

### 3. Belegausgabe
Der Beleg muss dem Kunden unmittelbar angeboten werden. Papier oder – mit Zustimmung –
elektronisch ist möglich.

Für einen produktiven Kassenbeleg sind u. a. erforderlich:
- vollständiger Unternehmername und Anschrift
- Ausstellungsdatum
- Vorgangsbeginn und -ende
- Menge / Art der Ware oder Leistung
- TSE-Transaktionsnummer
- Entgelt / Steuerbetrag / Steuersatz
- eAS- und TSE-Seriennummer
- Prüfwert
- Signaturzähler

QR-Code ist nach BMF-FAQ aktuell nicht gesetzlich zwingend.

### 4. DSFinV-K / Prüferexport
Für Kassen-Nachschau und Außenprüfung müssen Kassendaten standardisiert bereitgestellt
werden. TOR zielt auf DSFinV-K 2.4. Zusätzlich müssen TSE-Daten im vorgeschriebenen
TSE-Exportformat bereitgestellt werden.

### 5. Geparkte / offene Bons
Ein geparkter Bon ist nicht automatisch steuerlicher Umsatz. Er darf daher nicht als
abgeschlossener Verkauf in den Z-Umsatz eingehen.

Aber: Sobald durch Scannen/Bestellerfassung ein relevanter Vorgang beginnt, kann bereits
eine TSE-Transaktion erforderlich sein. Langanhaltende Vorgänge werden insbesondere als
"Bestellung" oder geeigneter "SonstigerVorgang" abgesichert und später mit dem
"Kassenbeleg" verknüpft.

Deshalb blockiert TOR 0.7.19 den Produktivbetrieb, solange Parken nicht real TSE-seitig
implementiert ist.

### 6. Kassensturzfähigkeit / Bargeldbewegungen
Einlagen und Entnahmen müssen nachvollziehbar erfasst werden. Der Soll-Kassenbestand
muss mit dem Ist-Bestand vergleichbar sein.

TOR 0.7.19 enthält eine append-only Test-Erfassung für EINLAGE / ENTNAHME.

### 7. TSE-Ausfall
Ausfallzeit und Grund müssen dokumentiert werden. Ein TSE-Ausfall darf nicht heimlich als
erfolgreiche Signatur behandelt werden. Für den späteren Produktivbetrieb ist ein
automatisches Ausfallprotokoll und eine eindeutige Belegkennzeichnung vorzusehen.

TOR 0.7.19 enthält ein separates Ausfall-Log und eine Fail-Safe-Schicht. Die
Produktivfreigabe erfordert dennoch Abnahme mit realer TSE und offiziellem SDK.

### 8. eAS-Seriennummer
Jede TOR-Installation erhält einmalig eine Hersteller-Seriennummer:
`TORPOS-<32 Hex-Zeichen>`.
Diese Seriennummer ist in TOR programmintern unveränderbar.

### 9. Mitteilung nach § 146a Abs. 4 AO
Das elektronische Aufzeichnungssystem ist grundsätzlich innerhalb eines Monats nach
Anschaffung bzw. Außerbetriebnahme elektronisch mitzuteilen. Das Verfahren läuft über
Mein ELSTER oder ERiC.

TOR 0.7.19 dokumentiert nur Status/Datum. Es übermittelt noch nicht an ELSTER/ERiC.

### 10. Aufbewahrung / Verfahrensdokumentation
Steuerrelevante Aufzeichnungen und Organisationsunterlagen müssen entsprechend den
gesetzlichen Aufbewahrungsfristen verfügbar und auswertbar bleiben. Eine
betriebsbezogene Verfahrensdokumentation ist vorzuhalten.

## Umsatzsteuer – Kiosk / Imbiss

Seit 01.01.2026 fallen Restaurant- und Verpflegungsdienstleistungen für Speisen
grundsätzlich unter 7 %, Getränke bleiben ausgenommen und regelmäßig 19 %.
Bei Kiosk-/Einzelhandelsartikeln ist der Steuersatz artikelbezogen zu bestimmen.

TOR darf deshalb den Steuersatz nicht allein aus "KIOSK" oder "IMBISS" ableiten.

## Status TOR POS Pro 0.7.19

Bereits vorbereitet:
- Windows Login / Admin-Rechte
- immutable eAS-Seriennummer
- append-only Audit-Log
- abgeschlossene Verkäufe programmintern nicht änder-/löschbar
- Bon-Parken separat vom Umsatz
- Z-Sperre bei offenen geparkten Bons
- Einlage / Entnahme Test-Erfassung
- Testbon mit deutlicher Kennzeichnung
- Belegstruktur für spätere TSE-Felder
- Recht & Fiskal Statusseite
- DSFinV-K Zielversion 2.4
- TSE-Ausfall-Log und Fail-Safe-Schicht
- TSE-TAR-Exportpfad über die offizielle Swissbit Runtime
- DSFinV-K-Vollständigkeitsprüfung (noch kein freigegebener Voll-Export)
- RSA-signierte kommerzielle Lizenzdatei, getrennt von der Fiskalfreigabe

Noch zwingend offen:
- offizielles Swissbit SDK / reale TSE
- Start/Finish/ggf. UpdateTransaction mit realer TSE vollständig abnehmen
- echte Kassenbeleg-/Bestellung-Verknüpfung
- DSFinV-K 2.4 Export + Validierung
- TSE TAR Export mit Realhardware validieren
- produktiver §6-KassenSichV-Belegtest
- Pfand-Steuerlogik fachlich finalisieren
- Storno/Rückgabe als eigene unveränderbare Fiskalvorgänge
- reale Z-/Tagesabschluss-Implementierung auf Fiskaldatenbasis

**Folge:** TOR POS Pro 0.7.19 bleibt TESTBETRIEB und ist noch nicht für produktive
steuerliche Aufzeichnungen freigegeben.
