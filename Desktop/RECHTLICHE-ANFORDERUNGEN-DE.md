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

Stand R131: Der Export schreibt je Kassenabschluss alle 20 Dateien mit der unveränderten
offiziellen index.xml des BZSt und prüft jeden Wert gegen deren Beschreibung. Was TOR
noch nicht erfasst, wird im Export-Protokoll ausdrücklich genannt. Seit R132-R136 erfasst:
Stammdaten je Abschluss, TSE-Stammdaten aus dem TSE-Export, Im Haus/Außer Haus,
Einlagen/Entnahmen mit Geschäftsvorfall und TSE, Training als AVTraining, Vorgangsbeginn
(BON_START), TSE-Startzeit (TSE_TA_START) und abgebrochene Vorgänge (AVBelegabbruch). Eine Abnahme mit
Prüfsoftware und realer TSE steht aus.
Quelle: https://www.bzst.de/DE/Unternehmen/Aussenpruefungen/DigitaleSchnittstelleFinV/digitaleschnittstellefinv_node.html

### 5. Geparkte / offene Bons
Ein geparkter Bon ist nicht automatisch steuerlicher Umsatz. Er darf daher nicht als
abgeschlossener Verkauf in den Z-Umsatz eingehen.

Aber: Sobald durch Scannen/Bestellerfassung ein relevanter Vorgang beginnt, kann bereits
eine TSE-Transaktion erforderlich sein. Langanhaltende Vorgänge werden insbesondere als
"Bestellung" oder geeigneter "SonstigerVorgang" abgesichert und später mit dem
"Kassenbeleg" verknüpft.

Seit R136 startet TOR die TSE-Transaktion mit der ersten Position (AEAO zu § 146a
Nr. 2.2.2). Ein geparkter Bon hält seine Transaktion offen und setzt sie beim Aufrufen fort;
die Bestellannahme beendet sie als Bestellung-V1, das Löschen eines geparkten Bons oder
das Leeren des Warenkorbs als AVBelegabbruch. Ein Z-Bericht ist nur ohne offenen Vorgang
möglich (Nr. 2.2.3.3). Ob eine über lange Zeit offene Kassenbeleg-Transaktion eines
geparkten Bons so akzeptiert wird, sollte mit dem Steuerberater bestätigt werden.

### 6. Kassensturzfähigkeit / Bargeldbewegungen
Einlagen und Entnahmen müssen nachvollziehbar erfasst werden. Der Soll-Kassenbestand
muss mit dem Ist-Bestand vergleichbar sein.

TOR 0.7.19 enthält eine append-only Test-Erfassung für EINLAGE / ENTNAHME.

### 7. TSE-Ausfall
Ausfallzeit und Grund müssen dokumentiert werden. Ein TSE-Ausfall darf nicht heimlich als
erfolgreiche Signatur behandelt werden. Für den späteren Produktivbetrieb ist ein
automatisches Ausfallprotokoll und eine eindeutige Belegkennzeichnung vorzusehen.

Rechtsgrundlage (Stand R129, 16.09.2026): AEAO zu § 146a Nr. 1.14 (BMF-Schreiben vom
30.06.2023; die Änderung vom 17.03.2026 lässt Nr. 1.14 und 2.7 unverändert):
- 1.14.1 Ausfallzeiten und Ausfallgrund sind zu dokumentieren (automatisiert möglich).
- 1.14.2 Der Ausfall muss auf dem Beleg erkennbar sein (fehlende Transaktionsnummer oder
  eindeutige Kennzeichnung).
- 1.14.3 Das System darf bis zur Behebung weiter genutzt werden; die Belegausgabepflicht
  bleibt bestehen.
- 1.14.4 Die Ursache ist unverzüglich zu beseitigen.
- Nr. 2.7: Die Belegausgabepflicht entfällt nur bei Ausfall des ganzen Systems oder des
  Druck-/Übermittlungswegs.
- DSFinV-K, Datei TSE_Transaktionen: Feld TSE_TA_FEHLER für Erläuterungen von Problemen
  in der Kommunikation zwischen Aufzeichnungssystem und TSE.

**Kein Nachsignieren.** Eine nachträgliche Signierung der während des Ausfalls erfassten
Vorgänge ist in AO, KassenSichV, AEAO und DSFinV-K nicht vorgesehen. Sie könnte den Ausfall
auch nicht heilen: § 2 KassenSichV verlangt, dass die Transaktion unmittelbar gestartet wird
und das Sicherheitsmodul die Zeitpunkte festlegt; eine spätere Signatur trüge die spätere
TSE-Zeit. TOR speichert daher je Beleg genau einen endgültigen TSE-Eintrag (Signatur oder
Ausfall) und signiert nicht nach.

Offizielle Quellen:
- https://www.bundesfinanzministerium.de/Content/DE/Downloads/BMF_Schreiben/Weitere_Steuerthemen/Abgabenordnung/AO-Anwendungserlass/2023-06-30-AEAO-Par-146-AO.pdf
- https://www.bundesfinanzministerium.de/Content/DE/Downloads/BMF_Schreiben/Weitere_Steuerthemen/Abgabenordnung/AO-Anwendungserlass/2026-03-17-aenderung-aeao-146a.pdf
- https://www.gesetze-im-internet.de/kassensichv/__2.html

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
- DSFinV-K-2.4-Export aller 20 Dateien mit Vorabprüfung und Export-Protokoll (R131, Abnahme ausstehend)
- RSA-signierte kommerzielle Lizenzdatei, getrennt von der Fiskalfreigabe

Noch zwingend offen:
- offizielles Swissbit SDK / reale TSE
- Start/Finish/ggf. UpdateTransaction mit realer TSE vollständig abnehmen
- echte Kassenbeleg-/Bestellung-Verknüpfung
- DSFinV-K 2.4 Export mit Prüfsoftware und realer TSE validieren
- TSE TAR Export mit Realhardware validieren
- produktiver §6-KassenSichV-Belegtest
- Pfand-Steuerlogik fachlich finalisieren
- Storno/Rückgabe als eigene unveränderbare Fiskalvorgänge
- reale Z-/Tagesabschluss-Implementierung auf Fiskaldatenbasis

**Folge:** TOR POS Pro 0.7.19 bleibt TESTBETRIEB und ist noch nicht für produktive
steuerliche Aufzeichnungen freigegeben.
