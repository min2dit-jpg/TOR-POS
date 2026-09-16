@echo off
setlocal
cd /d "%~dp0"

REM ============================================================
REM R121: runs BOTH suites in one go - the Desktop safety tests
REM and the Cloud tests. Until version control and CI exist, this
REM is the single gate to run before shipping anything.
REM ============================================================

echo ===============================================
echo 1/2  DESKTOP - Sicherheitstests
echo ===============================================
pushd Desktop
dotnet build "tests\TorPos.SafetyTests\TorPos.SafetyTests.csproj" -c Release -m:1
if not "%ERRORLEVEL%"=="0" (popd & goto :failed_desktop_build)
dotnet run --project "tests\TorPos.SafetyTests\TorPos.SafetyTests.csproj" -c Release --no-build
if not "%ERRORLEVEL%"=="0" (popd & goto :failed_desktop)
REM R127: once more under en-US - the culture of the GitHub test runner.
REM A test that only passes on a German Windows would otherwise first fail there.
set TOR_TEST_CULTURE=en-US
dotnet run --project "tests\TorPos.SafetyTests\TorPos.SafetyTests.csproj" -c Release --no-build
set "RC=%ERRORLEVEL%"
set TOR_TEST_CULTURE=
if not "%RC%"=="0" (popd & goto :failed_desktop)
popd

echo.
echo ===============================================
echo 2/2  CLOUD - Node-Tests
echo ===============================================
where node >nul 2>nul
if not "%ERRORLEVEL%"=="0" goto :no_node
pushd Cloud
call npm test
if not "%ERRORLEVEL%"=="0" (popd & goto :failed_cloud)
popd

echo.
echo ===============================================
echo ALLE TESTS ERFOLGREICH - Desktop und Cloud.
echo Die erwartete Pruefungszahl pruefen die Testprogramme selbst.
echo ===============================================
pause
exit /b 0

:no_node
echo.
echo ===============================================
echo CLOUD-TESTS UEBERSPRUNGEN
echo Node.js wurde nicht gefunden. Desktop-Tests sind bestanden.
echo Node 22+ installieren, um die Cloud-Tests mitzupruefen.
echo ===============================================
pause
exit /b 0

:failed_desktop_build
echo.
echo ===============================================
echo ABBRUCH: Desktop-Build fehlgeschlagen.
echo ===============================================
pause
exit /b 1

:failed_desktop
echo.
echo ===============================================
echo DESKTOP-SICHERHEITSTESTS FEHLGESCHLAGEN
echo Ausgabe fuer Service aufbewahren. Nichts ausliefern.
echo ===============================================
pause
exit /b 1

:failed_cloud
echo.
echo ===============================================
echo CLOUD-TESTS FEHLGESCHLAGEN
echo Desktop war in Ordnung, Cloud nicht. Nichts ausliefern.
echo ===============================================
pause
exit /b 1
