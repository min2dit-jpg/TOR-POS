@echo off
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0"
title TOR POS - 7-Tage-Demo Setup

echo ===============================================
echo TOR POS - 7-TAGE-DEMO BUILD
echo ===============================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
  echo FEHLER: .NET 10 SDK wurde nicht gefunden.
  exit /b 1
)

if exist "publish\demo-win-x64" rmdir /S /Q "publish\demo-win-x64"
if exist "installer-output\TOR-POS-Demo-Setup.exe" del /Q "installer-output\TOR-POS-Demo-Setup.exe"

dotnet publish "src\TorPos.App\TorPos.App.csproj" ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:TorDemoBuild=true ^
  -p:PublishSingleFile=false ^
  -p:PublishTrimmed=false ^
  -p:PublishReadyToRun=false ^
  -p:DebugType=None ^
  -p:DebugSymbols=false ^
  -o "publish\demo-win-x64"

if errorlevel 1 (
  echo FEHLER: Demo-Publish fehlgeschlagen.
  exit /b 1
)

set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"

if not defined ISCC (
  echo FEHLER: Inno Setup 6 wurde nicht gefunden.
  exit /b 1
)

"%ISCC%" /DMyOutputBaseFilename=TOR-POS-Demo-Setup /DMyPublishDir=publish\demo-win-x64 "TOR-POS-Pro-Setup.iss"
if errorlevel 1 (
  echo FEHLER: Demo-Installer konnte nicht erstellt werden.
  exit /b 1
)

if not exist "installer-output\TOR-POS-Demo-Setup.exe" (
  echo FEHLER: TOR-POS-Demo-Setup.exe fehlt.
  exit /b 1
)

echo.
echo FERTIG:
echo %~dp0installer-output\TOR-POS-Demo-Setup.exe
echo.
echo WICHTIG: Vor Veröffentlichung mit dem TOR Code-Signing-Zertifikat signieren
echo und danach Cloud\PUBLISH-DEMO.ps1 verwenden.
exit /b 0
