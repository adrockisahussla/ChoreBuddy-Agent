# Installs the ChoreBuddy Agent as a Windows Service + registers the user-session overlay autostart.
# Must run as administrator.

#Requires -RunAsAdministrator

$ErrorActionPreference = "Stop"

$serviceName = "ChoreBuddyAgent"
$displayName = "ChoreBuddy Firewall Agent"
$description = "Polls ChoreBuddy cloud for app shutoff commands and enforces them on this PC."

# Resolve absolute exe path
$exe = Join-Path $PSScriptRoot "src\ChoreBuddy.TestApp\bin\Debug\net8.0-windows\ChoreBuddy.TestApp.exe"

if (-not (Test-Path $exe)) {
    Write-Host "Building project first..." -ForegroundColor Yellow
    Push-Location (Join-Path $PSScriptRoot "src\ChoreBuddy.TestApp")
    & dotnet build -c Debug
    Pop-Location
}

if (-not (Test-Path $exe)) {
    Write-Host "ERROR: $exe not found. Build failed." -ForegroundColor Red
    exit 1
}

# === SERVICE ===
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Write-Host "Stopping and removing existing service..."
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
}

Write-Host "Creating service $serviceName..."
$binPath = "`"$exe`" --service"
sc.exe create $serviceName binPath= $binPath start= auto displayName= $displayName | Out-Null
sc.exe description $serviceName $description | Out-Null
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null

Write-Host "Starting service..."
Start-Service -Name $serviceName

# === WATCHDOG ===
$taskName = "ChoreBuddyAgentWatchdog"
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue

$action = New-ScheduledTaskAction -Execute "powershell.exe" `
    -Argument "-NoProfile -WindowStyle Hidden -Command `"if ((Get-Service -Name '$serviceName' -ErrorAction SilentlyContinue).Status -ne 'Running') { Start-Service -Name '$serviceName' }`""
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 1)
$principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -Hidden -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings | Out-Null

# === OVERLAY AUTOSTART (per-user HKCU\Run for the currently logged-in user) ===
# Note: this registers the overlay for the user running this script.
# To deploy for every user account, repeat this for HKEY_USERS\<sid> on each kid's account.
$overlayKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$overlayValue = "`"$exe`" --overlay"
New-Item -Path $overlayKey -Force | Out-Null
Set-ItemProperty -Path $overlayKey -Name "ChoreBuddyOverlay" -Value $overlayValue

Write-Host ""
Write-Host "Installed:" -ForegroundColor Green
Write-Host "  Service:  $serviceName (running as LocalSystem, auto-start)"
Write-Host "  Watchdog: $taskName (runs every 60s as SYSTEM)"
Write-Host "  Overlay:  HKCU\Run\ChoreBuddyOverlay (auto-starts on user login)"
Write-Host "  Log file: $env:ProgramData\ChoreBuddy\service.log"
Write-Host ""
Write-Host "To start the overlay NOW without logging out, run (as the user, not admin):"
Write-Host "  Start-Process '$exe' '--overlay'"
Write-Host ""
Write-Host "Service status:" -ForegroundColor Cyan
Get-Service -Name $serviceName | Format-Table Name, Status, StartType -AutoSize
