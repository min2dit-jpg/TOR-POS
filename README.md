# TOR POS

<!-- TOR_RELEASE:R149|0.7.33.849|Merd-M -->

**Aktueller Release:** R149 · Merd-M · 0.7.33.849

TOR POS ist ein Windows-Kassensystem mit Desktop- und Cloud-Komponenten. Der aktive Desktop-Code liegt unter `Desktop/`, die Cloud-Komponenten unter `Cloud/`.

## Verbindliche Versionsquelle

Die **einzige maßgebliche Versionsquelle** ist:

`Desktop/src/TorPos.Core/ReleaseInfo.cs`

Dort stehen Release-Name, numerische Version und Revision. Die folgenden Dateien spiegeln diese Angaben nur wider und müssen identisch bleiben:

- `Desktop/manifest.json`
- `Desktop/src/TorPos.App/TorPos.App.csproj`
- `Desktop/TOR-POS-Pro-Setup.iss`
- diese README und der zentrale `CHANGELOG.md`

Die CI prüft diese Werte automatisch. Eine Abweichung bricht den Build ab.

## Changelog-Regel

Dateien wie `R75.1-...`, `R126-CHANGELOG.md`, `R140-CHANGELOG.md` usw. sind **historische Revisionsdokumente**. Ihre Dateinamen zeigen den Stand der jeweiligen Änderung, nicht den aktuellen Programmstand.

Der aktuelle Stand ist immer oben in dieser README, im zentralen `CHANGELOG.md` und technisch in `ReleaseInfo.cs` angegeben.

## Projektstruktur

- `Desktop/` – TOR POS Desktop-Anwendung, Tests, Installer und fiskalische Integration
- `Cloud/` – TOR Cloud-Komponenten
- `.github/workflows/` – CI-Prüfungen
- `CHANGELOG.md` – zentraler Release-Index

## Hinweis zur fiskalischen Freigabe

Implementierter Code, erfolgreiche automatische Tests und ein Release-Stand ersetzen keine reale End-to-End-Abnahme mit der vorgesehenen TSE-, Drucker- und Terminal-Hardware. Der jeweilige Freigabestatus wird im Projekt separat geführt.
