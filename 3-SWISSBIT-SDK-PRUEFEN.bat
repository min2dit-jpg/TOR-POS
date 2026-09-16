@echo off
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0"

title TOR POS Pro - Swissbit SDK Pruefung

echo ===============================================
echo TOR POS Pro - SWISSBIT SDK PRUEFUNG
echo ===============================================
echo.

set "SDKDIR=%~dp0vendor\swissbit\windows64"

if not exist "%SDKDIR%\WormAPI.dll" (
  echo [FEHLT] WormAPI.dll
  echo.
  echo Lege die offizielle Windows-64-Bit WormAPI.dll hier ab:
  echo %SDKDIR%
  echo.
  echo NICHT von DLL-Download-Webseiten herunterladen.
  pause
  exit /b 1
)

echo [OK] WormAPI.dll
for %%F in ("%SDKDIR%\WormAPI.dll") do echo      Groesse: %%~zF Bytes

if exist "%SDKDIR%\WormAPIUni.dll" (
  echo [OK] WormAPIUni.dll
  for %%F in ("%SDKDIR%\WormAPIUni.dll") do echo      Groesse: %%~zF Bytes
) else (
  echo [INFO] WormAPIUni.dll nicht vorhanden.
  echo        Ob sie benoetigt wird, haengt vom konkreten offiziellen SDK-Paket ab.
)

echo.
echo SDK-Dateien gefunden.
echo Jetzt 1-SETUP-ERSTELLEN.bat starten.
echo Danach in TOR POS:
echo Einstellungen ^> TSE-Aktivierung ^> SDK / TSE PRUEFEN
echo.
pause
exit /b 0
