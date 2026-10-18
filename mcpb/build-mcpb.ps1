<#
.SYNOPSIS
  Baut die Claude-Desktop-Erweiterung dist\acmp-mcp-<version>.mcpb (MCP Bundle) fuer Claude Desktop
  und die MCP Registry.

.DESCRIPTION
  Die Erweiterung ist nur die Bruecke von Claude Desktop zum ACMP-MCP-Server (mcpb\server\index.js,
  ohne Abhaengigkeiten). Der Server selbst laeuft weiter auf dem ACMP-Server (install.ps1).

  Ein .mcpb ist ein ZIP mit manifest.json an der Wurzel. Das Skript prueft vorher:
  Version in mcpb\manifest.json = VERSION in mcpb\server\index.js = Eintrag in mcpb\CHANGELOG.md,
  nur ASCII in .js/.ps1, alle Dateien vorhanden.

  Das Paket ist reproduzierbar: feste Reihenfolge, feste Zeitstempel (Datum aus dem CHANGELOG),
  Zeilenenden immer CRLF. Derselbe Stand ergibt also dieselbe SHA-256. Die traegt das Skript mit
  Version und Download-URL in server.json ein (Eintrag fuer die MCP Registry).

  Fuer Releases Windows PowerShell 5.1 nehmen: PowerShell 7 komprimiert anders, die Pruefsumme
  waere eine andere. Hochgeladen wird genau die Datei, deren SHA-256 in server.json steht.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File mcpb\build-mcpb.ps1
#>
[CmdletBinding()]
param(
  # Zielordner, Standard dist\ im Repo (von Git ignoriert).
  [string] $OutDir,
  # server.json nicht anfassen.
  [switch] $OhneServerJson
)

$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSEdition -ne "Desktop") { Write-Warning "PowerShell $($PSVersionTable.PSVersion): andere Pruefsumme als mit Windows PowerShell 5.1" }
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $OutDir) { $OutDir = Join-Path $root "dist" }
$utf8 = New-Object System.Text.UTF8Encoding $false
function Text([string] $rel) { [System.IO.File]::ReadAllText((Join-Path $root $rel), $utf8) }

# ---------- Version ----------
$manifest = (Text "mcpb\manifest.json") | ConvertFrom-Json
$version = [string] $manifest.version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Version in mcpb\manifest.json ungueltig: '$version'" }

$m = [regex]::Match((Text "mcpb\server\index.js"), 'const VERSION\s*=\s*"([^"]+)"')
if (-not $m.Success -or $m.Groups[1].Value -ne $version) {
  throw "VERSION in mcpb\server\index.js ($($m.Groups[1].Value)) passt nicht zu manifest.json ($version)"
}
$m = [regex]::Match((Text "mcpb\CHANGELOG.md"), '(?m)^## ' + [regex]::Escape($version) + '\s+\S+\s+(\d{4})-(\d{2})-(\d{2})')
if (-not $m.Success) { throw "mcpb\CHANGELOG.md hat keinen Eintrag '## $version - JJJJ-MM-TT'" }
$zeit = New-Object DateTimeOffset ([int] $m.Groups[1].Value), ([int] $m.Groups[2].Value), ([int] $m.Groups[3].Value), 12, 0, 0, ([TimeSpan]::Zero)

# ---------- Inhalt ----------
# Pfad im Paket = Quelle im Repo. Reihenfolge fest.
$inhalt = [ordered]@{
  "manifest.json"   = "mcpb\manifest.json"
  "icon.png"        = "mcpb\icon.png"
  "server/index.js" = "mcpb\server\index.js"
  "CHANGELOG.md"    = "mcpb\CHANGELOG.md"
  "LICENSE"         = "LICENSE"
  "README.md"       = "README.md"
}

foreach ($ziel in $inhalt.Keys) {
  $quelle = Join-Path $root $inhalt[$ziel]
  if (-not (Test-Path $quelle)) { throw "Fehlt: $($inhalt[$ziel])" }
  if ($ziel -match '\.(js|ps1)$') {
    $b = [System.IO.File]::ReadAllBytes($quelle)
    for ($i = 0; $i -lt $b.Length; $i++) {
      if ($b[$i] -gt 127) { throw "Nicht-ASCII in $($inhalt[$ziel]) (Byte $i)" }
    }
  }
}
if (-not $inhalt.Contains([string] $manifest.server.entry_point)) { throw "entry_point '$($manifest.server.entry_point)' ist nicht im Paket" }
if ($manifest.icon -and -not $inhalt.Contains([string] $manifest.icon)) { throw "icon '$($manifest.icon)' ist nicht im Paket" }

# ---------- Paket ----------
Add-Type -AssemblyName System.IO.Compression
$name = "acmp-mcp-$version.mcpb"
$ziel = Join-Path $OutDir $name
[void](New-Item -ItemType Directory -Force $OutDir)
if (Test-Path $ziel) { Remove-Item $ziel -Force }

$fs = [System.IO.File]::Open($ziel, [System.IO.FileMode]::CreateNew)
try {
  $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
  try {
    foreach ($eintrag in $inhalt.Keys) {
      $daten = [System.IO.File]::ReadAllBytes((Join-Path $root $inhalt[$eintrag]))
      if ($eintrag -notmatch '\.png$') {
        # Zeilenenden je nach Checkout (autocrlf) verschieden; im Paket immer CRLF, sonst wechselt die Pruefsumme.
        $t = $utf8.GetString($daten).Replace("`r`n", "`n").Replace("`n", "`r`n")
        $daten = $utf8.GetBytes($t)
      }
      $e = $zip.CreateEntry($eintrag, [System.IO.Compression.CompressionLevel]::Optimal)
      $e.LastWriteTime = $zeit
      $w = $e.Open()
      try { $w.Write($daten, 0, $daten.Length) } finally { $w.Dispose() }
    }
  } finally { $zip.Dispose() }
} finally { $fs.Dispose() }

$sha = (Get-FileHash -Algorithm SHA256 $ziel).Hash.ToLowerInvariant()
$groesse = (Get-Item $ziel).Length
$url = "https://github.com/Herzog-GmbH/acmp-mcp/releases/download/mcpb-v$version/$name"

Write-Host "Paket:   $ziel ($([Math]::Round($groesse / 1KB)) KB, $($inhalt.Count) Dateien)"
Write-Host "SHA-256: $sha"
Write-Host "URL:     $url"

# ---------- server.json ----------
$sj = Join-Path $root "server.json"
if (-not $OhneServerJson -and (Test-Path $sj)) {
  $t = [System.IO.File]::ReadAllText($sj, $utf8)
  $neu = ([regex] '"version":\s*"[^"]*"').Replace($t, '"version": "' + $version + '"', 1)
  $neu = ([regex] '"identifier":\s*"[^"]*"').Replace($neu, '"identifier": "' + $url + '"', 1)
  $neu = ([regex] '"fileSha256":\s*"[^"]*"').Replace($neu, '"fileSha256": "' + $sha + '"', 1)
  if ($neu -ne $t) { [System.IO.File]::WriteAllText($sj, $neu, $utf8); Write-Host "server.json nachgetragen (Version, URL, SHA-256)" }
  else { Write-Host "server.json passt schon" }
}
