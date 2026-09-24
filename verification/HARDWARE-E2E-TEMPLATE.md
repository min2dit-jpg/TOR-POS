# TOR POS – Hardware-/Fiskal-End-to-End-Abnahme

## Release

- Revision:
- Version:
- Release-Name:
- Commit SHA:
- Datum:
- Tester:

## Umgebung

- Windows-Version:
- Kassen-PC:
- TOR Edition: KIOSK / IMBISS
- Drucker:
- Scanner:
- Kartenterminal:
- Netzwerk:

## TSE

### Zertifizierung des Produkts (Referenz, aus dem BSI-Zertifikat)

| | |
|---|---|
| Prüfgegenstand | Swissbit TSE 2.0, Swissbit AG |
| Zertifizierungs-ID | **BSI-K-TR-0800-2026** |
| Konformität zu | BSI TR-03153 |
| Prüfgrundlage | TR-03153-1 v1.1.1 (19.12.2023), TR-03153-1-TS v1.1.1 (30.08.2024) |
| Ausgestellt | 21.04.2026, Bonn |
| Gültig bis | **20.04.2034** |

Diese ID gehört in `tse.bsi_id` (Erweitert / Techniker). Die Swissbit WORM API
liefert sie nicht; sie wird aus der zertifizierten Produktzuordnung übernommen.
Der vollständige Konformitätsreport und die dort genannte Version/Konfiguration
bleiben maßgeblich.

### Gerät

- Hersteller: Swissbit
- Produkt:
- TSE-Generation: 1 / 1.1 / 2
- TSE-Seriennummer:
- Zertifikat gültig bis (UTC):
- Client-ID:
- WORM-API-/SDK-Version:
- TSE-Status vor Test:
- TSE-TAR-Datei:
- TAR SHA-256:

> Keine PIN, PUK, Credential-Seeds oder andere Geheimnisse eintragen.

## Testfälle

| Test | Ergebnis | Nachweis / Bemerkung |
|---|---|---|
| Start **ohne** angeschlossene TSE: Warnfenster erscheint | ☐ PASS ☐ FAIL | |
| Warnfenster nennt den Zustand (keine TSE / SDK fehlt / Fehler) | ☐ PASS ☐ FAIL | |
| Kasse bleibt nach dem Hinweis bedienbar (Verkauf möglich) | ☐ PASS ☐ FAIL | |
| Hinweis erscheint nur **einmal** pro Programmlauf | ☐ PASS ☐ FAIL | |
| Admin sieht den Weg in Erweitert / Techniker, Kassenkraft nicht | ☐ PASS ☐ FAIL | |
| Badge **TSE-AUSFALL** in der Kopfzeile bleibt sichtbar, solange der Ausfall offen ist | ☐ PASS ☐ FAIL | |
| Tooltip des Badges nennt Beginn und Grund | ☐ PASS ☐ FAIL | |
| Ausfall ist im `tse_outage_log` dokumentiert (nachweisbar über den DSFinV-K-Export) | ☐ PASS ☐ FAIL | |
| Nach Anstecken einer betriebsbereiten TSE: kein Warnfenster mehr | ☐ PASS ☐ FAIL | |
| SDK lädt | ☐ PASS ☐ FAIL | |
| TSE wird erkannt | ☐ PASS ☐ FAIL | |
| TSE-Generation eindeutig erkannt | ☐ PASS ☐ FAIL | |
| Exakter Zertifikatsablauf wird gelesen | ☐ PASS ☐ FAIL | |
| TSE-Identität / Client-ID plausibel | ☐ PASS ☐ FAIL | |
| BAR Testbon 19 % | ☐ PASS ☐ FAIL | |
| BAR Testbon 7 % | ☐ PASS ☐ FAIL | |
| Kassenbeleg-V1 processData stimmt | ☐ PASS ☐ FAIL | |
| TSE Transaction Number gespeichert | ☐ PASS ☐ FAIL | |
| Signature Counter gespeichert | ☐ PASS ☐ FAIL | |
| TSE-Zeiten unverändert übernommen | ☐ PASS ☐ FAIL | |
| 80-mm-Bon Pflichtfelder | ☐ PASS ☐ FAIL | |
| DSFinV-K QR nach Anhang I | ☐ PASS ☐ FAIL | |
| TSE-Ausfall durch Entfernen / Trennen | ☐ PASS ☐ FAIL | |
| Ausfallhinweis auf Bon | ☐ PASS ☐ FAIL | |
| Keine Nachsignierung eines Ausfallbons | ☐ PASS ☐ FAIL | |
| Neustart / offener Vorgang Recovery | ☐ PASS ☐ FAIL | |
| TSE TAR Export | ☐ PASS ☐ FAIL | |
| DSFinV-K Export | ☐ PASS ☐ FAIL | |
| Bon ↔ TSE ↔ DSFinV-K Zuordnung | ☐ PASS ☐ FAIL | |
| Drucker End-to-End | ☐ PASS ☐ FAIL | |
| Kartenterminal End-to-End (falls aktiv) | ☐ PASS ☐ FAIL | |

### Startprüfung ohne TSE

> Diese Datei ist die **Vorlage** und bleibt leer. Ergebnisse gehören in eine
> datierte Kopie; der bisher einzige Bericht liegt in
> `verification/TSE-STARTPRUEFUNG-BERICHT-2026-09-23.md`.


Diese acht Zeilen werden **vor** dem Anstecken der TSE geprüft: Programm auf
einem PC ohne angeschlossene TSE starten und mit einem Admin-Konto sowie mit
einem Kassenkonto anmelden.

Erwartet wird ein Hinweisfenster, **kein** blockierter Verkauf: ein TSE-Ausfall
ist nach § 146a AO ein dokumentierter Ausfall und kein Grund, den Betrieb
anzuhalten. Das Fenster sagt das ausdrücklich und weist zugleich darauf hin,
dass die Vorgänge in dieser Zeit nicht fiskal abgesichert sind.

Im Trainingsmodus erscheint der Hinweis bewusst nicht, weil dort ohnehin nicht
signiert wird.

Die **Statuszeile** unter dem Verkaufsfeld ist absichtlich flüchtig: der nächste
Scan oder Vorgang überschreibt sie. Dauerhafter Anzeiger ist das Badge
**TSE-AUSFALL** in der Kopfzeile. Der eigentliche Nachweis liegt in der Tabelle
`tse_outage_log` (gegen Löschen per Trigger geschützt) und im Audit-Log als
Ereignis `TSE_UNAVAILABLE`; sichtbar wird er für die Prüfung über den
DSFinV-K-Export, der Beginn, Ende und Grund jedes Ausfalls mitführt. Eine
eigene Bildschirmliste der Ausfälle gibt es derzeit nicht.

Läuft die Oberfläche auf Türkisch oder Englisch, muss der Hinweis in dieser
Sprache erscheinen und der Gerätename **TSE** unverändert enthalten bleiben.

## BAR-Testbon – Detail

- TOR-Architektur: direkte Swissbit WORM API (kein fiskaltrust / kein ftState)
- Checkout-Journal vor Abschluss: `CASH_READY` (BAR verwendet kein `PREPARED → SENT`)
- Erwarteter TSE-Vorgang: `OPEN → FINISHED`
- Tatsächlicher TSE-Vorgangszustand:
- An die TSE gesendeter processType:
- An die TSE gesendete processData:
- Belegnummer:
- Steuersatz: 19 % / 7 %
- Brutto:
- Erwartetes `Kassenbeleg-V1` processData:
- Tatsächliches processData:
- TSE Transaction Number:
- Signature Counter:
- TSE Serial:
- Startzeit:
- Endzeit:
- QR-Payload:
- Vergleich TOR-Datensatz ↔ TSE: ☐ IDENTISCH ☐ ABWEICHUNG

## Störfall / Recovery

- Ausgelöster Störfall:
- Zeitpunkt:
- Verhalten vor Neustart:
- Verhalten nach Neustart:
- Doppelbuchung ausgeschlossen: ☐ JA ☐ NEIN
- Offener Vorgang korrekt beendet / wiederhergestellt: ☐ JA ☐ NEIN

## Exporte

- TSE TAR:
- TAR SHA-256:
- DSFinV-K Ordner/Archiv:
- DSFinV-K SHA-256:
- Prüfhinweise:

## Offene Punkte

-

## Abnahmeentscheidung

Diese physische Abnahme gilt ausschließlich für TSE-Generation: **_____**

> Eine bestandene Gen-1-, Gen-1.1- oder Gen-2-Abnahme darf keine andere
> Generation freigeben.

☐ BESTANDEN  
☐ NICHT BESTANDEN  
☐ OFFEN – weitere Prüfung erforderlich

Begründung:
