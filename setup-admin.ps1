# ACMP-MCP-Server: einmaliges Setup (ALS ADMINISTRATOR ausfuehren).
# Richtet Firewall-Freigabe und Autostart als geplante Aufgabe (laeuft als SYSTEM bei
# jedem Systemstart) ein. Optional: -AllowedSubnets fuer die erlaubten Quell-Subnetze
# (Default: nur das lokale Subnetz), z.B. -AllowedSubnets "10.0.0.0/24","10.0.1.0/24".
param(
  [string[]]$AllowedSubnets = @("LocalSubnet"),
  [switch]$NoStart   # nur registrieren, nicht starten/testen (nutzt install.ps1)
)
#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$base = $PSScriptRoot   # laeuft aus dem Ordner, in dem dieses Skript liegt (portabel)
$exe  = Join-Path $base "AcmpMcpServer.exe"
$cfg  = Join-Path $base "config.local.json"
if (-not (Test-Path $exe)) { Write-Host "AcmpMcpServer.exe fehlt - bitte zuerst src\build-server.ps1 ausfuehren." -ForegroundColor Red; exit 1 }

$cfgJson = Get-Content $cfg -Raw | ConvertFrom-Json
$port    = if ($cfgJson.mcp.httpPort) { $cfgJson.mcp.httpPort } else { 8765 }

Write-Host ("1) Firewall-Regel (TCP {0} inbound, Quelle {1}) ..." -f $port, ($AllowedSubnets -join ", "))
Get-NetFirewallRule -DisplayName "ACMP MCP Server ($port)" -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
New-NetFirewallRule -DisplayName "ACMP MCP Server ($port)" -Direction Inbound -Protocol TCP -LocalPort $port `
  -Action Allow -Profile Domain,Private -RemoteAddress $AllowedSubnets | Out-Null
Write-Host "   OK" -ForegroundColor Green

Write-Host "2) Autostart-Aufgabe 'ACMP-MCP-Server' (SYSTEM, bei Systemstart, Neustart bei Absturz) ..."
$action    = New-ScheduledTaskAction -Execute $exe -Argument ('"' + $cfg + '"') -WorkingDirectory $base
$trigger   = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
              -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 9999 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName "ACMP-MCP-Server" -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
if ($NoStart) { Write-Host "   OK - Aufgabe registriert (noch nicht gestartet)." -ForegroundColor Green; return }
Start-ScheduledTask -TaskName "ACMP-MCP-Server"
Start-Sleep -Seconds 6
Write-Host "   OK - Aufgabe registriert und gestartet." -ForegroundColor Green

Write-Host "3) Funktionstest ..."
try {
  $key      = $cfgJson.mcp.apiKey
  if (-not $key -and $cfgJson.mcp.users) { $key = @($cfgJson.mcp.users)[0].apiKey }
  $hdr      = if ($key) { @{ Authorization = "Bearer $key" } } else { @{} }
  $bindAddr = if ($cfgJson.mcp.bindAddress) { $cfgJson.mcp.bindAddress } else { "127.0.0.1" }
  $testHost = if ($bindAddr -in @("0.0.0.0", "*", "+")) { "127.0.0.1" } else { $bindAddr }
  $r = Invoke-RestMethod -Uri "http://${testHost}:${port}/" -Method Post -Headers $hdr `
        -ContentType "application/json" -Body '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}' -TimeoutSec 20
  Write-Host ("   OK - Server antwortet, {0} Tools verfuegbar." -f $r.result.tools.Count) -ForegroundColor Green
} catch {
  Write-Host ("   Test fehlgeschlagen: " + $_.Exception.Message) -ForegroundColor Yellow
  Write-Host "   Log pruefen: $base\server.log"
}
Write-Host "`nFertig. Verwaltung: Get-ScheduledTask ACMP-MCP-Server | Start-/Stop-ScheduledTask"
