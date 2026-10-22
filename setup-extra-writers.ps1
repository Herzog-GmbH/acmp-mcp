# Weitere Schreib-Tools einrichten (optional, standardmaessig aus). Braucht Admin-Rechte (startet sich bei Bedarf selbst erhoeht neu).
#   wum   Windows-Updates ablehnen / Ablehnung aufheben (acmp_wum_decline, acmp_wum_restore)          Rechte: grant-wum-writer.sql
#   unix  Unix Scripts anlegen, neue Version, Verteilungsring (acmp_unix_script_*), ab ACMP 6.10      Rechte: grant-unix-writer.sql
#   cf    Individuelle Felder anlegen (acmp_cf_create)                                                Rechte: grant-cf-params.sql
# acmp_cc_set_parameters (Parameter eines Client Commands) gehoert zum CC-Writer und braucht nur grant-cf-params.sql Abschnitt 3.
#
#   cd C:\ProgramData\AcmpMcp
#   .\setup-extra-writers.ps1                      gefuehrt: fragt je Funktion nach
#   .\setup-extra-writers.ps1 -Features wum,unix   nur diese einrichten
#   .\setup-extra-writers.ps1 -Disable             alle drei wieder ausschalten (oder mit -Features nur einzelne)
#
# Vorher (siehe docs\ACMP-VORBEREITUNG.md > "Weitere Schreib-Tools"):
#   1. CC-Writer eingerichtet (setup-cc-writer.ps1) - alle drei nutzen dessen SQL-Login, Bediener-Liste und Freigabe-Einstellung
#   2. Datenbank gesichert, das passende grant-*.sql als SQL-Admin ausgefuehrt
#
# Das Skript prueft die Rechte des Schreib-Logins, BEVOR es config.local.json aendert, schaltet nur Funktionen ein, deren
# Rechte vollstaendig sind, legt eine Sicherung der Config an und startet den Dienst neu.
param(
  [string[]]$Features,
  [string[]]$UnixFolderIds,
  [switch]$Disable,
  [switch]$NoRestart
)
$ErrorActionPreference = 'Stop'
$base = $PSScriptRoot
. (Join-Path $base "wizard-lib.ps1")
Invoke-SelfElevate $PSCommandPath $PSBoundParameters
$cfgPath = Join-Path $base "config.local.json"
if (-not (Test-Path $cfgPath)) { Write-Bad "config.local.json fehlt - zuerst install.ps1 ausfuehren."; exit 1 }
function Format-Guid([string]$g) { "{" + ([guid]($g.Trim().Trim('{', '}'))).ToString().ToUpperInvariant() + "}" }

$all = [ordered]@{
  wum  = @{ Section = "wumWriter";  Name = "Windows-Updates ablehnen (acmp_wum_decline, acmp_wum_restore)"; Sql = "grant-wum-writer.sql"; Backup = "wum-backups";
            Check = "acmp_wum_declined mit status=true" }
  unix = @{ Section = "unixWriter"; Name = "Unix Scripts schreiben (acmp_unix_script_create, _new_version, _set_ring)"; Sql = "grant-unix-writer.sql"; Backup = "unix-backups";
            Check = "acmp_unix_scripts mit status=true" }
  cf   = @{ Section = "cfWriter";   Name = "Individuelle Felder anlegen (acmp_cf_create)"; Sql = "grant-cf-params.sql"; Backup = "cf-backups";
            Check = "acmp_cf_list mit status=true" }
}
$sel = @(Split-ListArg $Features | ForEach-Object { $_.ToLowerInvariant() })
foreach ($f in $sel) { if (-not $all.Contains($f)) { Write-Bad "Unbekannte Funktion '$f' (erlaubt: wum, unix, cf)"; exit 1 } }

Write-Title "ACMP-MCP-Server - weitere Schreib-Tools (Windows-Updates, Unix Scripts, individuelle Felder)"
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
  $off = if ($sel) { $sel } else { @($all.Keys) }
  $n = 0
  foreach ($f in $off) {
    $s = $all[$f].Section
    if ($cfg.$s -and $cfg.$s.enabled) { Set-CfgProp $cfg.$s "enabled" $false; $n++ }
    Write-Ok ("aus: " + $all[$f].Name)
  }
  if ($n -gt 0) { Save-WithBackup $cfg; Restart-IfWanted }
  Write-Info "Die Lesetools (acmp_wum_declined, acmp_unix_scripts, acmp_unix_results, acmp_cf_list) bleiben verfuegbar."
  exit 0
}

# ---------------------------------------------------------------- Voraussetzung: CC-Writer-Login
$writeCs = if ($cfg.ccWriter) { "$($cfg.ccWriter.connectionString)" } else { "" }
if (-not $writeCs -or $writeCs -like "*HIER_*") {
  Write-Bad "Kein Schreib-Login (ccWriter.connectionString). Zuerst den CC-Writer einrichten: .\setup-cc-writer.ps1"
  exit 1
}
$req = @($cfg.ccWriter.allowedRequesters | Where-Object { $_ })
if (-not $req) { Write-Bad "ccWriter.allowedRequesters ist leer - zuerst .\setup-cc-writer.ps1 (oder -AddRequester)."; exit 1 }

Write-Hint "Direktes Schreiben in die ACMP-Datenbank ist von Aagon nicht vorgesehen. Vorher die Datenbank sichern."
Write-Info "Jede Schreibaktion dieser Tools laeuft IMMER ueber die Vier-Augen-Freigabe (auch bei bypassAll) und wird vorher gesichert."
Write-Info ("Bediener mit Schreibrecht (aus ccWriter.allowedRequesters): " + ($req -join ", "))
Write-Info "Getestet mit ACMP 6.10. Auf anderen Versionen sperrt die Vorpruefung, bis allowUntested gesetzt ist (nicht empfohlen)."

# ---------------------------------------------------------------- Auswahl
if (-not $sel) {
  foreach ($f in $all.Keys) {
    $s = $all[$f].Section
    if ($cfg.$s -and $cfg.$s.enabled) { Write-Ok ("schon aktiv: " + $all[$f].Name); continue }
    if (Read-YesNo ($all[$f].Name + "? Rechte vorher mit " + $all[$f].Sql) $false) { $sel += $f }
  }
}
if (-not $sel) { Write-Info "Nichts ausgewaehlt - nichts geaendert."; exit 0 }

# ---------------------------------------------------------------- Rechte pruefen
$c = New-Object System.Data.SqlClient.SqlConnection $writeCs
try { $c.Open() } catch { Write-Bad "Anmeldung mit dem Schreib-Login fehlgeschlagen: $($_.Exception.Message)"; Write-Info "Nichts geaendert."; exit 1 }
function Test-Perm([string]$obj, [string]$class, [string]$perm, [string]$col = $null) {
  $q = $c.CreateCommand()
  if ($class -eq "DATABASE") { $q.CommandText = "SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', @p)" }
  elseif ($col) { $q.CommandText = "SELECT HAS_PERMS_BY_NAME(@o, @k, @p, @c, 'COLUMN')"; [void]$q.Parameters.AddWithValue('@c', $col) }
  else { $q.CommandText = "SELECT HAS_PERMS_BY_NAME(@o, @k, @p)" }
  if ($class -ne "DATABASE") { [void]$q.Parameters.AddWithValue('@o', $obj); [void]$q.Parameters.AddWithValue('@k', $class) }
  [void]$q.Parameters.AddWithValue('@p', $perm)
  $r = $q.ExecuteScalar()
  ($r -ne $null) -and ($r -isnot [DBNull]) -and ([int]$r -eq 1)
}
# je Funktion: Pflicht-Rechte als "Objekt|Klasse|Recht[|Spalte]"
$need = @{
  wum  = @("dbo.ap_Wum_DeclineUpdates|OBJECT|EXECUTE", "dbo.at_IDList|TYPE|EXECUTE", "dbo.Sys_Wum_DeclinedUpdates|OBJECT|SELECT")
  unix = @("dbo.SYS_UnixClient_Scripts|OBJECT|SELECT", "dbo.SYS_UnixClient_Scripts|OBJECT|INSERT",
           "dbo.SYS_UnixClient_Scripts_Versions|OBJECT|SELECT", "dbo.SYS_UnixClient_Scripts_Versions|OBJECT|INSERT",
           "dbo.SYS_TestRings|OBJECT|SELECT", "dbo.SYS_TestRings|OBJECT|INSERT",
           "dbo.SYS_TestRings|OBJECT|UPDATE|TestRing", "dbo.SYS_TestRings|OBJECT|UPDATE|LastChangeDate",
           "dbo.SYS_AccessControl_AclEntries_Users|OBJECT|INSERT", "dbo.SYS_AccessControl_AclEntries_Groups|OBJECT|INSERT")
  cf   = @("dbo.mcp_cf_create_table|OBJECT|EXECUTE", "dbo.mcp_cf_add_column|OBJECT|EXECUTE",
           "dbo.SYS_TableRelations|OBJECT|INSERT", "dbo.SYS_CustomFields|OBJECT|INSERT", "dbo.VW_SYS_DISPLAYFIELDS|OBJECT|INSERT")
}
$ready = @()
try {
  foreach ($f in $sel) {
    $miss = @($need[$f] | Where-Object { $p = $_ -split '\|'; -not (Test-Perm $p[0] $p[1] $p[2] $(if ($p.Count -gt 3) { $p[3] } else { $null })) } |
              ForEach-Object { $p = $_ -split '\|'; if ($p.Count -gt 3) { "$($p[0]).$($p[3]):$($p[2])" } else { "$($p[0]):$($p[2])" } })
    if ($miss) { Write-Bad ($all[$f].Name + " - Rechte fehlen: " + ($miss -join ", ") + " -> " + $all[$f].Sql + " ausfuehren") ; continue }
    Write-Ok ($all[$f].Name + " - Rechte vollstaendig")
    if ($f -eq "wum") {
      if (-not (Test-Perm "dbo.Sys_Wum_DeclinedUpdates" "OBJECT" "DELETE")) { Write-Hint "Ohne DELETE auf Sys_Wum_DeclinedUpdates bleibt acmp_wum_restore gesperrt (grant-wum-writer.sql Abschnitt 3)." }
      if (-not (Test-Perm "dbo.ap_Wum_Rebuild_UpdateFlags" "OBJECT" "EXECUTE")) { Write-Hint "Ohne EXECUTE auf ap_Wum_Rebuild_UpdateFlags setzt ACMP das Flag IsDeclined erst beim naechsten eigenen Lauf." }
    }
    if ($f -eq "unix") {
      if (Test-Perm "dbo.SYS_TestRings" "OBJECT" "DELETE") { Write-Hint "Der Login darf SYS_TestRings LOESCHEN - das braucht er nicht (dort stehen auch die Ringe der Windows-Updates). Empfohlen: Recht entziehen." }
    }
    if ($f -eq "cf") {
      if (Test-Perm "" "DATABASE" "CREATE TABLE") { Write-Hint "Der Login darf selbst Tabellen anlegen - das braucht er nicht (die DDL laeuft ueber die Prozeduren). Empfohlen: db_ddladmin/db_owner entfernen." }
    }
    $ready += $f
  }
  $par = @("dbo.SYS_Jobs_Parameters_Declarations", "dbo.SYS_Jobs_Parameters_Config", "dbo.SYS_Jobs_Parameters_Config_Values") |
         Where-Object { -not (Test-Perm $_ "OBJECT" "INSERT") }
  if ($par) { Write-Info "acmp_cc_set_parameters (CC-Writer) bleibt gesperrt, bis grant-cf-params.sql Abschnitt 3 gelaufen ist." }
  else { Write-Ok "Rechte fuer acmp_cc_set_parameters vorhanden (Teil des CC-Writers, kein eigener Schalter)" }
} finally { $c.Close() }
if (-not $ready) { Write-Info "Nichts eingeschaltet. Punkte oben beheben und erneut starten."; exit 1 }

# ---------------------------------------------------------------- Unix: Ordner (optional)
$unixFolders = @()
if ($ready -contains "unix" -and $UnixFolderIds) {
  foreach ($g in (Split-ListArg $UnixFolderIds)) { try { $unixFolders += Format-Guid $g } catch { Write-Bad "Keine GUID: $g - nichts geaendert."; exit 1 } }
  Write-Ok ("Unix Scripts nur in diesen Ordnern: " + ($unixFolders -join ", "))
} elseif ($ready -contains "unix") {
  Write-Info "Unix Scripts: alle Unix-Script-Ordner erlaubt (einschraenken mit -UnixFolderIds <GUID>,<GUID>)."
}

# ---------------------------------------------------------------- Config schreiben
foreach ($f in $ready) {
  $s = $all[$f].Section
  if (-not $cfg.$s) { $cfg | Add-Member -NotePropertyName $s -NotePropertyValue ([pscustomobject]@{}) }
  $w = $cfg.$s
  Set-CfgProp $w "enabled" $true
  Set-CfgProp $w "approvalGate" $true
  if (-not $w.PSObject.Properties["allowUntested"]) { Set-CfgProp $w "allowUntested" $false }
  if (-not $w.PSObject.Properties["backupDir"]) { Set-CfgProp $w "backupDir" $all[$f].Backup }
  if ($f -eq "wum" -and -not $w.PSObject.Properties["rebuildFlags"]) { Set-CfgProp $w "rebuildFlags" $true }
  if ($f -eq "unix" -and ($unixFolders -or -not $w.PSObject.Properties["allowedFolderIds"])) { Set-CfgProp $w "allowedFolderIds" $unixFolders }
}
Save-WithBackup $cfg
Restart-IfWanted

Write-Host ""
foreach ($f in $ready) { Write-Ok ("eingerichtet: " + $all[$f].Name + " - pruefen im MCP-Client: " + $all[$f].Check) }
Write-Info "Dort muss die Vorpruefung bestanden sein. Jede Schreibaktion erscheint als Anfrage auf der Freigabe-Seite."
if ($ready -contains "unix") { Write-Info "Unix Scripts: Den Job startet ihr in der ACMP-Console (Container/Abfrage > Unix-Jobs ausfuehren). Ohne Unix-Script-Lizenz meldet der Job 'Erfolg', laeuft aber nicht - acmp_unix_results weist darauf hin." }
Write-Info "Ausschalten: .\setup-extra-writers.ps1 -Disable"
