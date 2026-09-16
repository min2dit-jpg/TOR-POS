@echo off
setlocal
cd /d "%~dp0"

dotnet build "tests\TorPos.SafetyTests\TorPos.SafetyTests.csproj" -c Release -m:1
set "BUILD_EXIT=%ERRORLEVEL%"
if not "%BUILD_EXIT%"=="0" goto :failed_build

dotnet run --project "tests\TorPos.SafetyTests\TorPos.SafetyTests.csproj" -c Release --no-build
set "TEST_EXIT=%ERRORLEVEL%"
if not "%TEST_EXIT%"=="0" goto :failed_test

echo.
echo ===============================================
echo Sicherheitstests erfolgreich.
echo Die erwartete Gesamtzahl wurde vom Testprogramm selbst geprueft.
echo ===============================================
pause
exit /b 0

:failed_build
echo.
echo ===============================================
echo SICHERHEITSTESTS NICHT GESTARTET
echo Build fehlgeschlagen. Exit-Code: %BUILD_EXIT%
echo Ausgabe fuer Service aufbewahren.
echo ===============================================
pause
exit /b 1

:failed_test
echo.
echo ===============================================
echo SICHERHEITSTESTS FEHLGESCHLAGEN
echo Testprozess Exit-Code: %TEST_EXIT%
echo KEINE Erfolgsmeldung wird ausgegeben.
echo Ausgabe fuer Service aufbewahren.
echo ===============================================
pause
exit /b 1
