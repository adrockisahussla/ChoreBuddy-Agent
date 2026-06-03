@echo off
:: Self-elevate to admin if not already
net session >nul 2>&1
if errorlevel 1 (
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0src\ChoreBuddy.TestApp\bin\Debug\net8.0-windows"
if not exist "ChoreBuddy.TestApp.exe" (
    echo Building...
    cd /d "%~dp0src\ChoreBuddy.TestApp"
    dotnet build
    cd /d "%~dp0src\ChoreBuddy.TestApp\bin\Debug\net8.0-windows"
)

start "" "ChoreBuddy.TestApp.exe"
