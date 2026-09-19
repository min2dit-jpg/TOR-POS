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

## BAR-Testbon – Detail

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
