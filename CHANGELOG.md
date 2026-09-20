# TOR POS – Release-Index

<!-- TOR_RELEASE:R171|0.7.33.871|Merd-M -->

## Aktueller Release

**R171 · Merd-M · 0.7.33.871**

Verbindliche Quelle: `Desktop/src/TorPos.Core/ReleaseInfo.cs`.

### R171

- DSFinV-K 2.4 Export: frei wählbare Von-/Bis-Kalendertage, Preflight und Zielordnerauswahl; Export bleibt auf vollständige Z-Abschlusszeiträume begrenzt.
- Release-Metadaten nach R149-Stagnation auf R171 / 0.7.33.871 synchronisiert; Versionsprüfung wird gegen neue Review-Revisionen gehärtet.
- R169/R170 Funktionen (Drucker-Zentrale, Kassenschubladen-Test, Gewichtsartikel/Waage) sind damit erstmals in einer fortgeschriebenen zentralen Release-Revision enthalten.

## Warum es viele R*-Dateien gibt

Die einzelnen `R*-CHANGELOG.md`-, Review- und Startdateien dokumentieren jeweils den historischen Stand einer bestimmten Änderung. Sie werden bewusst nicht als „aktuelle Version“ interpretiert.

Ein älteres Dokument wie R75.1 ist daher **kein Versionskonflikt**, solange die zentrale Versionsquelle und ihre Spiegeldateien auf denselben aktuellen Release zeigen.

## Versionskonsistenz

Bei jedem CI-Lauf werden mindestens diese Angaben gegeneinander geprüft:

- `TorRelease.Version`
- `TorRelease.Revision`
- `TorRelease.ReleaseName`
- `manifest.json`
- `TorPos.App.csproj`
- `TOR-POS-Pro-Setup.iss`
- Release-Marker in `README.md` und `CHANGELOG.md`

Bei einer Abweichung schlägt CI fehl. Damit kann z. B. ein neuer R150-Commit nicht mehr versehentlich mit R149-Installer- oder Manifestdaten ausgeliefert werden.

## Pflege bei einem neuen Release

1. Zuerst `Desktop/src/TorPos.Core/ReleaseInfo.cs` aktualisieren.
2. Die gespiegelten Versionsfelder in Manifest, App-Projekt und Installer angleichen.
3. Die Release-Marker in `README.md` und `CHANGELOG.md` aktualisieren.
4. CI ausführen; erst bei grüner Versionsprüfung releasen.

Historische Changelog-Dateien bleiben unverändert erhalten.
