# CC-Writer einrichten: Client Commands als ENTWURF direkt in die ACMP-Datenbank schreiben (optional, standardmaessig aus).
# Braucht Admin-Rechte (startet sich bei Bedarf selbst erhoeht neu).
#   cd C:\ProgramData\AcmpMcp
#   .\setup-cc-writer.ps1                         gefuehrt: fragt Vorlage, Testordner, Antragsteller und das SQL-Kennwort (verdeckt) ab
#   .\setup-cc-writer.ps1 -AddRequester mmuster   Bediener zusaetzlich fuer die Schreib-Tools freischalten (CC- und Kiosk-Writer)
#   .\setup-cc-writer.ps1 -Disable                schaltet den CC-Writer wieder aus
#
# Vorher (siehe docs\ACMP-VORBEREITUNG.md > "Client Commands direkt schreiben"):
#   1. DB-Report-Tools eingerichtet (db.connectionString, Lesezugriff)
#   2. In der ACMP-Konsole: Testordner mit eigenen Rechten und darin ein leerer Vorlage-Command ohne Bedingungen
#   3. SQL-Login mit grant-cc-writer.sql angelegt (als SQL-Admin), Datenbank gesichert
#
# Das Skript prueft Anmeldung, Rechte, Vorlage und Ordner, BEVOR es config.local.json aendert, legt eine Sicherung
# der Config an und startet den Dienst neu. Das Kennwort steht danach nur in config.local.json.
param(
  [string]$SqlServer,
  [string]$Database,
  [string]$Login = 'acmp_mcp_ccwriter',
  [string]$TemplateScriptId,
  [string[]]$AllowedFolderIds,
  [string[]]$AllowedRequesters,
  [string[]]$FileSourceRoots,
  [string]$FileRepositoryRoot,
  [string[]]$AddRequester,
  [switch]$Disable,
  [switch]$NoRestart
)
$ErrorActionPreference = 'Stop'
$base = $PSScriptRoot
. (Join-Path $base "wizard-lib.ps1")
Invoke-SelfElevate $PSCommandPath $PSBoundParameters
$cfgPath = Join-Path $base "config.local.json"
if (-not (Test-Path $cfgPath)) { Write-Bad "config.local.json fehlt - zuerst install.ps1 ausfuehren."; exit 1 }
$guidRx = '^\{?[0-9A-Fa-f]{8}(-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}?$'
function Format-Guid([string]$g) { "{" + ([guid]($g.Trim().Trim('{', '}'))).ToString().ToUpperInvariant() + "}" }

Write-Title "ACMP-MCP-Server - CC-Writer (Client Commands als Entwurf schreiben)"
$cfg = Read-AcmpConfig $cfgPath

function Save-WithBackup($cfg) {
  $bak = "$cfgPath.bak-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
  Copy-Item $cfgPath $bak
  Save-AcmpConfig $cfg $cfgPath
  Write-Ok "config.local.json gespeichert (Sicherung: $(Split-Path $bak -Leaf))"
}
function Restart-IfWanted {
  if ($NoRestart) { Write-Info "Wirksam nach Neustart des Dienstes."; return }
  $r = Restart-AcmpMcp $base
  if ($r.State -eq "ok") { Write-Ok "Dienst neu gestartet" } else { Write-Bad "Neustart: $($r.Message)"; exit 3 }
}

# ---------------------------------------------------------------- Ausschalten
if ($Disable) {
  if ($cfg.ccWriter) { Set-CfgProp $cfg.ccWriter "enabled" $false; Save-WithBackup $cfg; Restart-IfWanted }
  Write-Ok "CC-Writer ist aus. Lesen (acmp_cc_decode, acmp_cc_catalog) bleibt mit den DB-Report-Tools verfuegbar."
  exit 0
}

# ---------------------------------------------------------------- Bediener nachtragen
# Die Schreib-Tools (CC-Writer, acmp_lib_install, Kiosk-Writer) pruefen zusaetzlich zum API-Key, ob der Bediener in
# ccWriter.allowedRequesters steht (Kiosk: kioskWriter.allowedRequesters, solange es die gibt). Gelesen wird beim Start.
if ($AddRequester) {
  if (-not $cfg.ccWriter) { Write-Bad "ccWriter fehlt in config.local.json - zuerst den CC-Writer einrichten."; exit 1 }
  $users = @(Get-McpUsers $cfg | ForEach-Object { $_.name })
  $add = @(Split-ListArg $AddRequester)
  $unknown = @($add | Where-Object { $users -notcontains $_ })
  if ($unknown) { Write-Bad ("Keine Bediener: " + ($unknown -join ", ") + " - erst mit add-user.ps1 oder /onboarding anlegen."); exit 1 }
  $lists = @("ccWriter")
  if ($cfg.kioskWriter -and $cfg.kioskWriter.PSObject.Properties["allowedRequesters"]) { $lists += "kioskWriter" }
  foreach ($sec in $lists) {
    $cur = @($cfg.$sec.allowedRequesters | Where-Object { $_ })
    $new = @($cur + $add | Select-Object -Unique)
    Set-CfgProp $cfg.$sec "allowedRequesters" $new
    Write-Ok ("$sec.allowedRequesters: " + ($new -join ", "))
  }
  Save-WithBackup $cfg
  Restart-IfWanted
  Write-Info "Pruefen: acmp_cc_writer_status zeigt unter allowedRequesters die freigeschalteten Bediener."
  exit 0
}

Write-Hint "Direktes Schreiben in die ACMP-Datenbank ist von Aagon nicht vorgesehen. Vorher die Datenbank sichern."
Write-Info "Der Writer legt nur ENTWUERFE an (nie freigegeben), jede Schreibaktion braucht eine Zustimmung auf der"
Write-Info "Freigabe-Seite, und geschrieben wird nur nach bestandener Kompatibilitaetspruefung (acmp_cc_writer_status)."
if (-not (Read-YesNo "Weiter?" $false)) { Write-Info "Abgebrochen - nichts geaendert."; exit 0 }

# ---------------------------------------------------------------- Voraussetzung: Lesezugang
$readCs = if ($cfg.db) { "$($cfg.db.connectionString)" } else { "" }
if (-not $readCs -or $readCs -like "*HIER_*") {
  Write-Bad "db.connectionString fehlt. Zuerst die DB-Report-Tools einrichten (install.ps1, Schritt 'Optionale Funktionen')."
  exit 1
}
$csb = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $readCs
if (-not $SqlServer) { $SqlServer = Read-Value "SQL-Instanz der ACMP-Datenbank" $csb.DataSource '.+' }
if (-not $Database)  { $Database  = Read-Value "Datenbankname" $(if ($csb.InitialCatalog) { $csb.InitialCatalog } else { "ACMP" }) '.+' }

# ---------------------------------------------------------------- Vorlage, Ordner, Antragsteller
Write-Info ""
Write-Info "Vorlage: ein leerer Client Command OHNE Bedingungen (z.B. 'MCP Vorlage'). Von ihm kopiert der Writer Flags,"
Write-Info "Bedingungen und das interne Kennwortfeld. GUID: ACMP-Konsole > Client Commands > Command > Eigenschaften."
if (-not $TemplateScriptId) { $TemplateScriptId = Read-Value "GUID des Vorlage-Commands" "" $guidRx "Bitte eine GUID wie {12345678-1234-1234-1234-123456789ABC}." }
$TemplateScriptId = Format-Guid $TemplateScriptId

Write-Info ""
Write-Info "Testordner: Entwuerfe nur dort anlegen. Der Ordner braucht eigene Rechte-Eintraege (Ordner > Rechte),"
Write-Info "sonst waeren die Entwuerfe in der Konsole unsichtbar. Mehrere GUIDs mit Komma trennen."
if (-not $AllowedFolderIds) {
  $AllowedFolderIds = (Read-Value "GUID des Testordners" "" '^(\{?[0-9A-Fa-f]{8}(-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}?\s*,?\s*)+$' "Bitte eine oder mehrere GUIDs.") -split '\s*,\s*'
}
$AllowedFolderIds = @(Split-ListArg $AllowedFolderIds | ForEach-Object { Format-Guid $_ })

$users = @(Get-McpUsers $cfg | ForEach-Object { $_.name } | Select-Object -Unique)
if (-not $AllowedRequesters) {
  Write-Info ""
  Write-Info ("Bediener: " + $(if ($users) { $users -join ", " } else { "(keine)" }))
  $AllowedRequesters = (Read-Value "Wer darf Entwuerfe beantragen? (Kurznamen, Komma)" $(if ($users) { $users[0] } else { "" }) '.+') -split '\s*,\s*'
}
$AllowedRequesters = @(Split-ListArg $AllowedRequesters)
$unknown = @($AllowedRequesters | Where-Object { $users -notcontains $_ })
if ($unknown) { Write-Hint ("Unbekannte Bediener: " + ($unknown -join ", ") + " (werden trotzdem eingetragen)") }

# ---------------------------------------------------------------- Dateien (optional)
Write-Info ""
Write-Info "Optional: Dateien an Entwuerfe anhaengen (acmp_cc_add_file). Dafuer braucht es erlaubte UNC-Freigaben mit"
Write-Info "Installationsdateien (der Dienst liest als Computerkonto) und den Pfad des ACMP File Repository."
$prevRoots = if ($cfg.ccWriter -and $cfg.ccWriter.fileSourceRoots) { @($cfg.ccWriter.fileSourceRoots) } else { @() }
$prevRepo  = if ($cfg.ccWriter -and $cfg.ccWriter.fileRepositoryRoot) { "$($cfg.ccWriter.fileRepositoryRoot)" } else { "" }
if (-not $PSBoundParameters.ContainsKey('FileSourceRoots')) {
  $v = Read-Value "Erlaubte UNC-Freigaben (z.B. \\server\software, Komma; leer = keine Datei-Tools)" ($prevRoots -join ", ") '^(\\\\[^,]+(\s*,\s*\\\\[^,]+)*)?$' "Nur UNC-Pfade, die mit \\ beginnen."
  $FileSourceRoots = if ($v) { $v -split '\s*,\s*' } else { @() }
}
$FileSourceRoots = @(Split-ListArg $FileSourceRoots)
if ($FileSourceRoots -and -not $PSBoundParameters.ContainsKey('FileRepositoryRoot')) {
  $FileRepositoryRoot = Read-Value "File Repository der Client Commands auf diesem Server (...\ClientCommands\Projects)" $prevRepo
}
if ($FileRepositoryRoot -and -not (Test-Path $FileRepositoryRoot)) { Write-Hint "Ordner nicht gefunden: $FileRepositoryRoot - die Datei-Tools bleiben gesperrt, bis der Pfad stimmt." }

# ---------------------------------------------------------------- Kennwort + Pruefung
Write-Info ""
$pw = Read-Secret "Kennwort fuer SQL-Login $Login (Eingabe unsichtbar, wird nur in config.local.json gespeichert)"
if ([string]::IsNullOrEmpty($pw)) { Write-Bad "Kein Kennwort eingegeben - nichts geaendert."; exit 1 }
$wb = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
$wb["Data Source"] = $SqlServer; $wb["Initial Catalog"] = $Database; $wb["User ID"] = $Login; $wb["Password"] = $pw
$wb["Application Name"] = "ACMP-MCP-CcWriter"; $wb["Connect Timeout"] = 15
if ($csb.ContainsKey("TrustServerCertificate") -and $csb["TrustServerCertificate"]) { $wb["TrustServerCertificate"] = $true }
$writeCs = $wb.ConnectionString
$pw = $null

$c = New-Object System.Data.SqlClient.SqlConnection $writeCs
try { $c.Open() } catch { Write-Bad "Anmeldung an $SqlServer als $Login fehlgeschlagen: $($_.Exception.Message)"; Write-Info "Nichts geaendert."; exit 1 }
function Get-Scalar([string]$sql, [hashtable]$p = @{}) {
  $q = $c.CreateCommand(); $q.CommandText = $sql
  foreach ($k in $p.Keys) { [void]$q.Parameters.AddWithValue($k, $p[$k]) }
  $q.ExecuteScalar()
}
$ok = $true
$need = @("SYS_SCRIPTS:SELECT","SYS_SCRIPTS:INSERT","SYS_SCRIPTS:UPDATE","SYS_SCRIPTS_VARIABLES:SELECT","SYS_SCRIPTS_VARIABLES:INSERT",
          "SYS_SCRIPTS_VARIABLES:DELETE","SYS_SCRIPT_STRUCTURE:SELECT","SYS_Folders_AclEntries_Users:SELECT","SYS_Folders_AclEntries_Groups:SELECT",
          "SYS_AccessControl_AclEntries_Users:INSERT","SYS_AccessControl_AclEntries_Groups:INSERT")
$fileNeed = @("SYS_SCRIPT_FILES:SELECT","SYS_SCRIPT_FILES:INSERT","SYS_SCRIPT_FILES:DELETE")
$folderNeed = @("SYS_SCRIPT_STRUCTURE:INSERT","SYS_Folders_AclEntries_Users:INSERT","SYS_Folders_AclEntries_Groups:INSERT")
function Test-Perms([string[]]$list) { @($list | Where-Object { $t, $p = $_ -split ':'; (Get-Scalar "SELECT HAS_PERMS_BY_NAME(@o,'OBJECT',@p)" @{ '@o' = "dbo.$t"; '@p' = $p }) -ne 1 }) }
function Test-ColPerm([string]$t, [string]$col) { (Get-Scalar "SELECT HAS_PERMS_BY_NAME(@o,'OBJECT','UPDATE',@c,'COLUMN')" @{ '@o' = "dbo.$t"; '@c' = $col }) -eq 1 }
try {
  $miss = Test-Perms $need
  if ($miss) { Write-Bad ("Rechte fehlen: " + ($miss -join ", ") + " -> grant-cc-writer.sql Abschnitt 3 ausfuehren"); $ok = $false } else { Write-Ok "Rechte des Schreib-Logins vollstaendig (Entwuerfe, Freigeben, Verschieben)" }
  if ((Get-Scalar "SELECT HAS_PERMS_BY_NAME('dbo.SYS_SCRIPTS','OBJECT','DELETE')") -eq 1) { Write-Hint "Der Login darf in SYS_SCRIPTS LOESCHEN - das braucht er nicht. Empfohlen: Recht entziehen." }
  if ((Test-Perms @("SYS_SCRIPT_FILES:SELECT"))) { Write-Hint "SELECT auf SYS_SCRIPT_FILES fehlt - acmp_cc_release bleibt gesperrt (grant-cc-writer.sql Abschnitt 3)" }
  if ($FileSourceRoots) {
    $fmiss = Test-Perms $fileNeed
    if ($fmiss) { Write-Hint ("Datei-Rechte fehlen: " + ($fmiss -join ", ") + " - Datei-Tools bleiben gesperrt (Abschnitt 4)") } else { Write-Ok "Datei-Rechte vorhanden" }
  }
  $dmiss = Test-Perms $folderNeed
  if ($dmiss) { Write-Info "Ordner anlegen (acmp_cc_add_folder) bleibt gesperrt - optional: grant-cc-writer.sql Abschnitt 5" } else { Write-Ok "Ordner anlegen erlaubt" }
  if ((Test-ColPerm "SYS_SCRIPT_STRUCTURE" "Description") -and (Test-ColPerm "SYS_SCRIPT_STRUCTURE" "ParentID")) { Write-Ok "Ordner umbenennen/verschieben erlaubt" }
  else { Write-Info "Ordner umbenennen/verschieben (acmp_cc_update_folder) bleibt gesperrt - optional: grant-cc-writer.sql Abschnitt 6" }
  $q = $c.CreateCommand()
  $q.CommandText = "SELECT TOP 1 CAST(HasConditions AS int), DESCRIPTION FROM SYS_SCRIPTS WHERE SCRIPTID=@t ORDER BY ScriptVersion DESC"
  [void]$q.Parameters.AddWithValue('@t', $TemplateScriptId)
  $r = $q.ExecuteReader()
  if (-not $r.Read()) { Write-Bad "Vorlage $TemplateScriptId nicht gefunden"; $ok = $false }
  elseif ($r.GetInt32(0) -ne 0) { Write-Bad "Vorlage '$($r.GetString(1))' hat Bedingungen - bitte einen Command ohne Bedingungen nehmen"; $ok = $false }
  else { Write-Ok "Vorlage: '$($r.GetString(1))'" }
  $r.Close()
  foreach ($f in $AllowedFolderIds) {
    $q = $c.CreateCommand()
    $q.CommandText = "SELECT s.Description, (SELECT COUNT(*) FROM SYS_Folders_AclEntries_Users u WHERE u.ObjectID=@g) + (SELECT COUNT(*) FROM SYS_Folders_AclEntries_Groups g WHERE g.ObjectID=@g) FROM SYS_SCRIPT_STRUCTURE s WHERE s.ScriptStructureID=@f"
    [void]$q.Parameters.AddWithValue('@f', $f); [void]$q.Parameters.AddWithValue('@g', [guid]($f.Trim('{', '}')))
    $r = $q.ExecuteReader()
    if (-not $r.Read()) { Write-Bad "Ordner $f nicht gefunden"; $ok = $false }
    elseif ([int]$r.GetValue(1) -eq 0) { Write-Bad "Ordner '$($r.GetString(0))' hat keine eigenen Rechte-Eintraege (Entwuerfe waeren unsichtbar)"; $ok = $false }
    else { Write-Ok "Testordner: '$($r.GetString(0))' ($($r.GetValue(1)) Rechte-Eintraege)" }
    $r.Close()
  }
} finally { $c.Close() }
if (-not $ok) { Write-Info "Nichts geaendert. Punkte oben beheben und erneut starten."; exit 1 }

# ---------------------------------------------------------------- Config schreiben
if (-not $cfg.ccWriter) { $cfg | Add-Member -NotePropertyName ccWriter -NotePropertyValue ([pscustomobject]@{}) }
$w = $cfg.ccWriter
Set-CfgProp $w "enabled" $true
Set-CfgProp $w "connectionString" $writeCs
Set-CfgProp $w "templateScriptId" $TemplateScriptId
Set-CfgProp $w "allowedRequesters" $AllowedRequesters
Set-CfgProp $w "allowedFolderIds" $AllowedFolderIds
Set-CfgProp $w "approvalGate" $true
if (-not $w.PSObject.Properties["allowUntested"]) { Set-CfgProp $w "allowUntested" $false }
Set-CfgProp $w "fileSourceRoots" $FileSourceRoots
Set-CfgProp $w "fileRepositoryRoot" $(if ($FileRepositoryRoot) { $FileRepositoryRoot } else { "" })
if (-not $w.PSObject.Properties["backupDir"]) { Set-CfgProp $w "backupDir" "cc-backups" }
if ($w.PSObject.Properties["approvers"]) { $w.PSObject.Properties.Remove("approvers") }
$writeCs = $null
Save-WithBackup $cfg
Restart-IfWanted

Write-Host ""
Write-Ok "CC-Writer eingerichtet."
Write-Info "Pruefen: im MCP-Client 'acmp_cc_writer_status' aufrufen - dort muss 'bestanden' stehen."
Write-Info "Danach z.B.: 'Lege im Testordner einen Entwurf an, der ...' - die Freigabe-Anfrage zeigt jeden Schritt."
Write-Info "Weitere Bediener freischalten: .\setup-cc-writer.ps1 -AddRequester <kurzname>"
Write-Info "Kiosk pflegen (optional): grant-kiosk-writer.sql, dann .\setup-kiosk-writer.ps1"
Write-Info "Ausschalten: .\setup-cc-writer.ps1 -Disable"
