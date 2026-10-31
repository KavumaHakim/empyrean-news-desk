@echo off
rem Rebuilds the Release app so the latest code changes are always what launches.
cd /d "%~dp0"

tasklist /FI "IMAGENAME eq NewsDesk.App.exe" | find /I "NewsDesk.App.exe" >nul
if not errorlevel 1 (
    echo NEWS DESK is already running. Close it first, then run this again.
    pause
    exit /b 1
)

echo Building NEWS DESK...
dotnet build "%~dp0NewsDesk.App\NewsDesk.App.csproj" -c Release -v q -nologo
if errorlevel 1 (
    echo.
    echo Build failed - see the errors above. The app was not started.
    pause
    exit /b 1
)

cd /d "%~dp0NewsDesk.App\bin\Release\net8.0-windows"
start "" "%~dp0NewsDesk.App\bin\Release\net8.0-windows\NewsDesk.App.exe"
