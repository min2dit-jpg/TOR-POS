# TOR POS Desktop

**Aktueller Stand:** R171 · Merd-M · 0.7.33.871

Die verbindliche Versionsquelle ist `src/TorPos.Core/ReleaseInfo.cs`. Der
zentrale Release-Index liegt im Repository-Root unter `../CHANGELOG.md`.

## Technologie

- Windows Desktop-Anwendung
- .NET 10
- Avalonia 12.1.2
- SQLite / Microsoft.Data.Sqlite 10.0.11
- Microsoft.Extensions.DependencyInjection
- ZVT über Portalum.Zvt
- Swissbit Hardware-TSE über die externe WORM API

Die Anwendung ist für Windows-Kassenhardware ausgelegt. Drucker-, TSE- und
Kartenterminalintegration sind Windows-spezifisch; Cross-Compilation allein
macht daraus keine freigegebene Linux-Kasse.

## Hauptstruktur

- `src/TorPos.App/` – Avalonia UI und Desktop-Komposition
- `src/TorPos.Application/` – Checkout-/Use-Case-Orchestrierung
- `src/TorPos.Core/` – Domänenmodelle, Fiskaldaten, Regeln
- `src/TorPos.Infrastructure/` – SQLite, TSE, Drucker, Export, Hardware
- `tests/TorPos.SafetyTests/` – Safety-/Regressionstest-Suite
- `tools/` – Diagnose-, UI-Snapshot- und Hilfswerkzeuge
- `verification/` – ältere lokale technische Nachweise

## Build und Tests

Übliche Einstiegspunkte:

- `1-SETUP-ERSTELLEN.bat` – Windows Setup bauen
- `2-NUR-ENTWICKLUNG-DIREKT-STARTEN.bat` – Entwicklungsstart
- `7-SICHERHEITSTESTS.bat` – Safety-/Regressionstests

Die maßgebliche CI liegt im Repository-Root unter
`../.github/workflows/tor-pos-ci.yml` und prüft zusätzlich
Versionskonsistenz, Release-Build, UI-Snapshots und Cloud-Tests.

## Fiskalischer Stand

Im aktuellen Code vorhanden sind unter anderem:

- Swissbit-Hardware-TSE-Provider über WORM API,
- TSE Start/Update/Finish,
- TSE-Aktivierung und TAR-Export,
- `Kassenbeleg-V1` / `Bestellung-V1` processData nach DSFinV-K 2.4 Anhang I,
- DSFinV-K-Export,
- TSE-Daten und Anhang-I-QR auf dem Bon,
- TSE-Ausfallbehandlung,
- Restart-/Vorgang-Recovery.

**Noch getrennt nachzuweisen:** reale End-to-End-Abnahme mit der vorgesehenen
Swissbit-TSE, WORM-API-Version, Drucker- und – falls aktiviert –
Kartenterminal-Hardware. Automatische Tests oder ein erkannter TSE-Stick allein
sind keine fiskalische Produktivfreigabe.

Der Abnahmenachweis wird unter `../verification/` geführt.

## Historische Dokumente

Die vielen `R*-CHANGELOG.md`, `R*-REVIEW.md` und älteren v0.7.33-Dokumente
beschreiben den jeweiligen damaligen Entwicklungsstand. Sie sind bewusst als
Audit-/Entwicklungshistorie erhalten und **nicht** als aktuelle
Versionsbeschreibung zu lesen.

Für den aktuellen Stand immer zuerst verwenden:

1. `../README.md`
2. `../CHANGELOG.md`
3. `src/TorPos.Core/ReleaseInfo.cs`
4. `ROADMAP.md`
