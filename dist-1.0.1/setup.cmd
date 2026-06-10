@echo off
:: Self-elevate (re-launch as admin if not already)
net session >/dev/null 2>&1
if errorlevel 1 (
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)
:: Launch the wizard
start "" "%~dp0ChoreBuddy.TestApp.exe" --setup
