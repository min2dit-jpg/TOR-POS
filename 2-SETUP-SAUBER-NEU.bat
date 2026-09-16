@echo off
setlocal EnableExtensions

REM ------------------------------------------------------------
REM Automatische Administrator-Anforderung
REM Doppelklick genuegt: falls dieser Prozess nicht erhoeht ist,
REM startet Windows dieselbe BAT automatisch mit UAC neu.
REM ------------------------------------------------------------
powershell -NoProfile -Command "exit([int](-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)))" >nul 2>&1
if errorlevel 1 (
  echo Administratorrechte werden angefordert...
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath 'cmd.exe' -ArgumentList '/c','""%~f0""' -Verb RunAs"
  exit /b
)

chcp 65001 >nul
cd /d "%~dp0"

title TOR POS Pro - Sauberer Release Build

echo ===============================================
echo TOR POS Pro - SAUBER NEU KOMPILIEREN
echo ===============================================
echo.
echo Diesen Modus verwenden:
echo - vor Kunden-Release
echo - nach Paket-/SDK-Aenderungen
echo - wenn der Schnellbuild merkwuerdige Fehler zeigt
echo.

if exist "publish\win-x64" rmdir /S /Q "publish\win-x64"
if exist "installer-output" rmdir /S /Q "installer-output"
if exist "src\TorPos.App\bin" rmdir /S /Q "src\TorPos.App\bin"
if exist "src\TorPos.App\obj" rmdir /S /Q "src\TorPos.App\obj"
if exist "src\TorPos.Infrastructure\bin" rmdir /S /Q "src\TorPos.Infrastructure\bin"
if exist "src\TorPos.Infrastructure\obj" rmdir /S /Q "src\TorPos.Infrastructure\obj"
if exist "src\TorPos.Core\bin" rmdir /S /Q "src\TorPos.Core\bin"
if exist "src\TorPos.Core\obj" rmdir /S /Q "src\TorPos.Core\obj"

echo Cache wurde geloescht.
echo Jetzt wird der normale Setup-Builder gestartet...
echo.
call "1-SETUP-ERSTELLEN.bat"
exit /b %errorlevel%
