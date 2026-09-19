# TOR POS – 7-Tage-Demo

Stand: 2026-09-19

## Ziel

Die öffentliche Demo läuft ab der ersten erfolgreichen Online-Aktivierung genau
sieben Tage. Eine normale Deinstallation und erneute Installation auf demselben
Windows-PC startet keinen neuen Testzeitraum.

Die Demo ist keine fiskalische Produktivfreigabe. Ohne kommerzielle Lizenz und
die getrennten FiscalRelease-Abnahmen entstehen keine echten Produktivbuchungen.

## Trial-ID statt Hardware-Fingerprint

TOR POS liest für die Demo keine MachineGuid, keine Laufwerksseriennummer und
keine sonstigen Hardwarekennungen.

Beim ersten Demo-Start wird stattdessen lokal eine kryptographisch zufällige
256-Bit Trial-ID erzeugt und als 64-stellige Hex-Zeichenfolge gespeichert:

`%ProgramData%\TOR-POS-Pro\trial-installation.id`

Der Demo-Installer legt den maschinenweiten Ordner mit Schreibrecht für normale
Benutzer an und markiert ihn in Inno Setup mit `uninsneveruninstall`.
Dadurch bleibt die Trial-ID bei einer normalen Deinstallation bestehen.

An den Aktivierungsdienst werden nur übertragen:

- Trial-ID
- TOR POS Version
- TOR POS Revision

Die Trial-ID enthält keine Hardwaredaten und wird nicht aus persönlichen oder
geräteinternen Identifikatoren abgeleitet.

## Cloud-Semantik

`POST /api/v1/trial/activate`

Request:

- `trial_id`: 64-stellige Trial-ID
- `version`
- `revision`

Erste Aktivierung:

- `first_seen_at = Serverzeit`
- `expires_at = first_seen_at + 7 Tage`

Spätere Aktivierungen derselben Trial-ID:

- geben dasselbe `first_seen_at` zurück
- geben dasselbe `expires_at` zurück
- verlängern die Demo nicht

Eine abgelaufene Trial-ID erhält keinen neuen Start.

## Schutzumfang

Der Mechanismus verhindert den üblichen Reset durch normale
Deinstallation/Neuinstallation. Ein Benutzer mit administrativem Zugriff, der
bewusst den maschinenweiten ProgramData-Eintrag manipuliert oder löscht, kann
nicht allein durch eine lokale Datei technisch absolut ausgeschlossen werden.
Deshalb wird diese Demo-Sperre nicht als manipulationssichere Lizenzsicherung
bezeichnet.

Die kommerzielle TOR-POS-Lizenz bleibt davon vollständig getrennt.

## Offline-Verhalten

Nach erfolgreicher Online-Aktivierung wird der absolute Ablaufzeitpunkt zusätzlich
im normalen TOR-POS-Datenbereich zwischengespeichert.

- normale Offline-Starts sind bis zum Ablauf möglich
- ein Zurückstellen der Windows-Uhr gegenüber dem zuletzt gesehenen Zeitpunkt
  sperrt den Offline-Start und verlangt eine Online-Prüfung
- wird nur der lokale Laufzeit-Cache gelöscht, fragt TOR POS erneut beim Server;
  die maschinenweite Trial-ID und der serverseitige Start bleiben unverändert

Die Cache-Signatur dient der Manipulationserkennung und ersetzt nicht die
serverseitige Aktivierungshistorie.

## Live API

Bis `api.torpos.de` als Custom Domain freigeschaltet werden kann, verwendet der
Demo-Build den temporären Live-Endpunkt:

`https://tor-pos-trial-api-xifmg0.v2.appdeploy.ai`

Danach wird ausschließlich `TrialPolicy.PublicApiBaseUrl` auf
`https://api.torpos.de` umgestellt.

## Demo-Build

Nur ein Build mit

`-p:TorDemoBuild=true`

enthält `TOR_DEMO_BUILD` und aktiviert die Trial-Sperre.

Build:

`Desktop/BUILD-DEMO-SETUP.bat`

Ergebnis:

`Desktop/installer-output/TOR-POS-Demo-Setup.exe`

Die normale TOR-POS-Ausgabe bleibt ein Nicht-Demo-Build.

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

Der Download-Endpunkt prüft vor der Auslieferung erneut SHA-256.

Öffentlicher Einstieg:

`GET /api/v1/trial/download`

Not-Aus:

`Cloud/DISABLE-DEMO.ps1`

## Datenschutz

Für den öffentlichen Betrieb muss der Datenschutzhinweis mindestens erklären:

- Zweck: Bereitstellung und Missbrauchsschutz der einmaligen 7-Tage-Demo
- gespeicherte Daten: zufällige Trial-ID, erste/letzte Aktivierung, Ablaufdatum,
  Softwareversion und Revision
- keine MachineGuid, Laufwerksseriennummer oder sonstige Hardwarekennung
- die abgelaufene Trial-ID muss zur Erkennung bereits genutzter Demos erhalten
  bleiben, solange diese Einmal-pro-Installation-Regel angeboten wird
- Lösch- und Aufbewahrungsregel ist vor öffentlicher Freigabe final festzulegen

