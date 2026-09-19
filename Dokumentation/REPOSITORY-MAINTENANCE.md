# Repository Maintenance

Stand: 2026-09-19

## Größe

GitHub meldet für das Repository ungefähr 289 MB Historie. Der aktuelle `main`-Tree umfasst jedoch nur rund **4.64 MiB** an getrackten Dateien.

Die Differenz stammt überwiegend aus historischen Build-/Release-Artefakten. `Desktop/.gitignore` dokumentiert, dass früher u. a. ein ca. 44-MB-Installer und zahlreiche Runtime-DLLs in die Git-Historie gelangten.

## Entscheidung

Die Historie wird derzeit **nicht** neu geschrieben:
- aktuelle Dateien sind klein und sauber;
- offene/draft Branches und PRs würden durch Force-Rewrite unnötig gefährdet;
- fiskalische Entwicklungsnachweise und Commit-Historie sollen nachvollziehbar bleiben.

Eine History-Rewrite-Aktion ist nur gerechtfertigt, wenn Speicher-/Clone-Probleme tatsächlich operativ relevant werden und vorher alle Branches/Tags koordiniert wurden.

## Regeln für neue Dateien

Nicht committen:
- `bin/`, `obj/`
- `publish/`, `installer-output/`
- generierte Setup-Dateien
- Runtime-DLL-Pakete
- SDK-Archive
- Quellcode-ZIPs
- Secrets, private Keys, Lizenzdateien

Dafür gelten die Root- und Desktop-`.gitignore`-Regeln.

## Aktueller Tree

Der aktuelle Tree besteht überwiegend aus C#-Quellcode, Markdown/JSON-Dokumentation und kleinen Verifikationslogs. Es gibt im aktuellen `main` keinen Hinweis auf einen großen gebündelten SDK-/Installer-Blob als Ursache der 289-MB-Anzeige.
