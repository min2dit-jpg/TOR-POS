# TOR POS – 7-Tage-Demo

Stand: 2026-09-19

## Ziel

Die öffentliche Demo läuft ab der ersten Online-Aktivierung genau sieben Tage.
Eine Deinstallation und erneute Installation auf demselben Windows-PC startet
keinen neuen Testzeitraum.

Die Demo ist keine fiskalische Produktivfreigabe. Ohne kommerzielle Lizenz und
die getrennten FiscalRelease-Abnahmen entstehen keine echten Produktivbuchungen.

## Gerätebindung

Der Windows-Client liest lokal:

- Windows MachineGuid
- Seriennummer des Windows-Systemlaufwerks

Aus diesen Werten entsteht lokal:

`SHA-256("TOR-POS-TRIAL-PC-V1|<MachineGuid>|<VolumeSerial>")`

Nur dieser 64-stellige SHA-256-Wert wird an TOR POS Cloud übertragen.
MachineGuid und Laufwerksseriennummer verlassen den PC nicht.

Die normale kommerzielle Lizenz bleibt davon getrennt. Deren InstallationId wird
nicht in den Trial-Fingerprint aufgenommen, weil eine Neuinstallation sonst eine
neue Demo erzeugen könnte.

## Cloud-Semantik

`POST /api/v1/trial/activate`

Erste Aktivierung:

- `first_seen_at = Serverzeit`
- `expires_at = first_seen_at + 7 Tage`
- Fingerprint ist Primärschlüssel

Spätere Aktivierungen desselben Fingerprints:

- geben dasselbe `first_seen_at` zurück
- geben dasselbe `expires_at` zurück
- verlängern die Demo nicht
- funktionieren damit auch nach Deinstallation/Neuinstallation nicht als Reset

Ein abgelaufener Fingerprint bleibt gespeichert und erhält keinen neuen Start.

## Offline-Verhalten

Nach einer erfolgreichen Online-Aktivierung wird der absolute Ablaufzeitpunkt
lokal zwischengespeichert.

- normale Offline-Starts sind bis zum Ablauf möglich
- ein Zurückstellen der Windows-Uhr gegenüber dem zuletzt gesehenen Zeitpunkt
  sperrt den Offline-Start und verlangt eine Online-Prüfung
- wird der lokale Cache gelöscht, ist erneut eine Online-Prüfung nötig; der
  Cloud-Datensatz startet dabei nicht neu

Die lokale Cache-Signatur ist eine Manipulationserkennung, kein Ersatz für die
serverseitige Gerätehistorie.

## Demo-Build

Nur ein Build mit

`-p:TorDemoBuild=true`

enthält `TOR_DEMO_BUILD` und aktiviert die Trial-Sperre.

Normaler TOR-POS-Build und Demo-Build bleiben damit getrennt.

Build:

`Desktop/BUILD-DEMO-SETUP.bat`

Ergebnis:

`Desktop/installer-output/TOR-POS-Demo-Setup.exe`

## Veröffentlichung

Das Setup darf erst nach Authenticode-Code-Signing veröffentlicht werden:

`Cloud/PUBLISH-DEMO.ps1`

Die Veröffentlichung:

1. kopiert das Setup in eine Staging-Datei,
2. prüft die Authenticode-Signatur,
3. prüft den erwarteten Signer-Thumbprint,
4. berechnet SHA-256,
5. veröffentlicht unter einem hashbasierten Dateinamen,
6. schreibt atomar `trial-manifest.json`.

Der Download-Endpunkt prüft vor jeder Auslieferung erneut SHA-256.

Öffentlicher Einstieg:

`GET /api/v1/trial/download`

Not-Aus:

`Cloud/DISABLE-DEMO.ps1`

## Datenschutz

Der Trial-Fingerprint ist ein pseudonymes Gerätekennzeichen. Für den Betrieb der
Demo muss im Datenschutzhinweis erklärt werden:

- Zweck: Missbrauchsschutz / einmaliger 7-Tage-Test pro PC
- gespeicherte Daten: SHA-256-Gerätefingerprint, erste/letzte Aktivierung,
  Ablaufdatum, Softwareversion/Revision
- keine Speicherung der Rohwerte MachineGuid oder Laufwerksseriennummer
- Aufbewahrungslogik: abgelaufene Fingerprints müssen für die Verhinderung
  wiederholter Demo-Aktivierungen erhalten bleiben

Vor öffentlicher Freigabe ist die konkrete DSGVO-Aufbewahrungs-/Löschregel mit
dem Betreiber-Datenschutzhinweis abzugleichen.
