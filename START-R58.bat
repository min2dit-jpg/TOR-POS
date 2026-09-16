@echo off
setlocal
cd /d "%~dp0"
start "TOR Cloud R58" /D "%~dp0Cloud" cmd /c START-TOR-CLOUD.bat
start "TOR POS R58" /D "%~dp0Desktop" cmd /c 2-NUR-ENTWICKLUNG-DIREKT-STARTEN.bat
