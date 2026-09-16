@echo off
setlocal
cd /d "%~dp0"

echo ============================================================
echo TOR POS R74 - DATENBANK / WAL LASTTEST
echo ============================================================
echo.
echo Es wird NICHT die echte TOR-POS-Datenbank benutzt.
echo Der Test erzeugt unter %%TEMP%% eine Wegwerf-Datenbank mit:
echo   - 10.000 Artikeln
echo   - 500.000 Verkaeufen
echo   - 500.000 SaleItems
echo.
echo Je nach PC kann der Lauf einige Minuten dauern.
echo Die Messwerte werden unter verification\R74 gespeichert.
echo.
pause

dotnet build "tools\TorPos.DbStress\TorPos.DbStress.csproj" -c Release -m:1
set "BUILD_EXIT=%ERRORLEVEL%"
if not "%BUILD_EXIT%"=="0" goto :failed_build

dotnet run --project "tools\TorPos.DbStress\TorPos.DbStress.csproj" -c Release --no-build -- --report "%~dp0verification\R74"
set "TEST_EXIT=%ERRORLEVEL%"
if not "%TEST_EXIT%"=="0" goto :failed_test

echo.
echo ============================================================
echo DATENBANK / WAL LASTTEST ERFOLGREICH.
echo Report liegt unter Desktop\verification\R74
echo ============================================================
pause
exit /b 0

:failed_build
echo.
echo ============================================================
echo LASTTEST NICHT GESTARTET - BUILD FEHLGESCHLAGEN
echo Exit-Code: %BUILD_EXIT%
echo ============================================================
pause
exit /b 1

:failed_test
echo.
echo ============================================================
echo DATENBANK / WAL LASTTEST FEHLGESCHLAGEN
echo Exit-Code: %TEST_EXIT%
echo Temp-Datenbank/Report fuer Analyse aufbewahren.
echo ============================================================
pause
exit /b 1
