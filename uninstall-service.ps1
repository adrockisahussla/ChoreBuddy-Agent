#Requires -RunAsAdministrator

$serviceName = "ChoreBuddyAgent"
$taskName = "ChoreBuddyAgentWatchdog"

if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Write-Host "Removing watchdog task..."
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Write-Host "Stopping service..."
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Write-Host "Removing service..."
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 1
}

Get-Process ChoreBuddy.TestApp -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Remove-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name ChoreBuddyOverlay -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Uninstalled service, watchdog, and overlay autostart." -ForegroundColor Green
Write-Host "Note: firewall rules and IFEO registry blocks remain in place."
Write-Host "To clear them manually:"
Write-Host "  Get-NetFirewallRule -DisplayName 'ChoreBuddy_*' | Remove-NetFirewallRule"
