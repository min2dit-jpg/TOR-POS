# TOR POS · Swissbit WORM API Probe

Dieses kleine, unabhängige Entwicklerwerkzeug prüft eine offiziell bezogene
Windows-64-Bit `WormAPI.dll`, ohne eine TSE zu initialisieren oder Transaktionen
auszuführen.

Es prüft ausschließlich:

- ob die DLL geladen werden kann,
- ob `worm_getVersion` aufrufbar ist,
- ob alle von `SwissbitWormApiBridge` benötigten 39 Exporte vorhanden sind.

## Aufruf

```powershell
dotnet run --project tools/TorPos.WormApiProbe -- "C:\Pfad\zu\WormAPI.dll"
```

Ein bestandener Probe-Lauf bedeutet **nicht**, dass die TSE für den Produktivbetrieb
freigegeben ist. Dafür bleiben reale Hardware-, Start/Update/Finish-, TAR-, Recovery-
und DSFinV-K-End-to-End-Tests erforderlich.

Die Swissbit-Binärdateien selbst gehören nicht in dieses Repository.


## Read-only Recovery-Diagnose

Wenn eine offizielle Windows-x64 `WormAPI.dll` und eine dedizierte Test-TSE vorhanden
sind, kann das Werkzeug zusätzlich nur lesende Recovery-Informationen abfragen:

```powershell
dotnet run --project tools/TorPos.WormApiProbe -- "C:\Pfad\WormAPI.dll" --recovery "E:" "TOR-KASSE-TEST"
```

Dabei werden ausschließlich `worm_init`, `worm_transaction_listStartedTransactions`,
`worm_transaction_lastResponse` und die zugehörigen Response-Getter verwendet.
Es werden **keine** Setup-, Login-, Zeit-, Start-, Update- oder Finish-Aufrufe ausgeführt.

Die Recovery-Diagnose ist für Crash-/Restart-Abnahmetests gedacht: TOR kann damit
seinen lokalen offenen Vorgang mit dem Zustand vergleichen, den die TSE selbst meldet.

Auch dieser Modus ist kein Produktivfreigabe-Nachweis. Vor Einsatz muss die exakte
offizielle Windows-SDK-Version und deren Header gegen die verwendeten Signaturen
geprüft werden.
