# TOR POS Pro – Swissbit Hardware-TSE Runtime
Stand: 04.09.2026

## Status v0.6.8

TOR besitzt jetzt eine reale native WORM-API-Ladeschicht für Windows.

Die Swissbit-Binärdateien selbst werden NICHT mit TOR ausgeliefert.
Sie müssen aus einem offiziell bezogenen Swissbit / TSE-Lieferanten SDK stammen.

## Erwartete Windows-Dateien

Mindestens:
- `WormAPI.dll`

Je nach SDK-Paket zusätzlich:
- `WormAPIUni.dll`

Ablage im TOR-Entwicklerpaket:
`vendor\swissbit\windows64\`

Der Setup-Builder kopiert vorhandene offizielle Dateien automatisch nach:
`publish\win-x64\SwissbitSdk\`

Im installierten System lädt TOR bevorzugt:
`<TOR POS Installationsordner>\SwissbitSdk\WormAPI.dll`

## Was TOR v0.6.8 real vorbereitet/implementiert

- dynamisches Laden von WormAPI.dll
- Abfrage der WORM-API-Version
- Prüfung benötigter Exporte
- automatische Suche nach `TSE_COMM.DAT`, `TSE_INFO.DAT` bzw. Laufwerkslabel SWISSBIT
- `worm_init` / `worm_cleanup`
- TSE-Info lesen
- Seriennummer
- FormFactor
- Hardware-/Softwareversion
- Zertifikatsablauf
- Initialisierungsstatus
- SelfTest-/Zeit-/CTSS-Status
- sichere Erstinitialisierung über `worm_tse_setup`
- Client-Registrierung
- Selbsttest
- TimeAdmin-Zeitupdate
- StartTransaction
- UpdateTransaction
- FinishTransaction
- Signaturzähler / Transaktionsnummer / LogTime / Signatur auslesen
- vollständiger TAR-Export
- alle WORM-Aufrufe innerhalb TOR serialisiert

## Sicherheitsregeln bei Erstinitialisierung

Eine produktive TSE darf nicht blind mehrfach initialisiert werden.

TOR verweigert automatisches `setup`, wenn die TSE uninitialisiert ist,
PIN/PUK aber bereits verändert wurden.

Credential-Seed wird NICHT fest in TOR eingebaut.
Der Benutzer/Installateur muss den Seed seines konkreten Lieferanten kennen.

Admin-PIN, TimeAdmin-PIN, PUK und Credential-Seed werden durch TOR v0.6.8
nicht in app_settings oder Logs gespeichert.

## Noch NICHT für steuerlichen Produktivbetrieb freigegeben

Die native Verbindung allein macht die Gesamtkasse noch nicht produktiv konform.

Vor Freigabe fehlen weiterhin insbesondere:
- offizieller Abgleich der verwendeten Hardware-TSE-2 SDK-Version mit Swissbit-Dokumentation
- echte Hardware-Abnahmetests
- ProcessType/ProcessData für Kassenbeleg/Bestellung final
- Parken ↔ TSE-Transaktionsverknüpfung
- TSE-Felder auf Produktivbeleg end-to-end
- DSFinV-K 2.4 Export
- Storno/Rückgabe-Fiskalfluss
- produktiver Z-Abschluss
- Ausfall-/Recovery-End-to-End-Test

Darum bleibt der globale TOR-Fiskalstatus weiterhin TESTBETRIEB.


## v0.7.0 – SDK auf vorhandenen Windows-PCs finden

TOR kann jetzt eine bereits auf demselben PC vorhandene `WormAPI.dll` suchen
oder per Dateiauswahl verwenden. Die Datei wird auf notwendige Swissbit
WORM-API-Exports geprüft, bevor der Pfad übernommen wird.

Menü:
`Einstellungen → TSE-Aktivierung`

Reihenfolge:
1. `SDK AUF PC SUCHEN`
2. falls nichts gefunden wird: `WORMAPI.DLL AUSWÄHLEN`
3. danach `TSE SUCHEN`
