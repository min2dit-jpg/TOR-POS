@echo off
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0"
set "REPORT=installer-output\TOR-POS-size-report.txt"
if not exist "installer-output" mkdir "installer-output"

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "$pub=Join-Path (Get-Location) 'publish\win-x64';" ^
  "$setup=Join-Path (Get-Location) 'installer-output\TOR-POS-Pro-Setup.exe';" ^
  "$report=Join-Path (Get-Location) 'installer-output\TOR-POS-size-report.txt';" ^
  "$lines=New-Object System.Collections.Generic.List[string];" ^
  "$lines.Add('TOR POS R66 Paket-Groessenbericht');" ^
  "$lines.Add('Erstellt: '+(Get-Date -Format 'yyyy-MM-dd HH:mm:ss'));" ^
  "$lines.Add('');" ^
  "if(Test-Path $pub){$files=Get-ChildItem -LiteralPath $pub -File -Recurse;$sum=($files|Measure-Object Length -Sum).Sum;$lines.Add(('Installationsdateien: {0:N1} MB / {1} Dateien' -f ($sum/1MB),$files.Count));$lines.Add('');$lines.Add('25 groesste Publish-Dateien:');$files|Sort-Object Length -Descending|Select-Object -First 25|ForEach-Object{$lines.Add(('{0,9:N1} MB  {1}' -f ($_.Length/1MB),($_.FullName.Substring($pub.Length).TrimStart('\'))))}}else{$lines.Add('Publish-Ordner fehlt.');};" ^
  "$lines.Add('');" ^
  "if(Test-Path $setup){$s=Get-Item -LiteralPath $setup;$lines.Add(('Setup EXE: {0:N1} MB' -f ($s.Length/1MB)))}else{$lines.Add('Setup EXE: noch nicht vorhanden.');};" ^
  "$lines.Add('');" ^
  "$lines.Add('Hinweis: Self-contained .NET bleibt absichtlich aktiv. Das macht die Installation groesser, vermeidet aber eine separate .NET-Runtime-Abhaengigkeit auf Kunden-PCs.');" ^
  "$lines|Set-Content -LiteralPath $report -Encoding UTF8;" ^
  "$lines|ForEach-Object{Write-Host $_}"

if /I "%~1"=="/silent" exit /b 0
echo.
echo Bericht gespeichert unter:
echo %~dp0%REPORT%
pause
