# ACMP-MCP-Server: gefuehrte Einrichtung im Terminal (auf dem ACMP-Server; startet sich bei Bedarf selbst mit Admin-Rechten neu).
#
#   .\install.ps1 -CheckOnly        Nur Voraussetzungen pruefen, nichts veraendern (kein Admin noetig)
#   .\install.ps1                   Assistent: installieren, ersten Administrator anlegen, LAN-Freigabe,
#                                   Client-Konfiguration - am Ende stehen alle Links und Befehle bereit
#   .\install.ps1 -NonInteractive -SicsUser op -AdminName admin -AdminEmail it@firma.de
#                                   Ohne Rueckfragen (Passwort aus $env:ACMP_SICS_PASSWORD), z.B. fuer Skripte
#
# Erneut ausfuehren ist gefahrlos: vorhandene config.local.json und Bediener bleiben erhalten,
# eine laufende Installation wird aktualisiert, bereits erledigte Schritte werden uebersprungen.
param(
  [string]$InstallDir = "C:\ProgramData\AcmpMcp",
  [string]$SicsDir    = "C:\Program Files (x86)\Aagon\AESB SICS\SICS",
  [int]$HttpPort      = 8765,
  [string]$SicsUser,
  [string]$AdminName,
  [string]$AdminEmail,
  [string]$AdminAcmpUser,
  [string]$BindAddress,
  [string[]]$AllowedSubnets,
  [switch]$CheckOnly,
  [switch]$NonInteractive
)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
. (Join-Path $repo "wizard-lib.ps1")
if (-not $CheckOnly) { Invoke-SelfElevate $PSCommandPath $PSBoundParameters }
if ($AllowedSubnets) { $AllowedSubnets = Split-ListArg $AllowedSubnets }
$ask = -not $NonInteractive
$T = 9

Write-Title "ACMP-MCP-Server - Einrichtung"
Write-Info "Der Assistent fuehrt dich durch alle Schritte. Eckige Klammern [..] zeigen den Vorschlag - Enter uebernimmt ihn."
Write-Info "Abbrechen jederzeit mit Strg+C; ein erneuter Start setzt dort fort, wo es noch etwas zu tun gibt."

# ================================================================ 1) Voraussetzungen
Write-Step 1 $T "Voraussetzungen pruefen"
$failed = $false
if (Test-IsAdmin) { Write-Ok "PowerShell laeuft als Administrator" }
elseif ($CheckOnly) { Write-Hint "Nicht als Administrator - fuer die Installation noetig" }
else { Write-Bad "Bitte PowerShell mit Rechtsklick > 'Als Administrator ausfuehren' starten"; $failed = $true }

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (Test-Path $csc) { Write-Ok ".NET Framework 4.x (Compiler vorhanden)" } else { Write-Bad ".NET Framework 4.8 fehlt (csc.exe nicht gefunden)"; $failed = $true }

$dlls = "Newtonsoft.Json.dll","Aagon.Sis.Sics.Clients.dll","Aagon.Sis.Sics.Icql.dll","Aagon.Sis.Sics.Interfaces.dll","Aagon.Logging.dll"
$missing = $dlls | Where-Object { -not (Test-Path (Join-Path $SicsDir $_)) }
if (-not $missing) { Write-Ok "AESB SICS gefunden ($SicsDir)" }
else { Write-Bad "AESB SICS nicht gefunden in $SicsDir - laeuft das Skript wirklich auf dem ACMP-Server? (sonst -SicsDir angeben)"; $failed = $true }

$wss = $false
try { $c = New-Object Net.Sockets.TcpClient; $iar = $c.BeginConnect("127.0.0.1", 3950, $null, $null); $wss = $iar.AsyncWaitHandle.WaitOne(2000) -and $c.Connected; $c.Close() } catch {}
if ($wss) { Write-Ok "SICS antwortet auf 127.0.0.1:3950" }
else { Write-Bad "SICS (Port 3950) antwortet nicht. Laufen die AESB-Dienste?  Get-Service | ? Name -match 'SICS|AESB'"; $failed = $true }

$portBusy = Get-NetTCPConnection -LocalPort $HttpPort -State Listen -ErrorAction SilentlyContinue
$ownProc  = Get-Process -Name AcmpMcpServer -ErrorAction SilentlyContinue
if (-not $portBusy) { Write-Ok "Port $HttpPort ist frei" }
elseif ($ownProc) { Write-Ok "Port ${HttpPort}: vorhandener ACMP-MCP-Server (wird aktualisiert)" }
else { Write-Bad "Port $HttpPort ist von einem anderen Programm belegt - mit -HttpPort einen anderen waehlen"; $failed = $true }

if ($failed) { Write-Host "`n  Voraussetzungen nicht erfuellt - nichts veraendert. Punkte oben beheben und erneut starten." -ForegroundColor Red; exit 1 }
if ($CheckOnly) { Write-Host "`n  Alles bereit. Zum Installieren: .\install.ps1 (als Administrator)" -ForegroundColor Green; exit 0 }

# ================================================================ 2) ACMP-Konsole
Write-Step 2 $T "Einstellung in der ACMP-Konsole"
Write-Info "Der Server braucht die freigeschaltete Public API:"
Write-Info "ACMP-Konsole > System > Einstellungen > ACMP Server > SICS-Verbindung > 'Public API - Zugriff gewaehren'"
if ($ask) {
  while (-not (Read-YesNo "Ist die Public API dort aktiviert?" $true)) {
    Write-Hint "Bitte jetzt in der ACMP-Konsole aktivieren und speichern. Details: docs\ACMP-VORBEREITUNG.md"
  }
}
Write-Ok "Public API bestaetigt"

# ================================================================ 3) Dateien + Build
Write-Step 3 $T "Dateien installieren und bauen"
New-Item -ItemType Directory -Force $InstallDir | Out-Null
icacls $InstallDir /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" | Out-Null
Write-Ok "$InstallDir (Inhalte nur fuer Administratoren und SYSTEM - dort liegen spaeter Zugangsdaten)"
$sameDir = (Resolve-Path $repo).Path.TrimEnd('\') -eq (Resolve-Path $InstallDir).Path.TrimEnd('\')
if (-not $sameDir) {
  foreach ($f in "api-tools.json","db-tools.json","config.example.json","grant-db-readonly.sql","wizard-lib.ps1","add-user.ps1","update-admin.ps1","uninstall-admin.ps1","setup-admin.ps1","set-network-admin.ps1") {
    Copy-Item (Join-Path $repo $f) $InstallDir -Force
  }
  New-Item -ItemType Directory -Force (Join-Path $InstallDir "docs") | Out-Null
  Copy-Item (Join-Path $repo "docs\*.md") (Join-Path $InstallDir "docs") -Force
}
# Normale Benutzer: Ordner auflisten + Start-Skripte lesen (damit sie sich per UAC selbst erhoehen koennen).
# Config, Secrets, Logs und EXEs bleiben nur fuer Administratoren/SYSTEM lesbar.
icacls $InstallDir /grant "*S-1-5-32-545:(RX)" | Out-Null
foreach ($f in "add-user.ps1","wizard-lib.ps1","set-network-admin.ps1") {
  $fp = Join-Path $InstallDir $f
  if (Test-Path $fp) { icacls $fp /grant "*S-1-5-32-545:(RX)" | Out-Null }
}
$exe = Join-Path $InstallDir "AcmpMcpServer.exe"
$firstInstall = -not (Test-Path $exe)
$serverName = if ($firstInstall) { "AcmpMcpServer.exe" } else { "AcmpMcpServer.new.exe" }
& (Join-Path $repo "build.ps1") -OutDir $InstallDir -SicsDir $SicsDir -ServerExeName $serverName | Out-Null
Write-Ok "gebaut ($serverName, acmp-token-setup.exe)"

# ================================================================ 4) SICS-Zugang
Write-Step 4 $T "Zugang zu ACMP (SICS-Benutzer)"
$cfgPath = Join-Path $InstallDir "config.local.json"
$newConfig = -not (Test-Path $cfgPath)
function Set-SicsCredentials {
  $cfg = Read-AcmpConfig $cfgPath
  $u = $SicsUser
  if (-not $u -and $ask) { $u = Read-Value "SICS-/AESB-Benutzername" $(if ($cfg.sics.username -notlike "HIER_*") { $cfg.sics.username } else { "" }) '.+' "Bitte einen Benutzernamen eingeben." }
  $p = $env:ACMP_SICS_PASSWORD
  if (-not $p -and $ask) { $p = Read-Secret "Passwort fuer '$u' (Eingabe unsichtbar, wird nur in config.local.json gespeichert)" }
  if (-not $u -or -not $p) { throw "SICS-Benutzer/Passwort fehlt (-SicsUser und `$env:ACMP_SICS_PASSWORD)." }
  $cfg.sics.username = $u; $cfg.sics.password = $p
  Save-AcmpConfig $cfg $cfgPath
}
if ($newConfig) {
  Write-Info "Der Server meldet sich mit einem SICS-/AESB-Benutzer an ACMP an (z.B. der aus der SICS-Verbindung)."
  $cfg = Get-Content (Join-Path $repo "config.example.json") -Raw | ConvertFrom-Json
  $cfg.mcp.httpPort = $HttpPort
  $cfg.mcp.bindAddress = "127.0.0.1"
  $cfg.approval.publicUrl = "http://127.0.0.1:$HttpPort"
  Save-AcmpConfig $cfg $cfgPath
  Set-SicsCredentials
  Write-Ok "config.local.json angelegt"
} else {
  Write-Ok "config.local.json vorhanden - Zugangsdaten bleiben unveraendert"
}

# ================================================================ 5) Dienst starten + Verbindung pruefen
Write-Step 5 $T "Dienst einrichten und Verbindung zu ACMP pruefen"
if ($firstInstall) {
  & (Join-Path $InstallDir "setup-admin.ps1") -NoStart | Out-Null
} else {
  Stop-ScheduledTask -TaskName $script:TaskName -ErrorAction SilentlyContinue
  Get-Process -Name AcmpMcpServer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 1
  Move-Item (Join-Path $InstallDir "AcmpMcpServer.new.exe") $exe -Force
}
Write-Ok "geplante Aufgabe '$script:TaskName' (SYSTEM, Autostart, Neustart bei Absturz)"
for ($try = 1; ; $try++) {
  $r = Restart-AcmpMcp $InstallDir
  if ($r.State -eq "ok") { Write-Ok "Server laeuft und ist mit ACMP verbunden"; break }
  if ($r.State -eq "sics-fehler") {
    Write-Bad "Anmeldung an ACMP/SICS klappt nicht: $($r.Message)"
    Write-Info "Haeufig: falsches Passwort, Benutzer gesperrt oder Public API nicht freigeschaltet."
    if ($ask -and $try -lt 4 -and (Read-YesNo "Zugangsdaten neu eingeben?" $true)) { Set-SicsCredentials; continue }
    Write-Info "Spaeter korrigieren: sics.username/password in $cfgPath, dann .\install.ps1 erneut."
    exit 2
  }
  Write-Bad $r.Message; exit 2
}

# ================================================================ 6) Erster Administrator
Write-Step 6 $T "Administrator / Freigeber"
$cfg = Read-AcmpConfig $cfgPath
$created = $null
$approvers = @(Get-Approvers $cfg)
if ($approvers.Count -gt 0) {
  Write-Ok ("Freigeber vorhanden: " + (($approvers | ForEach-Object { $_.name }) -join ", "))
  Write-Info "Weitere Kolleg:innen: .\add-user.ps1 (im Ordner $InstallDir)"
} else {
  Write-Info "Jetzt legst du den ersten Bediener an. Er bekommt einen eigenen MCP-API-Key und einen Login fuer die"
  Write-Info "Freigabe-Seite (Vier-Augen-Prinzip: Schreib-/Ausfuehrbefehle laufen erst nach Zustimmung)."
  $defName = ($env:USERNAME -replace '[^A-Za-z0-9_.-]', '').ToLower()
  $name  = if ($AdminName)  { $AdminName }  elseif ($ask) { Read-Value "Kurzname (2-32 Zeichen: Buchstaben, Zahlen, . _ -)" $defName '^[A-Za-z0-9_.-]{2,32}$' "Nur Buchstaben, Zahlen und . _ - (2-32 Zeichen)." } else { $defName }
  $email = if ($AdminEmail) { $AdminEmail } elseif ($ask) { Read-Value "E-Mail fuer Freigabe-Benachrichtigungen" "" '^[^@\s]+@[^@\s]+$' "Bitte eine gueltige E-Mail-Adresse." } else { throw "-AdminEmail fehlt." }
  $acmpU = if ($PSBoundParameters.ContainsKey('AdminAcmpUser')) { $AdminAcmpUser } elseif ($ask) {
    Write-Info "Optional: dein lokaler ACMP-Benutzer (ACMP-Konsole > Benutzer). Dann prueft ACMP deine echten Rechte,"
    Write-Info "statt alles mit dem SICS-Benutzer auszufuehren. Leer lassen = spaeter nachtragen."
    Read-Value "ACMP-Benutzername (optional)" "" '^([A-Za-z0-9_.-]{2,32})?$' "Nur Buchstaben, Zahlen und . _ -"
  } else { "" }
  if ($name -notmatch '^[A-Za-z0-9_.-]{2,32}$') { throw "Ungueltiger Kurzname '$name'." }
  $created = Add-McpUser $cfgPath $name $acmpU $true $email
  Write-Ok "Bediener '$name' angelegt (MCP-API-Key + Freigabe-Login erzeugt)"
}

# ================================================================ 7) ACMP-Identitaet
Write-Step 7 $T "Eigene ACMP-Identitaet verknuepfen"
if ($created -and $created.AcmpUser) {
  Write-Info "Einmalig mit deinem ACMP-Passwort anmelden. Gespeichert wird nur ein verschluesselter Token (DPAPI)."
  if ($ask -and (Read-YesNo "Jetzt verknuepfen?" $true)) {
    Write-Info "Bei 'Domain' einfach Enter druecken (lokaler ACMP-Benutzer)."
    Push-Location $InstallDir
    try { & (Join-Path $InstallDir "acmp-token-setup.exe") $created.AcmpUser } finally { Pop-Location }
    if ($LASTEXITCODE -eq 0) { Write-Ok "ACMP-Identitaet verknuepft" } else { Write-Hint "Verknuepfen fehlgeschlagen - spaeter: cd $InstallDir; .\acmp-token-setup.exe $($created.AcmpUser)" }
  } else { Write-Info "Spaeter: cd $InstallDir; .\acmp-token-setup.exe $($created.AcmpUser)" }
} else { Write-Info "uebersprungen (kein ACMP-Benutzer angegeben bzw. Bediener bestand schon)" }

# ================================================================ 8) Optionale Funktionen
Write-Step 8 $T "Optionale Funktionen"
$cfg = Read-AcmpConfig $cfgPath
$changed = $false
if ($ask) {
  $psActive = $cfg.runPowershell -and $cfg.runPowershell.clientCommandId -and "$($cfg.runPowershell.clientCommandId)" -notlike "HIER_*"
  if ($psActive) { Write-Ok "Ad-hoc-PowerShell aktiv" }
  elseif (Read-YesNo "Ad-hoc-PowerShell auf Clients (acmp_run_powershell) einrichten? Braucht eine Client-Command-Vorlage mit %SCRIPT%" $false) {
    Write-Info "Anleitung zur Vorlage: docs\ACMP-VORBEREITUNG.md > 'Ad-hoc-PowerShell'"
    $g = Read-Value "GUID des Client Commands (leer = ueberspringen)" "" '^(\{?[0-9A-Fa-f]{8}(-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}?)?$' "Bitte eine GUID wie {12345678-1234-1234-1234-123456789ABC}."
    if ($g) { $cfg.runPowershell.clientCommandId = $g; $changed = $true; Write-Ok "Vorlage eingetragen" }
  }
  $dbActive = $cfg.db -and $cfg.db.connectionString -and "$($cfg.db.connectionString)" -notlike "*HIER_*"
  if ($dbActive) { Write-Ok "DB-Report-Tools aktiv" }
  elseif (Read-YesNo "DB-Report-Tools (Schwachstellen, Inventar, Lizenzen) einrichten? Braucht Lesezugriff auf die ACMP-Datenbank" $false) {
    $inst = Read-Value "SQL-Instanz der ACMP-Datenbank (z.B. SQLSERVER\ACMP, leer = ueberspringen)" ""
    if ($inst) {
      $dbn = Read-Value "Datenbankname" "ACMP"
      $cfg.db.connectionString = "Server=$inst;Database=$dbn;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=10"
      $changed = $true
      Write-Ok "Verbindung eingetragen"
      Write-Hint "Der Dienst laeuft als SYSTEM und meldet sich als Computerkonto '$env:USERDOMAIN\$env:COMPUTERNAME`$' an der DB an."
      Write-Info "Leserecht dafuer einmalig setzen: grant-db-readonly.sql (Platzhalter anpassen, als SQL-Admin ausfuehren)."
    }
  }
  $smtpActive = $cfg.approval.notify -and $cfg.approval.notify.enabled -and "$($cfg.approval.notify.smtpHost)" -notlike "HIER_*"
  if ($smtpActive) { Write-Ok "E-Mail-Benachrichtigung aktiv" }
  elseif (Read-YesNo "E-Mail an Freigeber bei neuen Freigabe-Anfragen?" $false) {
    $sh = Read-Value "SMTP-Relay (Hostname/IP, leer = ueberspringen)" ""
    if ($sh) {
      $cfg.approval.notify.smtpHost = $sh
      $cfg.approval.notify.smtpPort = [int](Read-Value "Port" "25" '^\d+$')
      $defFrom = if ($env:USERDNSDOMAIN) { ("acmp-mcp@" + $env:USERDNSDOMAIN).ToLower() } else { "" }
      $cfg.approval.notify.from = Read-Value "Absenderadresse" $defFrom '^[^@\s]+@[^@\s]+$' "Bitte eine gueltige E-Mail-Adresse."
      $cfg.approval.notify.enabled = $true
      $changed = $true
      Write-Ok "SMTP eingetragen (ohne Anmeldung; Benutzer/Passwort bei Bedarf in config.local.json ergaenzen)"
    }
  }
} else { Write-Info "uebersprungen (-NonInteractive)" }
if ($changed) { Save-AcmpConfig $cfg $cfgPath }

# ================================================================ 9) Netzwerk + Abschluss
Write-Step 9 $T "Erreichbarkeit im Netzwerk"
$cfg = Read-AcmpConfig $cfgPath
$curBind = "$($cfg.mcp.bindAddress)"
$networkDone = $false
if ($curBind -and $curBind -notin @("127.0.0.1", "localhost")) {
  Write-Ok "Server ist bereits im Netz erreichbar ($curBind)"
} else {
  Write-Info "Aktuell nur auf diesem Rechner erreichbar. Damit Kolleg:innen von ihren PCs zugreifen, braucht es die LAN-IP."
  $sug = Get-LanSuggestion
  $doNet = if ($BindAddress) { $true } elseif ($ask) { Read-YesNo "Jetzt im LAN freigeben?" $true } else { $false }
  if ($doNet) {
    $ip  = if ($BindAddress) { $BindAddress } else { Read-Value "IP-Adresse dieses Servers" $(if ($sug) { $sug.Ip } else { "" }) '^\d{1,3}(\.\d{1,3}){3}$' "Bitte eine IPv4-Adresse." }
    $sub = if ($AllowedSubnets) { $AllowedSubnets } else {
      Write-Info "Empfehlung: nur das Netz der Admin-PCs zulassen. Mehrere Netze mit Komma trennen."
      (Read-Value "Erlaubte Quell-Netze" $(if ($sug) { $sug.Subnet } else { "LocalSubnet" })) -split '\s*,\s*'
    }
    & (Join-Path $InstallDir "set-network-admin.ps1") -BindAddress $ip -AllowedSubnets $sub | Out-Null
    Write-Ok "Freigegeben: http://${ip}:$($cfg.mcp.httpPort)/ fuer $($sub -join ', ')"
    Write-Hint "Der Server spricht nur HTTP - ueber Netzgrenzen hinweg (Internet, fremde Standorte) TLS-Proxy oder VPN davor."
    $networkDone = $true
  } else { Write-Info "Spaeter: .\set-network-admin.ps1 -BindAddress <IP>" }
}

# Neustart, damit neue Bediener / Identitaet / Optionen aktiv sind (set-network-admin startet selbst neu)
Write-Host ""
Write-Info "Dienst wird neu gestartet, damit alle Aenderungen aktiv sind ..."
$r = Restart-AcmpMcp $InstallDir
if ($r.State -ne "ok") { Write-Bad "Neustart: $($r.Message)"; exit 3 }

$cfg = Read-AcmpConfig $cfgPath
$publicUrl = Get-PublicUrl $cfg
if ($created) {
  try {
    $t = Invoke-McpCall (Get-LocalMcpUrl $cfg) $created.ApiKey "tools/list" @{}
    Write-Ok ("Test: Anmeldung mit dem neuen Key klappt, {0} Tools verfuegbar" -f $t.result.tools.Count)
    $w = Invoke-McpCall (Get-LocalMcpUrl $cfg) $created.ApiKey "tools/call" @{ name = "acmp_whoami"; arguments = @{} }
    ($w.result.content | ForEach-Object { $_.text }) -split "`n" | Where-Object { $_ } | ForEach-Object { Write-Info $_ }
  } catch { Write-Hint "Test-Aufruf fehlgeschlagen: $($_.Exception.Message)" }
}

Write-Title "Fertig"
if ($created) {
  Show-AccessSummary $created $publicUrl (-not $networkDone -and ($curBind -in @("", "127.0.0.1", "localhost")))
  if ($ask) {
    Write-Host ""
    $ch = Read-Choice "Welchen KI-Client nutzt du? Dann zeige ich dir die passende Einrichtung" $script:ClientNames 1
    Show-ClientConfig $ch $publicUrl $created.ApiKey
    Write-Host ""
    Write-Info "Danach den Client neu starten und testen: 'Wer bin ich in ACMP?' (Tool acmp_whoami)."
  }
} else {
  Write-Info "Weboberflaeche:  $publicUrl/dashboard  (Login mit deinem Freigabe-Token)"
}
Write-Host ""
Write-Info "Weitere Kolleg:innen:  cd $InstallDir; .\add-user.ps1"
Write-Info "Aktualisieren:         git pull; .\install.ps1"
Write-Info "Log:                   $(Get-LogPath $InstallDir $cfg)"
