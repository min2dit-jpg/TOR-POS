@echo off
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0"

title TOR POS - Kundenlizenz erstellen

echo ===============================================
echo TOR POS - KUNDENLIZENZ ERSTELLEN
echo ===============================================
echo.

set "REQUEST=%~1"
if not defined REQUEST set /p "REQUEST=Pfad zur Aktivierungsanfrage: "

set "DAYS=365"
set /p "DAYS_INPUT=Gueltigkeit in Tagen [365]: "
if defined DAYS_INPUT set "DAYS=%DAYS_INPUT%"

set "PRIVATE_KEY=%~dp0TOR-POS-LICENSE-PRIVATE.pem"
if not exist "%PRIVATE_KEY%" set /p "PRIVATE_KEY=Pfad zum privaten Lizenzschluessel: "

set "OUTPUT=%USERPROFILE%\Desktop\TOR-POS-KUNDENLIZENZ.json"
set /p "OUTPUT_INPUT=Zieldatei [%OUTPUT%]: "
if defined OUTPUT_INPUT set "OUTPUT=%OUTPUT_INPUT%"

dotnet run --project "%~dp0TorPos.LicenseIssuer\TorPos.LicenseIssuer.csproj" -- ^
  --request "%REQUEST%" ^
  --days "%DAYS%" ^
  --key "%PRIVATE_KEY%" ^
  --output "%OUTPUT%"

if errorlevel 1 (
  echo.
  echo FEHLER: Lizenz konnte nicht erstellt werden.
  pause
  exit /b 1
)

echo.
echo FERTIG: %OUTPUT%
echo Diese Lizenzdatei kann an den angegebenen Kunden gesendet werden.
pause
