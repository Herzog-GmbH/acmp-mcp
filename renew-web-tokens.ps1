#Requires -RunAsAdministrator
# renew-web-tokens.ps1 - Web-Tokens der Weboberflaeche neu erzeugen; die alten gelten danach nicht mehr.
#  Anlass: Bis zum 22.10.2026 standen die Web-Tokens (frueher "Freigabe-Tokens") in den Links der Freigabe-Mails. Wer solche Mails
#  verschickt hat, sollte die Tokens einmal erneuern.
#  - erzeugt je Web-Zugang (web.users; mit -Name nur diese) einen neuen Zufallswert (24 Byte, wie der Server selbst) und tauscht in
#    config.local.json nur diesen Wert (Sicherung config.local.json.bak-<Zeit>, sonst bytegleich)
#  - startet die Aufgabe "ACMP-MCP-Server" neu; alle Web-Sitzungen enden damit
#  - prueft: neue Tokens melden an, alte nicht. Geht etwas schief, kommt die alte Config zurueck (und der Server startet neu).
#  - zeigt die neuen Tokens EINMAL hier im Fenster: kein Protokoll, keine Datei. In den Passwort-Manager legen und den Kollegen
#    persoenlich geben, nicht per Mail oder Chat. API-Keys (mcp.users) bleiben, wie sie sind.
# Aufruf als Administrator im Installationsordner:  .\renew-web-tokens.ps1            (alle Web-Zugaenge)
#                                                    .\renew-web-tokens.ps1 -Name <Kurzname>   (nur diese)
param([string[]]$Name, [string]$InstallDir)
$ErrorActionPreference = 'Stop'
if (-not $InstallDir) { $InstallDir = if (Test-Path (Join-Path $PSScriptRoot 'config.local.json')) { $PSScriptRoot } else { Split-Path $PSScriptRoot -Parent } }
$cfgPath = Join-Path $InstallDir 'config.local.json'
$task    = 'ACMP-MCP-Server'
if (-not (Test-Path $cfgPath)) { throw "config.local.json nicht gefunden in $InstallDir (-InstallDir angeben)." }

function New-WebToken {
    $b = New-Object byte[] 24
    $rng = New-Object Security.Cryptography.RNGCryptoServiceProvider
    try { $rng.GetBytes($b) } finally { $rng.Dispose() }
    [Convert]::ToBase64String($b).Replace('+', '-').Replace('/', '_').TrimEnd('=')
}
function Restart-Mcp {
    Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
    for ($i = 0; $i -lt 20 -and (Get-Process -Name AcmpMcpServer -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
    Get-Process -Name AcmpMcpServer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    Start-ScheduledTask -TaskName $task
    Start-Sleep -Seconds 6
}
# POST /login ohne Weiterleitung folgen: Weiterleitung (der Server antwortet mit 303) mit Sitzungs-Cookie = angemeldet
function Test-WebLogin([string]$root, [string]$user, [string]$token) {
    $req = [Net.HttpWebRequest]::Create("$root/login")
    $req.Method = 'POST'; $req.AllowAutoRedirect = $false; $req.Timeout = 20000
    $req.ContentType = 'application/x-www-form-urlencoded'
    $body = [Text.Encoding]::UTF8.GetBytes('name=' + [Uri]::EscapeDataString($user) + '&token=' + [Uri]::EscapeDataString($token) + '&next=%2Fdashboard')
    $req.ContentLength = $body.Length
    $s = $req.GetRequestStream(); $s.Write($body, 0, $body.Length); $s.Close()
    try { $resp = $req.GetResponse() } catch [Net.WebException] { $resp = $_.Exception.Response; if (-not $resp) { throw } }
    try { return (@(301, 302, 303, 307, 308) -contains [int]$resp.StatusCode) -and ("" + $resp.Headers['Set-Cookie']) -match 'acmpmcp_sid=[^;]+' } finally { $resp.Close() }
}

# 1) Config lesen, neue Werte nur fuer die Tokens einsetzen
$raw  = [IO.File]::ReadAllBytes($cfgPath)
$bom  = $raw.Length -ge 3 -and $raw[0] -eq 0xEF -and $raw[1] -eq 0xBB -and $raw[2] -eq 0xBF
$text = [Text.Encoding]::UTF8.GetString($raw, $(if ($bom) { 3 } else { 0 }), $raw.Length - $(if ($bom) { 3 } else { 0 }))
$cfg  = $text | ConvertFrom-Json
$users = @($cfg.web.users | Where-Object { $_.name -and $_.token -and -not ("" + $_.token).StartsWith('HIER_') })
if ($users.Count -eq 0) {
    if ($cfg.approval) { throw 'config.local.json hat noch die alte approval-Section: erst install.ps1 laufen lassen (stellt auf web um), dann dieses Skript.' }
    throw 'Kein Web-Zugang in web.users - nichts zu erneuern.'
}
if ($Name) {
    $want = @($Name | ForEach-Object { $_.Trim().ToLowerInvariant() })
    $have = @($users | ForEach-Object { ("" + $_.name).ToLowerInvariant() })
    $miss = @($want | Where-Object { $have -notcontains $_ })
    if ($miss.Count) { throw ('Kein Web-Zugang fuer: ' + ($miss -join ', ') + ' - vorhanden: ' + (@($users | ForEach-Object { $_.name }) -join ', ') + '. Nichts geaendert.') }
    $users = @($users | Where-Object { $want -contains $_.name.ToLowerInvariant() })
}
$swap = @()
$new  = $text
foreach ($u in $users) {
    $old = '"' + $u.token + '"'
    $n = ([regex]::Matches($new, [regex]::Escape($old))).Count
    if ($n -ne 1) { throw "Token von $($u.name) steht nicht genau einmal in config.local.json ($n Treffer) - nichts geaendert." }
    $t = New-WebToken
    $new = $new.Replace($old, '"' + $t + '"')
    $swap += [pscustomobject]@{ Name = $u.name; Old = $u.token; New = $t }
}
$check = $new
foreach ($w in $swap) { $check = $check.Replace('"' + $w.New + '"', '"' + $w.Old + '"') }
if ($check -ne $text) { throw 'Interner Fehler: ausser den Tokens wuerde sich etwas aendern - nichts geaendert.' }
$parsed = $new | ConvertFrom-Json
foreach ($w in $swap) { if ((@($parsed.web.users | Where-Object { $_.name -eq $w.Name })[0]).token -ne $w.New) { throw "Interner Fehler bei $($w.Name) - nichts geaendert." } }

# 2) sichern, schreiben, neu starten
$ts  = Get-Date -Format 'yyyyMMdd_HHmmss'
$bak = "$cfgPath.bak-$ts"
Copy-Item $cfgPath $bak
$out = [Text.Encoding]::UTF8.GetBytes($new)
if ($bom) { $out = [byte[]](@(0xEF, 0xBB, 0xBF) + $out) }
[IO.File]::WriteAllBytes($cfgPath, $out)
Write-Host "Config geschrieben (Sicherung $(Split-Path $bak -Leaf)), MCP-Server startet neu ..." -ForegroundColor Cyan
Restart-Mcp

# 3) pruefen: neue Tokens melden an, alte nicht
$bind = if ($cfg.mcp.bindAddress) { $cfg.mcp.bindAddress } else { '127.0.0.1' }
$testHost = if ($bind -in @('0.0.0.0', '*', '+')) { '127.0.0.1' } else { $bind }
$port = if ($cfg.mcp.httpPort) { $cfg.mcp.httpPort } else { 8765 }
$root = "http://${testHost}:${port}"
$fail = @()
foreach ($w in $swap) {
    try {
        if (-not (Test-WebLogin $root $w.Name $w.New)) { $fail += "$($w.Name): neuer Token meldet nicht an" }
        if (Test-WebLogin $root $w.Name $w.Old) { $fail += "$($w.Name): alter Token meldet noch an" }
    } catch { $fail += "$($w.Name): Anmeldung nicht pruefbar ($($_.Exception.Message))" }
}
if ($fail.Count) {
    Write-Host ('FEHLER: ' + ($fail -join '; ')) -ForegroundColor Red
    Copy-Item $bak $cfgPath -Force
    Restart-Mcp
    Write-Host 'Alte Config zurueck, Server neu gestartet - die alten Tokens gelten weiter. Log: server.log' -ForegroundColor Yellow
    exit 1
}

Write-Host ''
Write-Host 'FERTIG: alte Web-Tokens gelten nicht mehr, alle Web-Sitzungen sind beendet. Neue Tokens (nur jetzt sichtbar):' -ForegroundColor Green
foreach ($w in $swap) { Write-Host ('   {0,-16} {1}' -f $w.Name, $w.New) }
Write-Host ''
Write-Host 'In den Passwort-Manager legen und persoenlich weitergeben, nicht per Mail oder Chat. Anmeldung: Kurzname + Web-Token auf /login.'
Write-Host "Zurueck (falls noetig): $(Split-Path $bak -Leaf) als config.local.json kopieren und die Aufgabe $task neu starten."
