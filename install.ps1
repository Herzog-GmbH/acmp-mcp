# ACMP-MCP-Server: Installation in einem Schritt (auf dem ACMP-Server, ALS ADMINISTRATOR).
#
#   .\install.ps1 -CheckOnly        Nur Voraussetzungen pruefen, nichts veraendern (kein Admin noetig)
#   .\install.ps1                   Installieren nach C:\ProgramData\AcmpMcp
#   .\install.ps1 -InstallDir D:\AcmpMcp
#
# Ablauf: Voraussetzungen pruefen -> Dateien kopieren -> bauen -> config.local.json anlegen
# (fragt den SICS-Benutzer ab) -> Autostart-Aufgabe + Firewall-Regel -> Setup-Link anzeigen.
# Erneut ausfuehren ist gefahrlos: eine vorhandene config.local.json bleibt unveraendert,
# eine laufende Installation wird aktualisiert.
param(
  [string]$InstallDir = "C:\ProgramData\AcmpMcp",
  [string]$SicsDir    = "C:\Program Files (x86)\Aagon\AESB SICS\SICS",
  [string]$SicsUser,
  [int]$HttpPort      = 8765,
  [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot

function Ok($m)   { Write-Host "  [OK]   $m" -ForegroundColor Green }
function Bad($m)  { Write-Host "  [FEHLT] $m" -ForegroundColor Red; $script:failed = $true }
function Warn($m) { Write-Host "  [HINWEIS] $m" -ForegroundColor Yellow }

# ---------------------------------------------------------------- 1) Voraussetzungen
Write-Host "1) Voraussetzungen" -ForegroundColor Cyan
$failed = $false
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isAdmin) { Ok "PowerShell laeuft als Administrator" } elseif ($CheckOnly) { Warn "Nicht als Administrator - fuer die Installation noetig" } else { Bad "Bitte PowerShell 'Als Administrator ausfuehren'" }

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (Test-Path $csc) { Ok ".NET Framework 4.x Compiler ($csc)" } else { Bad ".NET Framework 4.8 (csc.exe) nicht gefunden" }

$dlls = "Newtonsoft.Json.dll","Aagon.Sis.Sics.Clients.dll","Aagon.Sis.Sics.Icql.dll","Aagon.Sis.Sics.Interfaces.dll","Aagon.Logging.dll"
$missing = $dlls | Where-Object { -not (Test-Path (Join-Path $SicsDir $_)) }
if (-not $missing) { Ok "AESB-SICS-Bibliotheken ($SicsDir)" } else { Bad ("AESB SICS nicht gefunden in $SicsDir (fehlt: " + ($missing -join ", ") + "). Laeuft das Skript auf dem ACMP-Server?") }

$wss = $false
try { $c = New-Object Net.Sockets.TcpClient; $iar = $c.BeginConnect("127.0.0.1", 3950, $null, $null); $wss = $iar.AsyncWaitHandle.WaitOne(2000) -and $c.Connected; $c.Close() } catch {}
if ($wss) { Ok "SICS lauscht auf 127.0.0.1:3950" } else { Bad "Port 3950 (SICS/WSS) lokal nicht erreichbar - AESB-Dienste laufen?" }

$portBusy = Get-NetTCPConnection -LocalPort $HttpPort -State Listen -ErrorAction SilentlyContinue
$ownProc  = Get-Process -Name AcmpMcpServer -ErrorAction SilentlyContinue
if (-not $portBusy) { Ok "Port $HttpPort frei" } elseif ($ownProc) { Ok "Port $HttpPort belegt durch vorhandenen ACMP-MCP-Server (wird aktualisiert)" } else { Bad "Port $HttpPort ist von einem anderen Programm belegt (-HttpPort waehlen)" }

Write-Host ""
Write-Host "  Manuell in der ACMP-Konsole pruefen (siehe docs\ACMP-VORBEREITUNG.md):" -ForegroundColor Gray
Write-Host "   - System > Einstellungen > ACMP Server > SICS-Verbindung: 'Public API Zugriff gewaehren' aktiv" -ForegroundColor Gray
Write-Host "   - Ein SICS-/AESB-Benutzer mit Passwort ist vorhanden" -ForegroundColor Gray

if ($failed) { Write-Host "`nVoraussetzungen nicht erfuellt - Abbruch, nichts veraendert." -ForegroundColor Red; exit 1 }
if ($CheckOnly) { Write-Host "`nAlles bereit. Installation: .\install.ps1 (als Administrator)" -ForegroundColor Green; exit 0 }

# ---------------------------------------------------------------- 2) Dateien
Write-Host "`n2) Dateien nach $InstallDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Force $InstallDir | Out-Null
# Ordner enthaelt spaeter Zugangsdaten -> nur Administratoren und SYSTEM
icacls $InstallDir /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" | Out-Null
Ok "Rechte auf Administratoren + SYSTEM beschraenkt"
if ((Resolve-Path $repo).Path.TrimEnd('\') -ne (Resolve-Path $InstallDir).Path.TrimEnd('\')) {
  foreach ($f in "api-tools.json","db-tools.json","config.example.json","grant-db-readonly.sql","update-admin.ps1","uninstall-admin.ps1","setup-admin.ps1","set-network-admin.ps1") {
    Copy-Item (Join-Path $repo $f) $InstallDir -Force
  }
  Ok "Konfigurationsvorlagen und Verwaltungsskripte kopiert"
}

# ---------------------------------------------------------------- 3) Bauen
Write-Host "`n3) Bauen" -ForegroundColor Cyan
$exe = Join-Path $InstallDir "AcmpMcpServer.exe"
$firstInstall = -not (Test-Path $exe)
$serverName = if ($firstInstall) { "AcmpMcpServer.exe" } else { "AcmpMcpServer.new.exe" }
& (Join-Path $repo "build.ps1") -OutDir $InstallDir -SicsDir $SicsDir -ServerExeName $serverName

# ---------------------------------------------------------------- 4) Konfiguration
Write-Host "`n4) Konfiguration" -ForegroundColor Cyan
$cfgPath = Join-Path $InstallDir "config.local.json"
if (Test-Path $cfgPath) {
  Ok "config.local.json vorhanden - bleibt unveraendert"
} else {
  $cfg = Get-Content (Join-Path $repo "config.example.json") -Raw | ConvertFrom-Json
  if (-not $SicsUser) { $SicsUser = Read-Host "  SICS-/AESB-Benutzername" }
  $sec = Read-Host "  Passwort fuer '$SicsUser' (wird nur in config.local.json gespeichert)" -AsSecureString
  $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
  $cfg.sics.username = $SicsUser
  $cfg.sics.password = $plain
  $cfg.mcp.httpPort = $HttpPort
  $cfg.mcp.bindAddress = "127.0.0.1"
  $cfg.approval.publicUrl = "http://127.0.0.1:$HttpPort"
  # db / runPowershell / notify behalten ihre HIER_-Platzhalter -> der Server laesst diese Tools aus,
  # bis jemand echte Werte eintraegt (docs\ACMP-VORBEREITUNG.md).
  $cfg | ConvertTo-Json -Depth 20 | Set-Content -Path $cfgPath -Encoding UTF8
  $plain = $null
  Ok "config.local.json angelegt (nur lokal erreichbar: 127.0.0.1:$HttpPort)"
}

# ---------------------------------------------------------------- 5) Dienst
Write-Host "`n5) Autostart und Start" -ForegroundColor Cyan
if ($firstInstall) {
  & (Join-Path $InstallDir "setup-admin.ps1")
} else {
  & (Join-Path $InstallDir "update-admin.ps1")
}

# ---------------------------------------------------------------- 6) Ersteinrichtung
$log = Join-Path $InstallDir "server.log"
$line = $null
for ($i = 0; $i -lt 15 -and -not $line; $i++) {
  if (Test-Path $log) { $line = Get-Content $log -Tail 200 | Where-Object { $_ -match "ERSTEINRICHTUNG NOETIG" } | Select-Object -Last 1 }
  if (-not $line) { Start-Sleep -Seconds 1 }
}
Write-Host ""
if ($line -and $line -match "(http\S+/setup\?token=\S+)") {
  Write-Host "Fertig. Jetzt den ersten Administrator anlegen - diesen Link HIER AUF DEM SERVER im Browser oeffnen:" -ForegroundColor Green
  Write-Host "  $($Matches[1])" -ForegroundColor White
  Write-Host "Danach: .\set-network-admin.ps1 -BindAddress <LAN-IP dieses Servers>   (damit Kolleg:innen von ihren PCs zugreifen koennen)" -ForegroundColor Gray
} else {
  Write-Host "Fertig. Server laeuft. Weboberflaeche: http://127.0.0.1:$HttpPort/dashboard  (Log: $log)" -ForegroundColor Green
}
