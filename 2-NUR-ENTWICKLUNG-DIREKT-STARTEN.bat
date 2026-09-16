@echo off
cd /d "%~dp0"
echo NUR FUER ENTWICKLUNG - KEIN KUNDENINSTALLER
dotnet run --project "src\TorPos.App\TorPos.App.csproj"
pause
