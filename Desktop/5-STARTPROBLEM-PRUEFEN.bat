@echo off
setlocal EnableExtensions
chcp 65001 >nul

title TOR POS Pro - Startdiagnose

REM ------------------------------------------------------------
REM R12: Immer zuerst die aktuelle Program-Files-Installation.
REM LocalAppData ist nur noch Legacy-Fallback und wird klar markiert.
REM ------------------------------------------------------------
set "APP="
set "SOURCE="

if exist "%ProgramFiles%\TOR POS Pro\TorPos.App.exe" (
  set "APP=%ProgramFiles%\TOR POS Pro\TorPos.App.exe"
  set "SOURCE=AKTUELLE INSTALLATION - Program Files"
)

if not defined APP if exist "%ProgramFiles(x86)%\TOR POS Pro\TorPos.App.exe" (
  set "APP=%ProgramFiles(x86)%\TOR POS Pro\TorPos.App.exe"
  set "SOURCE=AKTUELLE INSTALLATION - Program Files x86"
)

if not defined APP if exist "%LOCALAPPDATA%\Programs\TOR POS Pro\TorPos.App.exe" (
  set "APP=%LOCALAPPDATA%\Programs\TOR POS Pro\TorPos.App.exe"
  set "SOURCE=ALTE / LEGACY INSTALLATION - LocalAppData"
)

set "LOG=%LOCALAPPDATA%\TOR POS Pro\Logs\TOR-POS-startup.log"

echo ===============================================
echo TOR POS Pro - STARTDIAGNOSE R12
echo ===============================================
echo.

if not defined APP (
  echo [FEHLT] Keine TOR POS Programmdatei gefunden.
  echo Erwartet wurde zuerst:
  echo %ProgramFiles%\TOR POS Pro\TorPos.App.exe
  echo.
  echo Bitte R12-Setup erneut installieren.
  pause
  exit /b 1
)

echo [OK] Programmdatei gefunden.
echo Quelle: %SOURCE%
echo EXE   : %APP%
echo.

if /I "%SOURCE%"=="ALTE / LEGACY INSTALLATION - LocalAppData" (
  echo [ACHTUNG] Es wurde nur eine alte LocalAppData-Installation gefunden.
  echo Das ist NICHT der aktuelle R12-Installationspfad.
  echo.
)

echo TOR POS wird mit korrektem Arbeitsordner gestartet...
for %%I in ("%APP%") do set "APPDIR=%%~dpI"
pushd "%APPDIR%"
start "" /D "%APPDIR%" "%APP%"
popd

timeout /t 5 /nobreak >nul

echo.
if exist "%LOG%" (
  echo [LOG GEFUNDEN]
  echo %LOG%
  echo.
  echo Die letzten 80 Zeilen:
  echo ------------------------------------------------
  powershell -NoProfile -Command "Get-Content -LiteralPath '%LOG%' -Tail 80"
  echo ------------------------------------------------
  echo.
  echo Log wird jetzt in Notepad geoeffnet.
  start "" notepad.exe "%LOG%"
) else (
  echo [KEIN LOG] Noch keine Start-Logdatei gefunden.
)

echo.
pause
