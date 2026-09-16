# TOR POS R63 – Core Stability & Cleanup

Version: **0.7.33.630**
Basis: **R62-Google-OAuth-QR**

## Ziel
R63 reduziert unnötige Installations- und Laufzeitreste, ohne für Kunden wichtige Abhängigkeiten oder fiskalische Daten zu entfernen. Stabilität hat Vorrang vor aggressiver Größenoptimierung.

## Setup / Installationsgröße
- `publish\win-x64` wird vor jedem Setup-Build vollständig neu erstellt. Dadurch können DLLs aus älteren Versionen nicht mehr versehentlich im Installer verbleiben.
- `bin/obj` bleiben beim Schnellbuild erhalten; die Compiler-Caches werden also nicht unnötig gelöscht.
- Release-Publish erzwingt `DebugType=None`, `DebugSymbols=false` und entfernt versehentliche `.pdb`-Dateien vor dem Inno-Setup-Build.
- `PublishReadyToRun=false` bleibt explizit, weil ReadyToRun die Dateigröße erhöhen kann.
- `PublishTrimmed=false` bleibt bewusst aktiv: aggressives Trimming wird bei Avalonia/Reflection-Abhängigkeiten nicht riskiert.
- `--self-contained true` bleibt bewusst aktiv. Kunden-PCs benötigen damit keine separat vorinstallierte .NET-10-Runtime. Das ist größer, aber für einen kommerziellen Kassen-PC robuster.
- Das lose `TorPos.ico` wird nicht mehr zusätzlich in den Publish-Ordner kopiert; das Icon bleibt als ApplicationIcon im Programm eingebettet.
- `LICENSE-SYSTEM-DE.md` ist Entwicklerdokumentation und wird nicht mehr in die Kundeninstallation kopiert.
- Das Splash-Logo `TorPos-Magnifier.png` wurde von 1254×1254 auf 512×512 optimiert. In der Oberfläche wird es nur mit 138 DIP angezeigt; die visuelle Qualität bleibt damit deutlich oberhalb der benötigten Auflösung.

## Paketgrößen-Bericht
Neu: `Desktop\8-PAKET-GROESSE-PRUEFEN.bat`

Nach einem Windows-Setup-Build schreibt das Script automatisch:
- unkomprimierte Größe von `publish\win-x64`
- Anzahl der Installationsdateien
- Größe der fertigen Setup-EXE
- die 25 größten Publish-Dateien

in `installer-output\TOR-POS-size-report.txt`.

## Laufzeit-Aufräumen
Nur klar wegwerfbare Update-Cache-Dateien werden automatisch bereinigt:
- abgebrochene `*.download`-Dateien älter als 24 Stunden
- alte Update-Installer werden begrenzt; die zwei neuesten bleiben erhalten
- ein aktuell als `update.staged_path` vorgemerkter Installer wird niemals durch die Bereinigung gelöscht

Explizit **nicht** automatisch gelöscht:
- Kassendatenbank
- Verkaufs-/Auditdaten
- TSE-/Fiskaldaten
- Produktbilder und Bon-Assets
- Backups
- Recovery-/Quarantänebelege

## Sicherheitstests
Zwei neue Tests prüfen die Update-Cache-Bereinigung. Ziel nach Windows-Test: **ALL 242 CHECKS PASSED**.

## Noch offen
- Der tatsächliche Windows-Publish und die exakte Setup-Größe können in der Generierungsumgebung nicht ausgeführt werden, da dort kein .NET 10 SDK/MSBuild vorhanden ist.
- TSE/DSFinV-K bleiben bis zur finalen Hardware-/Produktivprüfung gesperrt.
