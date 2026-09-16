@echo off
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0"

set "SRC=%~dp0installer-output\TOR-POS-Pro-Setup.exe"
if not exist "%SRC%" (
  echo FEHLER: installer-output\TOR-POS-Pro-Setup.exe wurde nicht gefunden.
  echo Zuerst 1-SETUP-ERSTELLEN.bat ausfuehren.
  pause
  exit /b 1
)

set "DSTDIR=%TEMP%\TOR-POS-Pro-Installer"
if not exist "%DSTDIR%" mkdir "%DSTDIR%"
copy /Y "%SRC%" "%DSTDIR%\TOR-POS-Pro-Setup.exe" >nul
if errorlevel 1 (
  echo FEHLER: Setup konnte nicht auf die lokale SSD kopiert werden.
  pause
  exit /b 1
)

echo Setup wurde lokal zwischengespeichert:
echo %DSTDIR%\TOR-POS-Pro-Setup.exe
echo.
start "" "%DSTDIR%\TOR-POS-Pro-Setup.exe"
exit /b 0
