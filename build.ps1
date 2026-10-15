# Baut AcmpMcpServer.exe und acmp-token-setup.exe mit dem csc.exe aus dem .NET Framework 4.x.
# Kein .NET-SDK und kein Visual Studio noetig. Muss auf dem ACMP-Server laufen, weil die
# Aagon-DLLs (AESB SICS) dort liegen.
#
#   .\build.ps1                              -> baut nach .\out\
#   .\build.ps1 -OutDir C:\ProgramData\AcmpMcp -ServerExeName AcmpMcpServer.new.exe
param(
  [string]$OutDir        = (Join-Path $PSScriptRoot "out"),
  [string]$SicsDir       = "C:\Program Files (x86)\Aagon\AESB SICS\SICS",
  [string]$ServerExeName = "AcmpMcpServer.exe"
)
$ErrorActionPreference = 'Stop'
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$ns  = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\netstandard.dll"
$src = Join-Path $PSScriptRoot "src"

foreach ($p in @($csc, $ns)) { if (-not (Test-Path $p)) { throw "Nicht gefunden: $p (.NET Framework 4.8 installiert?)" } }
$aagonDlls = @("Newtonsoft.Json.dll","Aagon.Sis.Sics.Clients.dll","Aagon.Sis.Sics.Icql.dll","Aagon.Sis.Sics.Interfaces.dll","Aagon.Logging.dll")
foreach ($d in $aagonDlls) { if (-not (Test-Path (Join-Path $SicsDir $d))) { throw "Aagon-DLL fehlt: $SicsDir\$d  (AESB SICS installiert? Sonst -SicsDir angeben)" } }
New-Item -ItemType Directory -Force $OutDir | Out-Null

$refs = @("/r:System.dll","/r:System.Core.dll","/r:System.Xml.dll","/r:System.Data.dll","/r:System.Security.dll","/r:System.DirectoryServices.dll","/r:System.Drawing.dll","/r:$ns")
$refs += $aagonDlls | ForEach-Object { "/r:" + (Join-Path $SicsDir $_) }

$serverOut = Join-Path $OutDir $ServerExeName
Write-Host "Baue $serverOut ..." -ForegroundColor Cyan
& $csc /nologo /platform:x64 /codepage:65001 /target:exe "/out:$serverOut" @refs `
  (Join-Path $src "AcmpMcpServer.cs") (Join-Path $src "AcmpConn.cs") (Join-Path $src "AcmpDb.cs") (Join-Path $src "AcmpApproval.cs") `
  (Join-Path $src "AcmpCcFormat.cs") (Join-Path $src "AcmpCcWriter.cs") (Join-Path $src "AcmpCcPreflight.cs") (Join-Path $src "AcmpCtl.cs") `
  (Join-Path $src "AcmpCcFolders.cs") (Join-Path $src "AcmpCcRelease.cs") (Join-Path $src "AcmpCcVersion.cs") `
  (Join-Path $src "AcmpKiosk.cs") (Join-Path $src "AcmpKioskImages.cs") (Join-Path $src "AcmpKioskUpdate.cs") (Join-Path $src "AcmpKioskGroup.cs")
if ($LASTEXITCODE -ne 0) { throw "Build AcmpMcpServer fehlgeschlagen (exit $LASTEXITCODE)" }

$tokenOut = Join-Path $OutDir "acmp-token-setup.exe"
Write-Host "Baue $tokenOut ..." -ForegroundColor Cyan
& $csc /nologo /platform:x64 /codepage:65001 /target:exe "/out:$tokenOut" @refs `
  (Join-Path $src "acmp-token-setup.cs") (Join-Path $src "AcmpConn.cs")
if ($LASTEXITCODE -ne 0) { throw "Build acmp-token-setup fehlgeschlagen (exit $LASTEXITCODE)" }

Write-Host "BUILD OK -> $OutDir" -ForegroundColor Green
