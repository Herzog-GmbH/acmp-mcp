# Spielt eine neu gebaute AcmpMcpServer.new.exe in den laufenden Dienst ein (ALS ADMIN ausfuehren).
# Stoppt den Autostart-Task, ersetzt die EXE, startet neu und testet.
#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$base = $PSScriptRoot   # laeuft aus dem Ordner, in dem dieses Skript liegt (portabel)
$exe  = Join-Path $base "AcmpMcpServer.exe"
$new  = Join-Path $base "AcmpMcpServer.new.exe"
if (-not (Test-Path $new)) { Write-Host "AcmpMcpServer.new.exe fehlt - nichts einzuspielen." -ForegroundColor Yellow; exit 0 }

Write-Host "1) Dienst stoppen ..."
Stop-ScheduledTask -TaskName "ACMP-MCP-Server" -ErrorAction SilentlyContinue
for ($i=0; $i -lt 20 -and (Get-Process -Name AcmpMcpServer -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
Get-Process -Name AcmpMcpServer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

Write-Host "2) Neue Version einspielen ..."
Copy-Item $new $exe -Force
Remove-Item $new -Force -ErrorAction SilentlyContinue
Write-Host "   OK" -ForegroundColor Green

Write-Host "3) Dienst starten ..."
Start-ScheduledTask -TaskName "ACMP-MCP-Server"
Start-Sleep -Seconds 6

Write-Host "4) Test ..."
try {
  $cfgJson  = Get-Content (Join-Path $base "config.local.json") -Raw | ConvertFrom-Json
  $key      = $cfgJson.mcp.apiKey
  if (-not $key -and $cfgJson.mcp.users) { $key = @($cfgJson.mcp.users)[0].apiKey }
  $hdr      = if ($key) { @{ Authorization = "Bearer $key" } } else { @{} }
  $bindAddr = if ($cfgJson.mcp.bindAddress) { $cfgJson.mcp.bindAddress } else { "127.0.0.1" }
  $testHost = if ($bindAddr -in @("0.0.0.0", "*", "+")) { "127.0.0.1" } else { $bindAddr }
  $port     = if ($cfgJson.mcp.httpPort) { $cfgJson.mcp.httpPort } else { 8765 }
  $r = Invoke-RestMethod -Uri "http://${testHost}:${port}/" -Method Post -Headers $hdr `
        -ContentType "application/json" -Body '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}' -TimeoutSec 20
  Write-Host ("   OK - neue Version laeuft, {0} Tools aktiv." -f $r.result.tools.Count) -ForegroundColor Green
} catch { Write-Host ("   Test fehlgeschlagen: " + $_.Exception.Message + "  (Log: $base\server.log)") -ForegroundColor Yellow }
Write-Host "`nHinweis: Am Hauptrechner Claude-Client einmal neu verbinden/neu starten, damit die neue Tool-Liste geladen wird."
