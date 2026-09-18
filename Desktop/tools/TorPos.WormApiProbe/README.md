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
