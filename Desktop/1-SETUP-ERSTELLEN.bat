@echo off
setlocal EnableExtensions EnableDelayedExpansion

REM ============================================================
REM TOR POS R66.1 - Fast / safe setup builder
REM - Build on Windows system drive: build directly, no staging
REM - Build from USB / other drive: mirror source to local SSD first
REM - Keep bin/obj in local workspace as incremental cache
REM ============================================================

powershell -NoProfile -Command "exit([int](-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)))" >nul 2>&1
if errorlevel 1 (
  echo Administratorrechte werden angefordert...
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath 'cmd.exe' -ArgumentList '/c','""%~f0""' -Verb RunAs"
  exit /b
)

chcp 65001 >nul
cd /d "%~dp0"
title TOR POS Pro - Schnelles Setup EXE

REM If we are already in the local staged workspace, never stage again.
if /I "%TOR_BUILD_STAGED%"=="1" goto :build_here

REM R66.1: robust rule. The Windows system drive is treated as local.
REM Anything else (USB, external SSD, secondary drive) is staged to LOCALAPPDATA.
if /I "%~d0"=="%SystemDrive%" goto :build_here
goto :stage_local_build

:stage_local_build
echo ===============================================
echo TOR POS Pro - SCHNELLES Windows Setup
echo ===============================================
echo.
echo R66.1: Nicht-Systemlaufwerk erkannt: %~d0
echo Systemlaufwerk: %SystemDrive%
echo.
echo Die Build-Arbeitskopie wird VOR der Kompilierung auf die lokale SSD
echo gespiegelt. Die Quelldateien auf %~d0 werden nicht veraendert.
echo.

set "TOR_LOCAL_WORK=%LOCALAPPDATA%\TOR-POS-Pro\BuildWorkspace-R66.1"
set "TOR_RETURN_DIR=%~dp0installer-output"
set "TOR_ROBOCOPY_LOG=%TEMP%\TOR-POS-R66.1-robocopy.log"

if not exist "%TOR_LOCAL_WORK%" mkdir "%TOR_LOCAL_WORK%"
if errorlevel 1 (
  echo FEHLER: Lokaler Build-Ordner konnte nicht erstellt werden:
  echo %TOR_LOCAL_WORK%
  pause
  exit /b 1
)

REM Keep local bin/obj as cache. Do not copy old publish/installer output.
robocopy "%~dp0." "%TOR_LOCAL_WORK%" /MIR /R:1 /W:1 /NFL /NDL /NP /XD bin obj publish installer-output .git > "%TOR_ROBOCOPY_LOG%" 2>&1
set "RC=!ERRORLEVEL!"
if !RC! GEQ 8 (
  echo.
  echo FEHLER: Lokale Build-Arbeitskopie konnte nicht erstellt werden.
  echo Robocopy-Code: !RC!
  echo Log: %TOR_ROBOCOPY_LOG%
  echo.
  type "%TOR_ROBOCOPY_LOG%"
  pause
  exit /b 1
)

echo Lokale Arbeitskopie bereit:
echo %TOR_LOCAL_WORK%
echo.

set "TOR_BUILD_STAGED=1"
set "TOR_BUILD_RETURN_DIR=%TOR_RETURN_DIR%"
call "%TOR_LOCAL_WORK%\1-SETUP-ERSTELLEN.bat"
set "RC=!ERRORLEVEL!"
exit /b !RC!

:build_here
echo ===============================================
echo TOR POS Pro - SCHNELLES Windows Setup
echo ===============================================
echo.
if /I "%TOR_BUILD_STAGED%"=="1" (
  echo Build-Ort: lokale SSD-Arbeitskopie
) else (
  echo Build-Ort: %~d0 ^(direkt - kein USB-Staging notwendig^)
)
echo.
echo HINWEIS:
echo Dieser Modus loescht bin/obj NICHT und nutzt den vorhandenen Build-Cache.
echo Dadurch sind Folge-Builds deutlich schneller.
echo.
echo Fuer eine komplett saubere Release-Kompilierung:
echo 2-SETUP-SAUBER-NEU.bat
echo.

set "STARTTIME=%TIME%"
set "TIMING_LOG=%~dp0TOR-POS-build-timing.txt"
> "%TIMING_LOG%" echo TOR POS R66.1 Build Timing
>>"%TIMING_LOG%" echo Start: %DATE% %TIME%
>>"%TIMING_LOG%" echo SourceDrive: %~d0
>>"%TIMING_LOG%" echo Staged: %TOR_BUILD_STAGED%

where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET 10 SDK wird einmalig installiert...
  where winget >nul 2>nul
  if errorlevel 1 (
    echo FEHLER: winget wurde nicht gefunden.
    echo Bitte .NET 10 SDK manuell installieren.
    pause
    exit /b 1
  )
  winget install --id Microsoft.DotNet.SDK.10 --exact --accept-package-agreements --accept-source-agreements
  if errorlevel 1 (
    echo FEHLER: .NET 10 SDK konnte nicht installiert werden.
    pause
    exit /b 1
  )
  set "PATH=%ProgramFiles%\dotnet;%PATH%"
)

echo.
echo TOR POS wird inkrementell kompiliert...
echo Beim ersten Lauf kann es laenger dauern. Danach wird der Build-Cache verwendet.
echo.
echo R75.1: Publish-Ordner wird vor jedem Setup neu erzeugt.
echo bin/obj bleiben als Build-Cache erhalten.

if exist "publish\win-x64" rmdir /S /Q "publish\win-x64"
if exist "installer-output" rmdir /S /Q "installer-output"

>>"%TIMING_LOG%" echo PublishStart: %TIME%
dotnet publish "src\TorPos.App\TorPos.App.csproj" ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=false ^
  -p:PublishTrimmed=false ^
  -p:PublishReadyToRun=false ^
  -p:DebugType=None ^
  -p:DebugSymbols=false ^
  -p:GenerateDocumentationFile=false ^
  -o "publish\win-x64" > "TOR-POS-build.log" 2>&1

if errorlevel 1 goto :build_fail
>>"%TIMING_LOG%" echo PublishEnd: %TIME%

REM Optional Swissbit SDK runtime. Vendor SDK is never bundled unless
REM developer placed the officially obtained x64 files here.
if exist "vendor\swissbit\windows64\WormAPI.dll" (
  if not exist "publish\win-x64\SwissbitSdk" mkdir "publish\win-x64\SwissbitSdk"
  copy /Y "vendor\swissbit\windows64\WormAPI.dll" "publish\win-x64\SwissbitSdk\WormAPI.dll" >nul
  if exist "vendor\swissbit\windows64\WormAPIUni.dll" (
    copy /Y "vendor\swissbit\windows64\WormAPIUni.dll" "publish\win-x64\SwissbitSdk\WormAPIUni.dll" >nul
  )
  echo Swissbit SDK: WormAPI.dll wird in Setup aufgenommen.
) else (
  echo Swissbit SDK: nicht eingebunden - TOR bleibt ohne reale TSE-Laufzeitbibliothek.
)

set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"

if not defined ISCC (
  echo.
  echo Inno Setup wird einmalig installiert...
  where winget >nul 2>nul
  if errorlevel 1 (
    echo FEHLER: winget wurde nicht gefunden.
    echo Bitte Inno Setup 6 manuell installieren.
    pause
    exit /b 1
  )
  winget install --id JRSoftware.InnoSetup --exact --accept-package-agreements --accept-source-agreements
  if errorlevel 1 (
    echo FEHLER: Inno Setup konnte nicht installiert werden.
    pause
    exit /b 1
  )
  if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
  if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
)

if not defined ISCC (
  echo FEHLER: ISCC.exe wurde nach der Installation nicht gefunden.
  pause
  exit /b 1
)

for /R "publish\win-x64" %%F in (*.pdb) do del /Q "%%F" >nul 2>&1

echo.
echo Windows-Installer wird erstellt...
>>"%TIMING_LOG%" echo InstallerStart: %TIME%
"%ISCC%" "TOR-POS-Pro-Setup.iss"
if errorlevel 1 goto :setup_fail
>>"%TIMING_LOG%" echo InstallerEnd: %TIME%

if not exist "installer-output\TOR-POS-Pro-Setup.exe" goto :setup_fail

call "8-PAKET-GROESSE-PRUEFEN.bat" /silent

if /I "%TOR_BUILD_STAGED%"=="1" if defined TOR_BUILD_RETURN_DIR (
  if not exist "%TOR_BUILD_RETURN_DIR%" mkdir "%TOR_BUILD_RETURN_DIR%"
  copy /Y "installer-output\TOR-POS-Pro-Setup.exe" "%TOR_BUILD_RETURN_DIR%\TOR-POS-Pro-Setup.exe" >nul
  if exist "installer-output\TOR-POS-size-report.txt" copy /Y "installer-output\TOR-POS-size-report.txt" "%TOR_BUILD_RETURN_DIR%\TOR-POS-size-report.txt" >nul
  copy /Y "%TIMING_LOG%" "%TOR_BUILD_RETURN_DIR%\TOR-POS-build-timing.txt" >nul
)

>>"%TIMING_LOG%" echo Ende: %DATE% %TIME%

echo.
echo ===============================================
echo FERTIG - SCHNELLBUILD R66.1
echo ===============================================
echo.
echo Kunden-Setup:
echo %~dp0installer-output\TOR-POS-Pro-Setup.exe
echo.
echo Start: %STARTTIME%
echo Ende : %TIME%
echo.
echo Das Setup wird jetzt von der lokalen Build-Position gestartet.
start "" "%~dp0installer-output\TOR-POS-Pro-Setup.exe"
exit /b 0

:build_fail
echo.
echo FEHLER beim Kompilieren von TOR POS.
echo Build-Log:
echo %~dp0TOR-POS-build.log
echo.
type "TOR-POS-build.log"
pause
exit /b 1

:setup_fail
echo.
echo FEHLER beim Erstellen des Windows-Installers.
pause
exit /b 1
