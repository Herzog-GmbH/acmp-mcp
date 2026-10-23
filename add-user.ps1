# Weiteren Bediener fuer den ACMP-MCP-Server anlegen (braucht Admin-Rechte - startet sich bei Bedarf selbst erhoeht neu).
#   cd C:\ProgramData\AcmpMcp
#   .\add-user.ps1
#   .\add-user.ps1 -Name mmustermann -AcmpUser admin_mm [-NoWeb] [-Writer]
#
# Wie /onboarding auf der Weboberflaeche, nur im Terminal: erzeugt MCP-API-Key und (ohne -NoWeb) einen
# Login fuer die Weboberflaeche, startet den Dienst neu und zeigt am Ende Zugang und Client-Konfiguration.
# Ist der CC-Writer eingerichtet, fragt es zusaetzlich, ob die Person Client Commands und den Kiosk
# schreiben darf (ccWriter.allowedRequesters). Ueber /onboarding angelegte Bediener schaltet ihr dafuer
# mit .\setup-cc-writer.ps1 -AddRequester <kurzname> frei.
param(
  [string]$Name,
  [string]$AcmpUser,
  [switch]$NoWeb,
  [switch]$Approver,   # veraltet (Version 1), wird ignoriert - Web-Zugang gibt es ohne -NoWeb immer
  [string]$Email,      # veraltet (Version 1), wird ignoriert
  [switch]$Writer,
  [switch]$NonInteractive
)
$ErrorActionPreference = 'Stop'
$base = $PSScriptRoot
. (Join-Path $base "wizard-lib.ps1")
Invoke-SelfElevate $PSCommandPath $PSBoundParameters
$ask = -not $NonInteractive
$cfgPath = Join-Path $base "config.local.json"
if (-not (Test-Path $cfgPath)) { Write-Bad "config.local.json fehlt - zuerst install.ps1 ausfuehren."; exit 1 }

Write-Title "ACMP-MCP-Server - neuen Bediener anlegen"
$cfg = Read-AcmpConfig $cfgPath
$existing = @(Get-McpUsers $cfg)
$webNames = @(Get-WebUsers $cfg | ForEach-Object { $_.name })
if ($existing.Count) {
  Write-Info "Vorhandene Bediener:"
  foreach ($u in $existing) { Write-Info ("  - {0,-20} ACMP: {1,-16} Web-Zugang: {2}" -f $u.name, $(if ($u.acmpUser) { $u.acmpUser } else { "-" }), $(if ($webNames -contains $u.name) { "ja" } else { "-" })) }
}

Write-Step 1 3 "Angaben"
$idRe = '^[A-Za-z0-9_.-]{2,32}$'
if (-not $Name) {
  if (-not $ask) { throw "-Name fehlt." }
  while ($true) {
    $Name = Read-Value "Kurzname (z.B. mmustermann)" "" $idRe "Nur Buchstaben, Zahlen und . _ - (2-32 Zeichen)."
    if ($existing | Where-Object { $_.name -ieq $Name }) { Write-Hint "'$Name' gibt es schon." } else { break }
  }
}
if (-not $PSBoundParameters.ContainsKey('AcmpUser') -and $ask) {
  Write-Info "Lokaler ACMP-Benutzer der Person (in der ACMP-Konsole angelegt). Dann prueft ACMP ihre echten Rechte."
  $AcmpUser = Read-Value "ACMP-Benutzername (optional)" "" '^([A-Za-z0-9_.-]{2,32})?$' "Nur Buchstaben, Zahlen und . _ -"
}
$hasWeb = -not $NoWeb
if (-not $PSBoundParameters.ContainsKey('NoWeb') -and $ask) {
  Write-Info "Die Weboberflaeche zeigt Aktivitaetsprotokoll und Notbremse und erlaubt das Onboarding weiterer Bediener."
  $hasWeb = Read-YesNo "Soll '$Name' Zugang zur Weboberflaeche bekommen?" $true
}
$ccOn = $cfg.ccWriter -and $cfg.ccWriter.enabled
$isWriter = [bool]$Writer
if ($ccOn -and -not $PSBoundParameters.ContainsKey('Writer') -and $ask) {
  Write-Info "Schreib-Tools (Client Commands als Entwurf, Freigeben, Ordner, Kiosk) laufen ueber einen eigenen SQL-Login des Servers."
  Write-Info "Wer sie nutzen darf, steht in ccWriter.allowedRequesters. Sie wirken sofort und stehen im Aktivitaetsprotokoll."
  $isWriter = Read-YesNo "Darf '$Name' diese Schreib-Tools nutzen?" $false
}
if ($isWriter -and -not $ccOn) { Write-Hint "CC-Writer ist nicht eingerichtet - Schreibrecht wird nicht eingetragen (spaeter: setup-cc-writer.ps1)."; $isWriter = $false }

Write-Step 2 3 "Anlegen und aktivieren"
$created = Add-McpUser $cfgPath $Name $AcmpUser $hasWeb
Write-Ok "Bediener '$Name' angelegt"
if ($isWriter) {
  $cfg = Read-AcmpConfig $cfgPath
  $lists = @("ccWriter")
  if ($cfg.kioskWriter -and $cfg.kioskWriter.PSObject.Properties["allowedRequesters"]) { $lists += "kioskWriter" }
  foreach ($sec in $lists) { Set-CfgProp $cfg.$sec "allowedRequesters" @(@($cfg.$sec.allowedRequesters | Where-Object { $_ }) + $Name | Select-Object -Unique) }
  Save-AcmpConfig $cfg $cfgPath
  Write-Ok "'$Name' darf die Schreib-Tools nutzen ($($lists -join ', ').allowedRequesters)"
}
$r = Restart-AcmpMcp $base
if ($r.State -ne "ok") { Write-Bad "Neustart: $($r.Message)"; exit 2 }
Write-Ok "Dienst neu gestartet - neuer Key ist aktiv"
$cfg = Read-AcmpConfig $cfgPath
try {
  $t = Invoke-McpCall (Get-LocalMcpUrl $cfg) $created.ApiKey "tools/list" @{}
  Write-Ok ("Test mit dem neuen Key: {0} Tools verfuegbar" -f $t.result.tools.Count)
} catch { Write-Hint "Test-Aufruf fehlgeschlagen: $($_.Exception.Message)" }

Write-Step 3 3 "Weitergeben"
$publicUrl = Get-PublicUrl $cfg
Show-AccessSummary $created $publicUrl ("$($cfg.mcp.bindAddress)" -in @("", "127.0.0.1", "localhost"))
Write-Hint "Key und Web-Token sicher weitergeben (z.B. 1Password-Freigabe), nicht per unverschluesselter Mail/Chat."
if ($AcmpUser) {
  Write-Host ""
  Write-Info "ACMP-Identitaet verknuepfen - muss die Person selbst tun (braucht ihr ACMP-Passwort), auf diesem Server als Admin:"
  Write-Host "  cd $base" -ForegroundColor White
  Write-Host "  .\acmp-token-setup.exe $AcmpUser" -ForegroundColor White
  Write-Info "danach Dienst neu starten:  Stop-ScheduledTask $script:TaskName; Start-ScheduledTask $script:TaskName"
}
if ($ask) {
  Write-Host ""
  $ch = Read-Choice "Welchen KI-Client nutzt '$Name'?" $script:ClientNames 1
  Show-ClientConfig $ch $publicUrl $created.ApiKey
}
