@echo off
setlocal
cd /d "%~dp0"
where node >nul 2>nul
if errorlevel 1 (
  echo Node.js bulunamadi. Node.js 22.13 veya daha yeni surum kurun.
  pause
  exit /b 1
)
node -e "require('node:sqlite')" >nul 2>nul
if errorlevel 1 (
  echo Node.js sqlite destegi bulunamadi. Node.js 22.13 veya daha yeni surum gerekir.
  pause
  exit /b 1
)
set TOR_CLOUD_DEMO=true
set HOST=127.0.0.1
set PORT=8787
start "TOR POS Cloud" http://127.0.0.1:8787
node server.js
pause
