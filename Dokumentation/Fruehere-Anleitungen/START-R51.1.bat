@echo off
setlocal
cd /d "%~dp0"
start "TOR Cloud R51.1" /D "%~dp0Cloud" cmd /c START-TOR-CLOUD.bat
start "TOR POS R51.1" /D "%~dp0Desktop" cmd /c 2-NUR-ENTWICKLUNG-DIREKT-STARTEN.bat
