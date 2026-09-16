# R55 Update-Veröffentlichung

Nur `PUBLISH-UPDATE.ps1` als Einstieg verwenden. Beispiel:

```powershell
.\PUBLISH-UPDATE.ps1 -SetupPath C:\Build\TOR-POS-Pro-Setup.exe -Version 0.7.33.550 -Revision R55 -SignerThumbprint <FREIGEGEBENER_ZERTIFIKAT_THUMBPRINT>
```

Ein tatsächlich gültig signiertes Setup und Node.js werden benötigt. Es wird
kein unsigniertes Update veröffentlicht. Bestehende Update-Dateien bleiben erhalten.
`DISABLE-UPDATE.ps1` schaltet die Freigabe über dasselbe atomare Manifest-Verfahren ab.
`TOR_CLOUD_UPDATES` kann den Zielordner festlegen. Kein automatisches UAC nötig,
aber Schreibrecht im Zielordner. `.publish.lock` nach einem Absturz nur entfernen,
wenn sicher kein Publisher mehr läuft. Alte hashbenannte EXE-Dateien nicht während
eines laufenden Downloads löschen. Dieses Paket setzt keinen produktiven Signer.
