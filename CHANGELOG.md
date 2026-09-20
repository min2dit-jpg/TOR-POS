# TOR POS – Release-Index

<!-- TOR_RELEASE:R173|0.7.33.873|Merd-M -->

## Aktueller Release

**R173 · Merd-M · 0.7.33.873**

Verbindliche Quelle: `Desktop/src/TorPos.Core/ReleaseInfo.cs`.

### R173

- Drucker-Zentrale: Windows-Spooler-Metadaten werden konsequent über OpenPrinterW/GetPrinterW als Unicode gelesen; fehlerhafte CJK-/Mojibake-Zeichen bei Treiber/Port werden verhindert.
- Epson-Erkennung: Office-/Multifunktions-/Fax-Drucker wie ET-4850 gelten nicht mehr als Bondrucker. Nur bekannte Epson-TM-Profile bzw. eindeutig POS-/Receipt-typische Epson-Warteschlangen werden freigegeben.
- Nicht geeignete Office/PDF/Fax-Drucker bleiben zur Diagnose sichtbar, können aber nicht als Bondrucker übernommen, getestet oder für die Kassenschublade verwendet werden.

### R172

- SQLite: verbliebene unabhängige Schreibpfade für Karten-Erstattungsstatus, DATEV-Outbox/Kassenbuch und Schema-Migrationen laufen über die zentrale FIFO-IoQueue; WAL/busy-timeout bleiben aktiv.
- Swissbit: native WORM-API Geräte-, Transaktions-, Aktivierungs- und TAR-Aufrufe laufen standardmäßig in einem isolierten Hilfsprozess mit Hard-Timeout; bei Timeout/Abbruch wird der Worker-Prozess beendet und der bestehende TSE-Ausfallpfad greift.
- Mengen/Bestand: neue Primärspeicherung als skalierte INTEGER-Milli-Einheiten (1 kg = 1000, also 1 g = 1); REAL-Spalten bleiben nur als Abwärtskompatibilitäts-Mirror/Fallback für Altbestände erhalten.
- CI: R172-Regressionsvertrag prüft FIFO-Single-Writer, Watchdog-Isolation, Fixed-Point-Pfade und erzeugt nach erfolgreicher Prüfung ein ZIP des exakt committed Source-Trees.

### R171

- DSFinV-K 2.4 Export: frei wählbare Von-/Bis-Kalendertage, Preflight und Zielordnerauswahl; Export bleibt auf vollständige Z-Abschlusszeiträume begrenzt.
- Nach erfolgreichem DSFinV-K-Export bietet TOR direkte Weitergabe an: kompletter Ordner auf USB/Datenträger oder vollständiger Export als ZIP per aktivem TOR-Mail/Google/SMTP-Versand an frei wählbare E-Mail, inklusive Steuerberater-Schnellauswahl.
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
