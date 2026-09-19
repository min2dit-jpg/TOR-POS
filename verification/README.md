# TOR POS – Verifikationsnachweise

Dieser Ordner enthält technische Nachweise aus Entwicklung, Tests und
Abnahmen. Er ist **kein Beweis einer steuerlichen Produktivfreigabe**.

Der aktuelle Softwarestand wird ausschließlich durch
`Desktop/src/TorPos.Core/ReleaseInfo.cs` bestimmt. Alte R*-Ordner und Logs
bleiben als historische Nachweise erhalten und dürfen nicht als Aussage über
den aktuellen Release interpretiert werden.

## Was hier bereits liegt

Die vorhandenen R41–R75.x-Dateien und Unterordner dokumentieren historische
Build-, Hash-, Cloud-, statische und Safety-Test-Ergebnisse aus den jeweiligen
Revisionen.

Zusätzlich existiert `Desktop/verification/` mit älteren lokalen Build- und
Testprotokollen. Diese Dateien bleiben aus Gründen der Nachvollziehbarkeit
erhalten.

## Aktuelle automatische Nachweise

Für den aktuellen Stand sind GitHub Actions und die aktuelle CI-Konfiguration
unter `.github/workflows/tor-pos-ci.yml` maßgeblich. Die CI umfasst derzeit
unter anderem:

- Versionskonsistenz,
- Windows Release-Build,
- Safety-/Regressionstests,
- UI-Snapshot-/Layout-Prüfungen,
- Cloud-Syntax- und Cloud-Tests.

Ein grüner CI-Lauf bestätigt nur diese automatisierten Prüfungen.

## Was für die reale fiskalische End-to-End-Abnahme noch separat nachzuweisen ist

Vor einer fiskalischen Produktivfreigabe mit realer Hardware müssen mindestens
folgende Punkte mit der vorgesehenen Hardware-/SDK-Kombination dokumentiert
werden:

1. Swissbit WORM API laden und reale Hardware-TSE erkennen.
2. TSE-Identität, Seriennummer, Client-ID und Status plausibilisieren.
3. Reale TSE-Transaktion Start/Finish mit dem von TOR erzeugten
   `Kassenbeleg-V1` durchführen.
4. Kontrollierten BAR-Testbon für 19 % und 7 % prüfen.
5. processData, Transaktionsnummer, Signaturzähler, TSE-Seriennummer,
   TSE-Zeiten und Signaturdaten gegen den gespeicherten TOR-Datensatz prüfen.
6. 80-mm-Bon inklusive TSE-Pflichtfeldern und DSFinV-K-Anhang-I-QR prüfen.
7. TSE entfernen / Verbindung unterbrechen: Ausfall muss erkannt, dokumentiert
   und auf dem Bon kenntlich gemacht werden; keine erfundene Signatur.
8. Programm während eines definierten Testzustands beenden und
   Restart-/Recovery-Verhalten prüfen.
9. TSE-TAR-Export erzeugen und lesbar archivieren.
10. DSFinV-K-Export des Testzeitraums erzeugen und die TSE-/Bon-Zuordnung
    gegen die realen Testtransaktionen prüfen.
11. Drucker-, Scanner- und – sofern aktiviert – Kartenterminal-End-to-End-Test
    auf der vorgesehenen Kassenhardware durchführen.

Für diese Abnahme soll pro freigegebenem Release ein eigener Nachweisordner
verwendet werden, z. B. `verification/hardware/Rxxx/`.

## Nachweisregeln

Ein Abnahmenachweis soll mindestens enthalten:

- TOR Revision und numerische Version,
- Datum und Testperson,
- Windows-/Kassenhardware,
- TSE-Hersteller, Produkt und Seriennummer in geeigneter gekürzter bzw.
  intern zulässiger Form,
- WORM-API-/SDK-Version,
- Druckermodell und Terminalmodell, soweit beteiligt,
- ausgeführte Testfälle,
- erwartetes und tatsächliches Ergebnis,
- Fehler und offene Punkte,
- Hashwerte relevanter Export-/Setup-Dateien,
- klare Entscheidung: bestanden / nicht bestanden / offen.

Produktive Geheimnisse wie TSE-PIN, PUK, Credential-Seed, private
Code-Signing-Schlüssel, Lizenz-Private-Keys oder Kundendaten gehören niemals in
diesen Ordner.
