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

- Hersteller: Swissbit
- Produkt:
- TSE-Seriennummer:
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
| Statuszeile zeigt denselben Zustand dauerhaft an | ☐ PASS ☐ FAIL | |
| Ausfall ist im Protokoll dokumentiert (nicht nur am Bildschirm) | ☐ PASS ☐ FAIL | |
| Nach Anstecken einer betriebsbereiten TSE: kein Warnfenster mehr | ☐ PASS ☐ FAIL | |
| SDK lädt | ☐ PASS ☐ FAIL | |
| TSE wird erkannt | ☐ PASS ☐ FAIL | |
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

Diese acht Zeilen werden **vor** dem Anstecken der TSE geprüft: Programm auf
einem PC ohne angeschlossene TSE starten und mit einem Admin-Konto sowie mit
einem Kassenkonto anmelden.

Erwartet wird ein Hinweisfenster, **kein** blockierter Verkauf: ein TSE-Ausfall
ist nach § 146a AO ein dokumentierter Ausfall und kein Grund, den Betrieb
anzuhalten. Das Fenster sagt das ausdrücklich und weist zugleich darauf hin,
dass die Vorgänge in dieser Zeit nicht fiskal abgesichert sind.

Im Trainingsmodus erscheint der Hinweis bewusst nicht, weil dort ohnehin nicht
signiert wird.

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

☐ BESTANDEN  
☐ NICHT BESTANDEN  
☐ OFFEN – weitere Prüfung erforderlich

Begründung:
