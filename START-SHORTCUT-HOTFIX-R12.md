# TOR POS Pro v0.7.33 R12 - Start / Shortcut Hotfix

- Der Installer entfernt alte TOR-POS-Desktop- und Startmenue-Links vor dem Erstellen der neuen Verknuepfungen.
- Die neue Desktop-Verknuepfung zeigt eindeutig auf `{app}\\TorPos.App.exe` im aktuellen Program-Files-Installationsordner.
- Startmenue-Verknuepfung wurde wieder aufgenommen.
- `5-STARTPROBLEM-PRUEFEN.bat` sucht ab R12 zuerst die aktuelle Program-Files-Installation. Die alte LocalAppData-Version ist nur noch ein deutlich markierter Legacy-Fallback.
- `6-NORMALSTART-PRUEFEN.bat` startet ausschliesslich die aktuelle Program-Files-Version und eignet sich zum schnellen Gegencheck.
- Keine Kassendaten werden geloescht; die persistenten TOR-POS-Daten bleiben unter dem bestehenden AppData-Datenpfad erhalten.
