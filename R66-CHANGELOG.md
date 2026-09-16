# TOR POS R66 – Installer Speed & Safe Upgrade

## Gefundene Ursachen
- Build wurde auf dem Testgerät direkt von einem USB-Laufwerk (D:) gestartet. .NET self-contained publish erzeugt/liest viele kleine Dateien und ist auf USB deutlich langsamer.
- Beim Update war TorPos.App.exe bzw. eine von TOR POS benutzte Datei noch geöffnet. Inno Setup wartete auf Restart Manager und zeigte anschließend den generischen Dialog „konnte nicht alle Anwendungen automatisch schließen“.

## Änderungen
- Startet `1-SETUP-ERSTELLEN.bat` von einem Wechseldatenträger, wird die Quelle automatisch nach `%LOCALAPPDATA%\TOR-POS-Pro\BuildWorkspace-R66` gespiegelt und dort kompiliert.
- Der lokale `bin/obj` Build-Cache bleibt zwischen Folge-Builds erhalten.
- Das fertige Setup und der Größenbericht werden zurück in den ursprünglichen `installer-output` Ordner kopiert.
- TOR POS besitzt ab R66 zusätzlich den festen Mutex `TOR-POS-Pro-Running`.
- Der Installer prüft diesen Mutex vor Dateizugriffen.
- Für R65 und ältere Versionen prüft der Installer vor dem letzten Wizard-Schritt zusätzlich auf `TorPos.App.exe`.
- Eine laufende Kasse wird NICHT mit `taskkill` beendet. Der Benutzer muss TOR POS normal schließen, damit Exit-Backup und Geräte-Shutdown sauber laufen.
- `SolidCompression=no`: Update-Installationen müssen nicht mehr aus einem großen Solid-Archiv lesen; das Setup kann etwas größer werden, ist auf älteren PCs/USB aber weniger blockierend.

## Wenn der alte R65-Installer bereits den Dialog zeigt
Nicht `Den Fehler ignorieren und fortfahren` wählen. TOR POS normal schließen und `Nochmals versuchen`; wenn das nicht möglich ist, Installation abbrechen.

## Abnahme
1. `1-SETUP-ERSTELLEN.bat` direkt von USB starten: Meldung `USB-Laufwerk erkannt` muss erscheinen.
2. Zweiter Build derselben R66-Quelle sollte den lokalen Cache verwenden.
3. TOR POS öffnen und R66-Setup starten: Setup muss früh auf die laufende Kasse hinweisen statt lange bei Dateikopie zu warten.
4. TOR POS normal schließen, Setup erneut fortsetzen.
5. Nach Installation TOR POS starten und Version 0.7.33.660 prüfen.
