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
setlocal EnableExtensions
cd /d "%~dp0"
set "DOTNET_EXE="
if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET_EXE=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET_EXE where dotnet >nul 2>nul && for /f "delims=" %%D in ('where dotnet') do set "DOTNET_EXE=%%D"
if not defined DOTNET_EXE (
  echo .NET 10 SDK yok. Once 1-SETUP-ERSTELLEN.bat calistirin.
  pause
  exit /b 1
)
"%DOTNET_EXE%" restore "src\TorPos.App\TorPos.App.csproj" || goto :fail
"%DOTNET_EXE%" build "src\TorPos.App\TorPos.App.csproj" -c Release --no-restore || goto :fail
if exist "publish\win-x64" rmdir /S /Q "publish\win-x64"
"%DOTNET_EXE%" publish "src\TorPos.App\TorPos.App.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false -o "publish\win-x64" || goto :fail
start "" explorer.exe "publish\win-x64"
exit /b 0
:fail
echo Derleme basarisiz. Ilk hata satirlarini bana gonderin.
pause
exit /b 1
