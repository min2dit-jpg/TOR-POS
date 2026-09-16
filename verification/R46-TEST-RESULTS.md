# R46 Test Results

## Cloud
- `npm run check`: **bestanden**.
- `npm test`: **13/13 bestanden**.
- Neu geprüft: Verkauf reduziert Cloud-Bestand genau einmal; Duplicate-Verkauf reduziert nicht erneut; verspäteter Verkauf kann einen neueren vollständigen Bestandssnapshot nicht zurückrechnen.
- Bestehende Prüfungen bleiben enthalten: Login/Tenant-Trennung, Belegdetails, Validierung/Rollback, Bestandssnapshot, R42 Artikelinfos, Berlin/DST, Pagination, Update-API, TOTP-2FA, Recovery/Sicherheitsregeln.

## Desktop statisch
- XAML / CSProj XML: strukturell lesbar.
- JSON: strukturell lesbar.
- 54 C#-Dateien: tokenbasierte Klammer-/Stringstruktur ohne Auffälligkeit.
- R46-Schlüsselpfade vorhanden: `Software & Update`, Techniker-Updatequelle, automatisches Stock-Dirty-Signal, Snapshot-Coalescing.
- SumUp-Verbindungsdateien sind SHA-256-identisch zu R45.

## Nicht in dieser Umgebung möglich
- Kein .NET 10 SDK vorhanden; deshalb kein echter Windows/Avalonia-Compile.
- Keine reale Windows-Drucker-/Scanner-/TSE-Hardwareabnahme.
- Remote Auto-Update bleibt bis Code-Signing-Zertifikat + fest hinterlegtem Thumbprint produktiv gesperrt.

Windows-Test: `Desktop\1-SETUP-ERSTELLEN.bat`.
