@echo off
setlocal
cd /d "%~dp0"
if exist "ChestCounter.exe" if exist "ChestCounter.exe.config" if exist "bin\replay-analyzer.exe" goto launch

echo Building Chest Counter for the first run...
echo For instant use without build tools, download FortniteChestCounter.zip from Releases.
echo.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0source\build.ps1"
if errorlevel 1 (
    echo.
    echo Build failed. See the message above and README.md for setup instructions.
    pause
    exit /b 1
)

:launch
start "" "%~dp0ChestCounter.exe"
