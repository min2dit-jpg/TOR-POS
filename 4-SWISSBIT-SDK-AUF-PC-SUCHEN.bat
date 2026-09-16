@echo off
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0"

title TOR POS Pro - Swissbit WormAPI.dll suchen

echo ====================================================
echo TOR POS Pro - SWISSBIT SDK AUF DIESEM PC SUCHEN
echo ====================================================
echo.
echo Es wird nur nach WormAPI.dll gesucht.
echo Dateien werden NICHT geloescht, verschoben oder kopiert.
echo.

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$roots=@($env:ProgramFiles,${env:ProgramFiles(x86)},$env:LOCALAPPDATA,$env:ProgramData,[Environment]::GetFolderPath('Desktop'),(Join-Path $env:USERPROFILE 'Downloads')) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique;" ^
  "$hits=@();" ^
  "foreach($r in $roots){ Write-Host ('Suche: ' + $r); try { $hits += Get-ChildItem -LiteralPath $r -Filter WormAPI.dll -File -Recurse -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName } catch {} };" ^
  "$hits=$hits | Select-Object -Unique;" ^
  "if($hits.Count -eq 0){ Write-Host ''; Write-Host '[NICHT GEFUNDEN] Keine WormAPI.dll auf diesem PC.' -ForegroundColor Yellow; exit 2 };" ^
  "Write-Host ''; Write-Host '[GEFUNDEN]' -ForegroundColor Green; $hits | ForEach-Object { Write-Host $_ };" ^
  "$hits | Set-Content -Encoding UTF8 '%~dp0SWISSBIT-SDK-GEFUNDEN.txt';" ^
  "Write-Host ''; Write-Host 'Liste gespeichert: SWISSBIT-SDK-GEFUNDEN.txt'"

echo.
echo Danach TOR POS oeffnen:
echo Einstellungen ^> TSE-Aktivierung
echo und WORMAPI.DLL AUSWAEHLEN verwenden.
echo.
pause
