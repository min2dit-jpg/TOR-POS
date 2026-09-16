@echo off
setlocal EnableExtensions
chcp 65001 >nul

title TOR POS Pro - Normalstart testen
set "APP=%ProgramFiles%\TOR POS Pro\TorPos.App.exe"

if not exist "%APP%" (
  echo [FEHLT] Aktuelle Installation wurde nicht gefunden:
  echo %APP%
  echo.
  echo Bitte zuerst installer-output\TOR-POS-Pro-Setup.exe installieren.
  pause
  exit /b 1
)

for %%I in ("%APP%") do set "APPDIR=%%~dpI"
echo TOR POS wird aus der aktuellen Installation gestartet:
echo %APP%
echo.
start "" /D "%APPDIR%" "%APP%"
exit /b 0
