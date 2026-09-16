# TOR POS Pro – Checkliste vor kommerzieller Freigabe

Stand: 16.09.2026 · Version 0.7.33.821 (R121)

> Arbeits- und Abnahmecheckliste, keine Rechts- oder Steuerberatung.

## Aktueller Freigabestatus

**NICHT PRODUKTIV FREIGEGEBEN.** Die Version bleibt `TESTBETRIEB`.
Eine aktive TOR-POS-Softwarelizenz ändert diesen fiskalen Status nicht.

Unten ist **keine** Box abgehakt. Das ist beabsichtigt und bleibt so, bis die
jeweilige Abnahme wirklich stattgefunden hat - die Liste ist der Nachweis,
nicht die Absichtserklärung. Softwareseitige Befunde aus dem Gesamtaudit vom
16.09.2026 (`TOR-POS-DERIN-INCELEME-2026-09-16.md`) sind in R113-R121
abgearbeitet; die offenen Punkte hier hängen an externen Voraussetzungen
(zertifizierte TSE, Swissbit-SDK-Lizenz, DSFinV-K-Descriptor, Rechts- und
Steuerprüfung, Realhardware).

## Blockierende fiskale Abnahmen

- [ ] Zertifizierte, aktuell zulässige TSE für den konkreten Vertrieb ausgewählt
- [ ] Offizielles Swissbit SDK/Runtime-Nutzungs- und Vertriebsrecht schriftlich geklärt
- [ ] Realhardware-Test: Initialisierung, Client-ID, Self-Test und Zeit
- [ ] Realhardware-Test: Start/Update/FinishTransaction und Abbruchfälle
- [ ] TSE-Trennung, Wiederanlauf und `TSE-AUSFALL`-Beleg vollständig getestet
- [ ] TAR-Export mit realer TSE erzeugt und extern geprüft
- [ ] DSFinV-K 2.4 vollständig implementiert, Descriptor/index und alle Tabellen validiert
- [ ] Z-/Tagesabschluss aus unveränderbaren Fiskaldaten implementiert und geprüft
- [ ] Bonpflichtfelder nach § 6 KassenSichV mit Testfällen abgenommen
- [ ] Parken/Bestellung, Storno, Retoure, Rabatt, Einlage/Entnahme und Pfand fiskal geprüft
- [ ] Kassenmeldung/Betreiberprozess nach § 146a Abs. 4 AO dokumentiert

Offizielle Prüfbasis:

- BMF-FAQ: <https://www.bundesfinanzministerium.de/Content/DE/FAQ/FAQ-steuergerechtigkeit-belegpflicht.html>
- BSI-Liste zertifizierter TSE: <https://www.bsi.bund.de/DE/Themen/Unternehmen-und-Organisationen/Standards-und-Zertifizierung/Zertifizierung-und-Anerkennung/Listen/Zertifizierte-Produkte-nach-TR/Technische_Sicherheitseinrichtungen/TSE_node.html>
- BSI TR-03153: <https://www.bsi.bund.de/DE/Themen/Unternehmen-und-Organisationen/Standards-und-Zertifizierung/Technische-Richtlinien/TR-nach-Thema-sortiert/tr03153/tr03153_node.html>
- § 146a AO: <https://www.gesetze-im-internet.de/ao_1977/__146a.html>
- KassenSichV: <https://www.gesetze-im-internet.de/kassensichv/>

## Software- und Lieferabnahme

- [ ] Windows Release-Build ohne Warnungen/Fehler auf sauberem Rechner
- [ ] Update-Test von mindestens der letzten zwei Kundenversionen
- [ ] Frische Installation: KIOSK und IMBISS jeweils auswählbar
- [ ] Update-Installation: vorhandene Auswahl sichtbar und bewusst änderbar
- [ ] Artikelmaske auf 1024×768, 1280×720 und Ziel-Touchscreen geprüft
- [ ] Datenbankmigration mit realer Kopie getestet; Backup und Restore nachgewiesen
- [ ] Fehlerfenster für defekte Datenbank, fehlendes SDK und Geräteausfall geprüft
- [ ] ZVT-Timeout/unklarer Zahlungsstatus verhindert automatische Doppelbuchung
- [ ] Drucktest mit `Ä Ö Ü · ä ö ü · ß · €`
- [ ] SBOM/Paketliste und Drittanbieterhinweise final erzeugt
- [ ] Setup, Quellstand, Konfiguration und Abnahmeprotokoll mit Hash archiviert

## Vertrag und Kundendokumentation

- [ ] `NUTZUNGSBEDINGUNGEN-DE.txt` durch IT-/Vertriebsrechtsberatung freigegeben
- [ ] Datenschutzinformationen und Auftragsrollen für tatsächlich genutzte Dienste geklärt
- [ ] Support, Updates, SLA, Gewährleistung und Haftung vertraglich festgelegt
- [ ] Kundenspezifische Verfahrensdokumentation vollständig ausgefüllt
- [ ] Bedienungs-, Backup-, TSE-Ausfall- und Exportanleitung ausgeliefert
- [ ] Kommerzielle Lizenz für richtige Kunden-Nr., Firma, PC-Gerätecode, Installations-ID und Edition ausgestellt
- [ ] Privater Lizenzschlüssel nicht im Kundenpaket und sicher verwahrt

## Freigabeprotokoll

- Build/Commit:
- Setup-SHA-256:
- Testdatum:
- Testsystem/TSE/SDK:
- Technische Freigabe:
- Fiskale/steuerliche Prüfung:
- Rechtliche Freigabe:
- Freigegebene Version:
