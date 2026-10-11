# Macht den ACMP-MCP-Server im LAN erreichbar (ALS ADMINISTRATOR, nach der Ersteinrichtung).
#   .\set-network-admin.ps1 -BindAddress 10.0.0.5
#   .\set-network-admin.ps1 -BindAddress 10.0.0.5 -AllowedSubnets "10.0.0.0/24"
#   .\set-network-admin.ps1 -BindAddress 127.0.0.1      (zurueck auf nur-lokal)
# Setzt mcp.bindAddress + approval.publicUrl in config.local.json, passt die Firewall-Regel an
# und startet den Dienst neu. Verweigert LAN-Betrieb, solange kein Bediener (API-Key) existiert.
param(
  [Parameter(Mandatory = $true)][string]$BindAddress,
  [string[]]$AllowedSubnets = @("LocalSubnet")
)
#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$cfgPath = Join-Path $PSScriptRoot "config.local.json"
$cfg  = Get-Content $cfgPath -Raw | ConvertFrom-Json
$port = if ($cfg.mcp.httpPort) { $cfg.mcp.httpPort } else { 8765 }
$local = $BindAddress -in @("127.0.0.1", "localhost")

$hasKey = [bool]$cfg.mcp.apiKey -or (@($cfg.mcp.users | Where-Object { $_.apiKey }).Count -gt 0)
if (-not $local -and -not $hasKey) {
  Write-Host "Noch kein Bediener angelegt. Zuerst die Ersteinrichtung (/setup) abschliessen - ohne API-Key waere der Server offen im Netz." -ForegroundColor Red
  exit 1
}

Copy-Item $cfgPath "$cfgPath.$(Get-Date -Format yyyyMMdd_HHmmss).bak"
$cfg.mcp.bindAddress = $BindAddress
$hostForUrl = if ($BindAddress -in @("0.0.0.0", "*", "+")) { $env:COMPUTERNAME } else { $BindAddress }
$cfg.approval.publicUrl = "http://${hostForUrl}:$port"
$cfg | ConvertTo-Json -Depth 20 | Set-Content -Path $cfgPath -Encoding UTF8
Write-Host "config.local.json: bindAddress=$BindAddress, publicUrl=$($cfg.approval.publicUrl) (Sicherung angelegt)" -ForegroundColor Green

Get-NetFirewallRule -DisplayName "ACMP MCP Server ($port)" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
if (-not $local) {
  New-NetFirewallRule -DisplayName "ACMP MCP Server ($port)" -Direction Inbound -Protocol TCP -LocalPort $port `
    -Action Allow -Profile Domain,Private -RemoteAddress $AllowedSubnets | Out-Null
  Write-Host ("Firewall: TCP {0} eingehend erlaubt von {1}" -f $port, ($AllowedSubnets -join ", ")) -ForegroundColor Green
}

Stop-ScheduledTask -TaskName "ACMP-MCP-Server" -ErrorAction SilentlyContinue
Get-Process -Name AcmpMcpServer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1
Start-ScheduledTask -TaskName "ACMP-MCP-Server"
Write-Host "Dienst neu gestartet. Clients verbinden sich mit: http://${hostForUrl}:$port/" -ForegroundColor Green
Write-Host "Hinweis: Der Server spricht nur HTTP. Fuer Zugriff ueber Netzgrenzen hinweg TLS-Proxy oder VPN davorsetzen." -ForegroundColor Yellow
