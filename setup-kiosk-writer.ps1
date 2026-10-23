# Kiosk-Writer einrichten: Kiosk-Eintraege, -Ordner, Sichtbarkeitsfilter und Icons direkt in der ACMP-Datenbank pflegen
# (optional, standardmaessig aus). Braucht Admin-Rechte (startet sich bei Bedarf selbst erhoeht neu).
#   cd C:\ProgramData\AcmpMcp
#   .\setup-kiosk-writer.ps1            gefuehrt: fragt die freigegebenen Kiosk-Ordner und optional die Bild-Ablage ab
#   .\setup-kiosk-writer.ps1 -Disable   schaltet den Kiosk-Writer wieder aus
#
# Vorher (siehe docs\ACMP-VORBEREITUNG.md > "Kiosk pflegen"):
#   1. CC-Writer eingerichtet (setup-cc-writer.ps1) - der Kiosk-Writer nutzt dessen SQL-Login und Bediener-Liste
#   2. grant-kiosk-writer.sql als SQL-Admin ausgefuehrt, Datenbank gesichert
#   3. In der ACMP-Konsole einen Kiosk-Ordner zum Testen angelegt (der MCP schreibt nur in freigegebene Ordner)
#
# Das Skript prueft Rechte und Ordner, BEVOR es config.local.json aendert, legt eine Sicherung der Config an und
# startet den Dienst neu.
param(
  [string[]]$AllowedGroupIds,
  [string]$ImageRepositoryDir,
  [string[]]$ImageSourceRoots,
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

Write-Title "ACMP-MCP-Server - Kiosk-Writer (Kiosk-Eintraege und -Ordner pflegen)"
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
  if ($cfg.kioskWriter) { Set-CfgProp $cfg.kioskWriter "enabled" $false; Save-WithBackup $cfg; Restart-IfWanted }
  Write-Ok "Kiosk-Writer ist aus. Lesen (acmp_kiosk_tree, acmp_kiosk_images) bleibt mit den DB-Report-Tools verfuegbar."
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
Write-Info "Jede Kiosk-Aenderung zeigt mit dryRun vorher eine Vorschau, wird gesichert (acmp_kiosk_restore) und steht im Aktivitaetsprotokoll."
Write-Info ("Bediener mit Schreibrecht (aus ccWriter.allowedRequesters): " + ($req -join ", "))
if (-not (Read-YesNo "Weiter?" $false)) { Write-Info "Abgebrochen - nichts geaendert."; exit 0 }

$c = New-Object System.Data.SqlClient.SqlConnection $writeCs
try { $c.Open() } catch { Write-Bad "Anmeldung mit dem Schreib-Login fehlgeschlagen: $($_.Exception.Message)"; Write-Info "Nichts geaendert."; exit 1 }
function Get-Scalar([string]$sql, [hashtable]$p = @{}) {
  $q = $c.CreateCommand(); $q.CommandText = $sql
  foreach ($k in $p.Keys) { [void]$q.Parameters.AddWithValue($k, $p[$k]) }
  $q.ExecuteScalar()
}
function Test-Perms([string[]]$list) { @($list | Where-Object { $t, $p = $_ -split ':'; (Get-Scalar "SELECT HAS_PERMS_BY_NAME(@o,'OBJECT',@p)" @{ '@o' = "dbo.$t"; '@p' = $p }) -ne 1 }) }
$need = @("SYS_Kiosk_Items:SELECT","SYS_Kiosk_Items:INSERT","SYS_Kiosk_Items:UPDATE","SYS_Kiosk_Items:DELETE",
          "SYS_Kiosk_Filters:SELECT","SYS_Kiosk_Filters:INSERT","SYS_Kiosk_Filters:DELETE",
          "SYS_Kiosk_Filters_ActiveDirectoryUserPropertyNames:SELECT","SYS_Kiosk_Filters_ActiveDirectoryUserPropertyNames:INSERT",
          "SYS_Kiosk_Filters_ActiveDirectoryComputerPropertyNames:SELECT","SYS_Kiosk_Filters_ActiveDirectoryComputerPropertyNames:INSERT",
          "SYS_Kiosk_Groups:SELECT","SYS_Kiosk_Groups:INSERT","SYS_Kiosk_Groups:UPDATE",
          "SYS_MultiLanguageItems:SELECT","SYS_MultiLanguageItems:INSERT","SYS_MultiLanguageItems:UPDATE",
          "SYS_MultiLanguageItems_Memo:SELECT","SYS_MultiLanguageItems_Memo:INSERT","SYS_MultiLanguageItems_Memo:UPDATE")
$imgNeed = @("SYS_CustomImages:SELECT","SYS_CustomImages:INSERT","SYS_CustomImages:DELETE")
$ok = $true
$groups = @()
try {
  $miss = Test-Perms $need
  if ($miss) { Write-Bad ("Rechte fehlen: " + ($miss -join ", ") + " -> grant-kiosk-writer.sql Abschnitt 3 ausfuehren"); $ok = $false } else { Write-Ok "Kiosk-Rechte des Schreib-Logins vollstaendig" }
  if ((Get-Scalar "SELECT HAS_PERMS_BY_NAME('dbo.SYS_Kiosk_Groups','OBJECT','DELETE')") -eq 1) { Write-Hint "Der Login darf Kiosk-Ordner LOESCHEN (loescht per CASCADE alle Eintraege) - das braucht er nicht. Empfohlen: Recht entziehen." }
  $imgMiss = Test-Perms $imgNeed
  if (-not $miss) {
    $q = $c.CreateCommand()
    $q.CommandText = "SELECT CAST(g.Id AS varchar(36)), ISNULL(m.Caption_de, N''), (SELECT COUNT(*) FROM SYS_Kiosk_Items i WHERE i.GroupId = g.Id) " +
                     "FROM SYS_Kiosk_Groups g LEFT JOIN SYS_MultiLanguageItems m ON m.Id = g.Id ORDER BY 2"
    $r = $q.ExecuteReader()
    while ($r.Read()) { $groups += [pscustomobject]@{ Id = Format-Guid $r.GetString(0); Name = $r.GetString(1); Items = [int]$r.GetValue(2) } }
    $r.Close()
  }
} finally { $c.Close() }
if (-not $ok) { Write-Info "Nichts geaendert. Punkte oben beheben und erneut starten."; exit 1 }

# ---------------------------------------------------------------- Ordner
Write-Info ""
Write-Info "Der MCP schreibt nur in Kiosk-Ordner, die hier freigegeben sind, und in Ordner, die er selbst anlegt."
Write-Info "Empfohlen fuer den Anfang: ein eigener Test-Ordner. Vorhandene Ordner:"
for ($i = 0; $i -lt $groups.Count; $i++) { Write-Info ("  {0,2}) {1,-40} {2,3} Eintraege  {3}" -f ($i + 1), $groups[$i].Name, $groups[$i].Items, $groups[$i].Id) }
$prev = if ($cfg.kioskWriter -and $cfg.kioskWriter.allowedGroupIds) { @($cfg.kioskWriter.allowedGroupIds) } else { @() }
if (-not $AllowedGroupIds) {
  $v = Read-Value "Freigegebene Ordner (Nummern oder GUIDs, Komma)" ($prev -join ", ") '.+'
  $AllowedGroupIds = @($v -split '\s*,\s*' | Where-Object { $_ } | ForEach-Object {
    if ($_ -match '^\d+$' -and [int]$_ -ge 1 -and [int]$_ -le $groups.Count) { $groups[[int]$_ - 1].Id } else { $_ } })
}
$sel = @()
foreach ($g in (Split-ListArg $AllowedGroupIds)) {
  try { $id = Format-Guid $g } catch { Write-Bad "Keine GUID: $g"; exit 1 }
  $hit = $groups | Where-Object { $_.Id -eq $id }
  if (-not $hit) { Write-Bad "Kiosk-Ordner $id nicht gefunden - nichts geaendert."; exit 1 }
  Write-Ok ("Ordner '{0}' ({1} Eintraege)" -f $hit.Name, $hit.Items)
  $sel += $id
}
if (-not $sel) { Write-Bad "Kein Ordner gewaehlt - nichts geaendert."; exit 1 }

# ---------------------------------------------------------------- Bilder (optional)
Write-Info ""
Write-Info "Optional: Icons hochladen (acmp_kiosk_add_image). Dafuer braucht es den Ordner 'Images' im File Repository"
Write-Info "dieses ACMP-Servers und UNC-Freigaben, aus denen der Dienst (Computerkonto) Bilddateien lesen darf."
$prevDir = if ($cfg.kioskWriter -and $cfg.kioskWriter.imageRepositoryDir) { "$($cfg.kioskWriter.imageRepositoryDir)" } else { "" }
if (-not $prevDir -and $cfg.ccWriter.fileRepositoryRoot -and "$($cfg.ccWriter.fileRepositoryRoot)" -match '^(.*)\\ClientCommands\\Projects\\?$') { $prevDir = $Matches[1] + "\Images" }
$prevRoots = if ($cfg.kioskWriter -and $cfg.kioskWriter.imageSourceRoots) { @($cfg.kioskWriter.imageSourceRoots) } else { @() }
if (-not $PSBoundParameters.ContainsKey('ImageRepositoryDir')) {
  $ImageRepositoryDir = Read-Value "Ordner Images im File Repository (leer = keine Bild-Tools)" $prevDir
}
if ($ImageRepositoryDir) {
  if (-not (Test-Path $ImageRepositoryDir)) { Write-Hint "Ordner nicht gefunden: $ImageRepositoryDir - die Bild-Tools bleiben gesperrt, bis der Pfad stimmt." }
  if (-not $PSBoundParameters.ContainsKey('ImageSourceRoots')) {
    $v = Read-Value "Erlaubte UNC-Ordner mit Bilddateien (z.B. \\server\software\icons, Komma)" ($prevRoots -join ", ") '^(\\\\[^,]+(\s*,\s*\\\\[^,]+)*)?$' "Nur UNC-Pfade, die mit \\ beginnen."
    $ImageSourceRoots = if ($v) { $v -split '\s*,\s*' } else { @() }
  }
  if ($imgMiss) { Write-Hint ("Bild-Rechte fehlen: " + ($imgMiss -join ", ") + " - Bild-Tools bleiben gesperrt (grant-kiosk-writer.sql Abschnitt 4)") } else { Write-Ok "Bild-Rechte vorhanden" }
}
$ImageSourceRoots = @(Split-ListArg $ImageSourceRoots)

# ---------------------------------------------------------------- Config schreiben
if (-not $cfg.kioskWriter) { $cfg | Add-Member -NotePropertyName kioskWriter -NotePropertyValue ([pscustomobject]@{}) }
$k = $cfg.kioskWriter
Set-CfgProp $k "enabled" $true
Set-CfgProp $k "allowedGroupIds" $sel
if ($k.PSObject.Properties["approvalGate"]) { $k.PSObject.Properties.Remove("approvalGate") }
if (-not $k.PSObject.Properties["allowUntested"]) { Set-CfgProp $k "allowUntested" $false }
Set-CfgProp $k "imageRepositoryDir" $(if ($ImageRepositoryDir) { $ImageRepositoryDir } else { "" })
Set-CfgProp $k "imageSourceRoots" $ImageSourceRoots
if (-not $k.PSObject.Properties["backupDir"]) { Set-CfgProp $k "backupDir" "kiosk-backups" }
Save-WithBackup $cfg
Restart-IfWanted

Write-Host ""
Write-Ok "Kiosk-Writer eingerichtet."
Write-Info "Pruefen: im MCP-Client 'acmp_kiosk_tree' mit status=true aufrufen - dort muss die Vorpruefung bestanden sein."
Write-Info "Danach z.B.: 'Lege im Kiosk-Ordner <Name> einen Eintrag fuer den Command <Name> an, sichtbar nur fuer die OU ...'"
Write-Info "Aenderungen sieht der Anwender nach 'Aktualisieren' im Kiosk. Zurueck: acmp_kiosk_restore."
Write-Info "Ausschalten: .\setup-kiosk-writer.ps1 -Disable"
