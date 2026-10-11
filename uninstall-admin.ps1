# Entfernt Autostart-Aufgabe und Firewall-Regel des ACMP-MCP-Servers (ALS ADMIN ausfuehren).
#Requires -RunAsAdministrator
$ErrorActionPreference = 'SilentlyContinue'
Stop-ScheduledTask -TaskName "ACMP-MCP-Server"
Unregister-ScheduledTask -TaskName "ACMP-MCP-Server" -Confirm:$false
Get-Process -Name AcmpMcpServer -ErrorAction SilentlyContinue | Stop-Process -Force
Get-NetFirewallRule -DisplayName "ACMP MCP Server (*)" | Remove-NetFirewallRule
Write-Host "Autostart-Aufgabe und Firewall-Regel entfernt." -ForegroundColor Green
Write-Host "Der Ordner $PSScriptRoot (inkl. config.local.json / secrets.local.json) bleibt erhalten - bei Bedarf selbst loeschen." -ForegroundColor Gray
