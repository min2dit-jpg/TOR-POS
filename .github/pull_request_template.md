## Zweck

Kurz beschreiben, was geändert wird und warum.

## Sicherheits- und Fiskalcheck

- [ ] CI läuft grün (Windows Build, SafetyTests, UI-Snapshot, Cloud)
- [ ] Keine Passwörter, Tokens, TSE-PIN/PUK/Seed, Lizenz-Private-Keys oder Kundendaten committed
- [ ] Änderungen an Kasse/Zahlung/TSE/DSFinV-K sind mit Regressionstests abgedeckt
- [ ] Kein Hardwaretest wird als bestanden behauptet, wenn nur Simulation/Mocks verwendet wurden
- [ ] Bei fiskalischer Änderung: ProcessData / Beleg / Export / Recovery-Auswirkung geprüft
- [ ] Bei Release-Änderung: ReleaseInfo, Manifest, csproj, Installer und Release-Marker bleiben konsistent
- [ ] Produktionsfreigabe wird nicht aus einem erfolgreichen CI-Lauf allein abgeleitet

## Hardware / externe Systeme

Betroffene reale Hardware oder Fremdsysteme markieren:

- [ ] Swissbit TSE
- [ ] Bondrucker
- [ ] ZVT-Terminal
- [ ] TOR Cloud
- [ ] fiskaltrust Sandbox
- [ ] Nicht betroffen

Falls reale Hardware nicht vorhanden war, hier ausdrücklich „nicht real getestet“ vermerken.

## Rückfall / Recovery

Beschreiben, was bei Crash, Timeout, UNKNOWN-Zahlung, TSE-Ausfall oder Neustart passiert, sofern relevant.
