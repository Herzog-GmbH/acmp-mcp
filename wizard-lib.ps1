# Gemeinsame Hilfsfunktionen fuer install.ps1 und add-user.ps1 (wird per Dot-Sourcing geladen).
# Enthaelt: Ein-/Ausgabe im Terminal, Config lesen/schreiben, Bediener anlegen, Dienst neu starten,
# Log auswerten, MCP-Test und die Copy-Paste-Konfigurationen fuer die MCP-Clients.

$script:TaskName = "ACMP-MCP-Server"

# ---------------------------------------------------------------- Ein-/Ausgabe
function Write-Title($text) {
  Write-Host ""
  Write-Host ("=" * 72) -ForegroundColor DarkCyan
  Write-Host "  $text" -ForegroundColor Cyan
  Write-Host ("=" * 72) -ForegroundColor DarkCyan
}
function Write-Step($nr, $total, $text) { Write-Host ""; Write-Host "[$nr/$total] $text" -ForegroundColor Cyan }
function Write-Info($m) { Write-Host "      $m" -ForegroundColor Gray }
function Write-Ok($m)   { Write-Host "  [OK]      $m" -ForegroundColor Green }
function Write-Bad($m)  { Write-Host "  [FEHLT]   $m" -ForegroundColor Red }
function Write-Hint($m) { Write-Host "  [HINWEIS] $m" -ForegroundColor Yellow }

function Read-Value([string]$prompt, [string]$default = "", [string]$pattern = $null, [string]$patternHint = $null) {
  while ($true) {
    $p = if ($default) { "  $prompt [$default]" } else { "  $prompt" }
    $v = Read-Host $p
    if ([string]::IsNullOrWhiteSpace($v)) { $v = $default }
    $v = "$v".Trim()
    if (-not $pattern -or $v -match $pattern) { return $v }
    Write-Hint $(if ($patternHint) { $patternHint } else { "Ungueltige Eingabe." })
  }
}

function Read-YesNo([string]$prompt, [bool]$default = $true) {
  $suffix = if ($default) { "[J/n]" } else { "[j/N]" }
  while ($true) {
    $v = Read-Host "  $prompt $suffix"
    if ([string]::IsNullOrWhiteSpace($v)) { return $default }
    if ($v -match '^(j|ja|y|yes)$') { return $true }
    if ($v -match '^(n|nein|no)$') { return $false }
  }
}

function Read-Secret([string]$prompt) {
  $sec = Read-Host "  $prompt" -AsSecureString
  $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
  try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

function Read-Choice([string]$prompt, [string[]]$options, [int]$default = 1) {
  for ($i = 0; $i -lt $options.Count; $i++) { Write-Host ("      [{0}] {1}" -f ($i + 1), $options[$i]) }
  while ($true) {
    $v = Read-Host "  $prompt [$default]"
    if ([string]::IsNullOrWhiteSpace($v)) { return $default }
    $n = 0
    if ([int]::TryParse($v, [ref]$n) -and $n -ge 1 -and $n -le $options.Count) { return $n }
  }
}

function Test-IsAdmin {
  ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ---------------------------------------------------------------- Config
function Read-AcmpConfig([string]$path) { Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json }

function Save-AcmpConfig($cfg, [string]$path) {
  $json = $cfg | ConvertTo-Json -Depth 30
  [IO.File]::WriteAllText($path, $json, (New-Object Text.UTF8Encoding($false)))
}

function Set-CfgProp($obj, [string]$name, $value) {
  if ($obj.PSObject.Properties[$name]) { $obj.$name = $value } else { $obj | Add-Member -NotePropertyName $name -NotePropertyValue $value }
}

function New-RandomToken([int]$bytes) {
  $b = New-Object byte[] $bytes
  $rng = [Security.Cryptography.RNGCryptoServiceProvider]::new()
  try { $rng.GetBytes($b) } finally { $rng.Dispose() }
  ([Convert]::ToBase64String($b)).Replace('+', '-').Replace('/', '_').TrimEnd('=')
}

function Get-McpUsers($cfg) {
  $list = @()
  if ($cfg.approval -and $cfg.approval.requester) { $list += [pscustomobject]@{ name = $cfg.approval.requester; acmpUser = $cfg.mcp.defaultAcmpUser } }
  if ($cfg.mcp -and $cfg.mcp.users) { $list += @($cfg.mcp.users | Where-Object { $_.name }) }
  $list
}

function Get-Approvers($cfg) {
  if ($cfg.approval -and $cfg.approval.approvers) { @($cfg.approval.approvers | Where-Object { $_.name -and $_.token -and -not "$($_.token)".StartsWith("HIER_") }) } else { @() }
}

# Legt einen Bediener an (wie /setup bzw. /onboarding im Server). Gibt ApiKey und ggf. ApprovalToken zurueck.
function Add-McpUser([string]$cfgPath, [string]$name, [string]$acmpUser, [bool]$isApprover, [string]$email) {
  $cfg = Read-AcmpConfig $cfgPath
  foreach ($u in (Get-McpUsers $cfg)) { if ($u.name -ieq $name) { throw "Ein Bediener '$name' existiert bereits." } }
  $apiKey = New-RandomToken 32
  $approvalToken = if ($isApprover) { New-RandomToken 24 } else { $null }

  $nu = [pscustomobject]@{ name = $name; apiKey = $apiKey; acmpUser = $(if ($acmpUser) { $acmpUser } else { $null }) }
  Set-CfgProp $cfg.mcp "users" (@($cfg.mcp.users | Where-Object { $_ }) + $nu)
  if ($isApprover) {
    $na = [pscustomobject]@{ name = $name; token = $approvalToken; email = $email }
    # Nur anhaengen - vorhandene Eintraege bleiben unveraendert (auch unvollstaendige/Platzhalter)
    Set-CfgProp $cfg.approval "approvers" (@($cfg.approval.approvers | Where-Object { $_ }) + $na)
  }
  Save-AcmpConfig $cfg $cfgPath
  [pscustomobject]@{ Name = $name; ApiKey = $apiKey; ApprovalToken = $approvalToken; AcmpUser = $acmpUser }
}

function Get-PublicUrl($cfg) {
  $u = if ($cfg.approval -and $cfg.approval.publicUrl) { "$($cfg.approval.publicUrl)" } else { "http://127.0.0.1:$($cfg.mcp.httpPort)" }
  $u.TrimEnd('/')
}

# ---------------------------------------------------------------- Dienst + Log
function Get-LogPath([string]$installDir, $cfg) {
  $lf = if ($cfg.mcp.logFile) { "$($cfg.mcp.logFile)" } else { "server.log" }
  if ([IO.Path]::IsPathRooted($lf)) { $lf } else { Join-Path $installDir $lf }
}

function Get-LogMark([string]$logPath) { if (Test-Path $logPath) { (Get-Item $logPath).Length } else { 0 } }

function Get-LogSince([string]$logPath, [long]$mark) {
  if (-not (Test-Path $logPath)) { return @() }
  $fs = [IO.File]::Open($logPath, 'Open', 'Read', 'ReadWrite')
  try {
    if ($mark -gt $fs.Length) { $mark = 0 }
    $fs.Seek($mark, 'Begin') | Out-Null
    $sr = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)
    ($sr.ReadToEnd() -split "`r?`n") | Where-Object { $_ }
  } finally { $fs.Dispose() }
}

# Startet den Dienst neu und wartet, bis er laeuft oder klar scheitert.
# Ergebnis: "ok" | "sics-fehler" | "abbruch" | "timeout" (+ Meldung)
function Restart-AcmpMcp([string]$installDir, [int]$timeoutSec = 90) {
  $cfg = Read-AcmpConfig (Join-Path $installDir "config.local.json")
  $log = Get-LogPath $installDir $cfg
  Stop-ScheduledTask -TaskName $script:TaskName -ErrorAction SilentlyContinue
  for ($i = 0; $i -lt 20 -and (Get-Process -Name AcmpMcpServer -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
  Get-Process -Name AcmpMcpServer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 1
  $mark = Get-LogMark $log
  Start-ScheduledTask -TaskName $script:TaskName
  Wait-AcmpMcp $log $mark $timeoutSec
}

function Wait-AcmpMcp([string]$log, [long]$mark, [int]$timeoutSec = 90) {
  $deadline = (Get-Date).AddSeconds($timeoutSec)
  Write-Host "      warte auf den Dienst " -NoNewline -ForegroundColor Gray
  while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    Write-Host "." -NoNewline -ForegroundColor Gray
    $lines = Get-LogSince $log $mark
    if ($lines | Where-Object { $_ -match "MCP-Server laeuft" }) { Write-Host ""; return [pscustomobject]@{ State = "ok"; Message = "" } }
    $abort = $lines | Where-Object { $_ -match "ABBRUCH" } | Select-Object -Last 1
    if ($abort) { Write-Host ""; return [pscustomobject]@{ State = "abbruch"; Message = $abort } }
    $fails = @($lines | Where-Object { $_ -match "Verbindung fehlgeschlagen \(Versuch" })
    if ($fails.Count -ge 2) { Write-Host ""; return [pscustomobject]@{ State = "sics-fehler"; Message = $fails[-1] } }
  }
  Write-Host ""
  [pscustomobject]@{ State = "timeout"; Message = "Kein Start innerhalb von $timeoutSec s - siehe $log" }
}

# ---------------------------------------------------------------- MCP-Test
function Invoke-McpCall([string]$url, [string]$apiKey, [string]$method, $params) {
  $body = @{ jsonrpc = "2.0"; id = 1; method = $method; params = $params } | ConvertTo-Json -Depth 10 -Compress
  $hdr = @{}; if ($apiKey) { $hdr["Authorization"] = "Bearer $apiKey" }
  Invoke-RestMethod -Uri $url -Method Post -Headers $hdr -ContentType "application/json" -Body $body -TimeoutSec 60
}

function Get-LocalMcpUrl($cfg) {
  $bind = "$($cfg.mcp.bindAddress)"
  $h = if (-not $bind -or $bind -in @("0.0.0.0", "*", "+")) { "127.0.0.1" } else { $bind }
  "http://${h}:$($cfg.mcp.httpPort)/"
}

# ---------------------------------------------------------------- Netzwerk
function Get-LanSuggestion {
  $cfgIp = Get-NetIPConfiguration -ErrorAction SilentlyContinue | Where-Object { $_.IPv4DefaultGateway -and $_.IPv4Address } | Select-Object -First 1
  if (-not $cfgIp) { return $null }
  $ip = $cfgIp.IPv4Address | Select-Object -First 1
  $prefix = [int]$ip.PrefixLength
  $bytes = ([Net.IPAddress]::Parse($ip.IPAddress)).GetAddressBytes()
  $mask = [uint32]0; if ($prefix -gt 0) { $mask = [uint32]([math]::Pow(2, 32) - [math]::Pow(2, 32 - $prefix)) }
  $maskBytes = [BitConverter]::GetBytes($mask); [Array]::Reverse($maskBytes)
  $net = for ($i = 0; $i -lt 4; $i++) { $bytes[$i] -band $maskBytes[$i] }
  [pscustomobject]@{ Ip = $ip.IPAddress; Subnet = ("{0}/{1}" -f ($net -join "."), $prefix) }
}

# ---------------------------------------------------------------- Client-Konfiguration
$script:ClientNames = @("Claude Code", "Claude Desktop", "VS Code / GitHub Copilot", "OpenAI Codex CLI", "Cursor", "Gemini CLI", "Anderer MCP-Client")

function Show-ClientConfig([int]$choice, [string]$url, [string]$key) {
  $u = $url.TrimEnd('/') + "/"
  Write-Host ""
  switch ($choice) {
    1 { Write-Info "Auf DEINEM PC in einem Terminal ausfuehren:"
        Write-Host "  claude mcp add --transport http acmp $u --header `"Authorization: Bearer $key`" --scope user" -ForegroundColor White }
    2 { Write-Info "In claude_desktop_config.json (Einstellungen > Entwickler > Konfiguration bearbeiten), braucht Node.js:"
        Write-Host @"
  {
    "mcpServers": {
      "acmp": {
        "command": "npx",
        "args": ["-y", "mcp-remote", "$u", "--header", "Authorization: Bearer $key", "--allow-http"]
      }
    }
  }
"@ -ForegroundColor White }
    3 { Write-Info "In .vscode/mcp.json bzw. der Benutzer-MCP-Konfiguration (VS Code fragt den Key beim Start ab):"
        Write-Host @"
  {
    "inputs": [ { "type": "promptString", "id": "acmp-key", "description": "ACMP-MCP API-Key", "password": true } ],
    "servers": {
      "acmp": { "type": "http", "url": "$u", "headers": { "Authorization": "Bearer `${input:acmp-key}" } }
    }
  }
"@ -ForegroundColor White
        Write-Info "Key beim Abfragen einfuegen: $key" }
    4 { Write-Info "In ~/.codex/config.toml:"
        Write-Host @"
  [mcp_servers.acmp]
  url = "$u"
  bearer_token_env_var = "ACMP_MCP_KEY"
"@ -ForegroundColor White
        Write-Info "Und als Benutzer-Umgebungsvariable (PowerShell auf deinem PC):"
        Write-Host "  [Environment]::SetEnvironmentVariable('ACMP_MCP_KEY', '$key', 'User')" -ForegroundColor White }
    5 { Write-Info "In ~/.cursor/mcp.json:"
        Write-Host @"
  { "mcpServers": { "acmp": { "url": "$u", "headers": { "Authorization": "Bearer $key" } } } }
"@ -ForegroundColor White }
    6 { Write-Info "In ~/.gemini/settings.json:"
        Write-Host @"
  { "mcpServers": { "acmp": { "httpUrl": "$u", "headers": { "Authorization": "Bearer $key" } } } }
"@ -ForegroundColor White }
    default {
        Write-Info "Jeder Client mit Streamable HTTP:"
        Write-Host "  URL:    $u" -ForegroundColor White
        Write-Host "  Header: Authorization: Bearer $key" -ForegroundColor White
        Write-Info "Nur-stdio-Clients ueber die Bruecke: npx -y mcp-remote $u --header `"Authorization: Bearer $key`" --allow-http"
        Write-Info "Weitere Vorlagen: docs/CLIENTS.md im Repo" }
  }
}

function Show-AccessSummary($user, [string]$publicUrl, [bool]$localOnly) {
  Write-Host ""
  Write-Host "  Zugang fuer '$($user.Name)' - diese Werte werden NUR JETZT angezeigt:" -ForegroundColor Yellow
  Write-Host "    MCP-URL:        $publicUrl/" -ForegroundColor White
  Write-Host "    MCP-API-Key:    $($user.ApiKey)" -ForegroundColor White
  if ($user.ApprovalToken) {
    $q = "approver=" + [Uri]::EscapeDataString($user.Name) + "&token=" + [Uri]::EscapeDataString($user.ApprovalToken)
    Write-Host "    Freigaben:      $publicUrl/approvals?$q" -ForegroundColor White
    Write-Host "    Dashboard:      $publicUrl/dashboard?$q" -ForegroundColor White
    Write-Host "    Onboarding:     $publicUrl/onboarding?$q" -ForegroundColor White
    Write-Info "Die Links enthalten deinen persoenlichen Freigabe-Login - als Lesezeichen speichern, nicht weitergeben."
  }
  if ($localOnly) { Write-Hint "Der Server ist noch NUR auf diesem Rechner erreichbar (127.0.0.1). Fuer andere PCs: .\set-network-admin.ps1" }
  if (Get-Command Set-Clipboard -ErrorAction SilentlyContinue) {
    if (Read-YesNo "API-Key in die Zwischenablage kopieren?" $true) { Set-Clipboard -Value $user.ApiKey; Write-Ok "API-Key kopiert" }
  }
}
