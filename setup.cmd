@echo off
:: Self-elevate
net session >nul 2>&1
if errorlevel 1 (
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

set EXE=%~dp0src\ChoreBuddy.TestApp\bin\Debug\net8.0-windows\ChoreBuddy.TestApp.exe
if not exist "%EXE%" (
    echo Building first...
    pushd "%~dp0src\ChoreBuddy.TestApp"
    dotnet build
    popd
)

start "" "%EXE%" --setup
