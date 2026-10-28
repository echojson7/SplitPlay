# SPDX-License-Identifier: MIT
<#
.SYNOPSIS
  Builds the SplitPlay driver packages (SplitPlayIdd, SplitPlayPad) with the WDK from NuGet, validates them and
  collects them into an output folder. Nothing is installed on the machine.

.DESCRIPTION
  1. nuget restore drivers\packages.config into drivers\packages (WDK + SDK NuGet packages)
  2. msbuild SplitPlay.Drivers.sln  (StampInf -> InfVerif task -> link -> package -> Inf2Cat, per driver)
  3. SplitPlay.Drivers.Tests.exe     (host-side smoke tests, incl. INF checks)
  4. infverif.exe /w /v on the stamped INFs (Windows Driver rules, explicit and visible in the log)
  5. optional test signing of .dll and .cat with a throwaway self-signed certificate, which is removed from the
     certificate store again; only its public part (.cer) is kept next to the packages
  6. copies <name>.inf/.dll/.cat (+ .pdb) into -OutDir\<name>\

.EXAMPLE
  pwsh drivers\scripts\Build-Drivers.ps1 -TestSign
#>
[CmdletBinding()]
param(
  [string] $Configuration = 'Release',
  [string] $Platform = 'x64',
  # Four-part version stamped into DriverVer, e.g. 0.1.0.42.
  [string] $DriverVersion = '0.1.0.0',
  [string] $OutDir,
  [switch] $TestSign,
  [switch] $SkipRestore,
  # Allow the build to fall back to an installed WDK when the NuGet packages are absent.
  [switch] $AllowInstalledWdk
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$driversDir = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $driversDir 'out' }
$wdkVersion = ([xml](Get-Content (Join-Path $driversDir 'packages.config'))).packages.package |
  Where-Object { $_.id -eq 'Microsoft.Windows.WDK.x64' } | Select-Object -ExpandProperty version
$packagesDir = Join-Path $driversDir 'packages'
$wdkRoot = Join-Path $packagesDir "Microsoft.Windows.WDK.x64.$wdkVersion\c"
$sdkRoot = Join-Path $packagesDir "Microsoft.Windows.SDK.CPP.$wdkVersion\c"
$kit = '10.0.26100.0'

function Invoke-Native([string] $exe, [string[]] $arguments) {
  Write-Host ">> $exe $($arguments -join ' ')"
  & $exe @arguments
  if ($LASTEXITCODE -ne 0) { throw "$exe failed with exit code $LASTEXITCODE" }
}

function Find-Tool([string] $name, [string[]] $candidates) {
  foreach ($c in $candidates) { if ($c -and (Test-Path -LiteralPath $c)) { return (Resolve-Path -LiteralPath $c).Path } }
  $kits = 'C:\Program Files (x86)\Windows Kits\10'
  $found = Get-ChildItem -Path $kits -Recurse -Filter $name -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\|\\x86\\' } | Sort-Object FullName -Descending | Select-Object -First 1
  if ($found) { return $found.FullName }
  throw "$name not found"
}

# ---- 1. restore -----------------------------------------------------------------------------------------------
if (-not $SkipRestore) {
  $nuget = (Get-Command nuget.exe -ErrorAction SilentlyContinue).Source
  if (-not $nuget) { throw 'nuget.exe is not on PATH' }
  Invoke-Native $nuget @('restore', (Join-Path $driversDir 'packages.config'), '-PackagesDirectory', $packagesDir,
                         '-ConfigFile', (Join-Path $driversDir 'nuget.config'), '-NonInteractive')
}
$haveNuGetWdk = Test-Path -LiteralPath $wdkRoot
Write-Host "WDK NuGet package: $wdkRoot (present: $haveNuGetWdk)"

# ---- 2. build ---------------------------------------------------------------------------------------------------
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
if (-not $vs) { throw 'Visual Studio with MSBuild not found' }
$msbuild = Join-Path $vs.Trim() 'MSBuild\Current\Bin\amd64\MSBuild.exe'
Write-Host "MSBuild: $msbuild"
& $vswhere -latest -products * -requires Microsoft.Component.MSBuild -include packages -format json |
  ConvertFrom-Json | ForEach-Object { $_.packages } |
  Where-Object { $_.id -match 'DriverKit' } | ForEach-Object { Write-Host "VS WDK component: $($_.id) $($_.version)" }

$logDir = Join-Path $driversDir 'build\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$requireNuGet = if ($AllowInstalledWdk) { 'false' } else { 'true' }
Invoke-Native $msbuild @(
  (Join-Path $driversDir 'SplitPlay.Drivers.sln'), '/t:Rebuild', '/m', '/nologo', '/v:minimal',
  "/p:Configuration=$Configuration", "/p:Platform=$Platform",
  "/p:SplitPlayDriverVersion=$DriverVersion", "/p:SplitPlayRequireNuGetWdk=$requireNuGet",
  "/bl:$(Join-Path $logDir 'drivers.binlog')"
)

$binDir = Join-Path $driversDir "build\$Platform\$Configuration"
$drivers = @('SplitPlayIdd', 'SplitPlayPad')

# ---- 3. smoke tests ---------------------------------------------------------------------------------------------
Invoke-Native (Join-Path $binDir 'SplitPlay.Drivers.Tests.exe') @($driversDir)

# ---- 4. InfVerif ------------------------------------------------------------------------------------------------
$infVerif = Find-Tool 'infverif.exe' @((Join-Path $wdkRoot "tools\$kit\x64\infverif.exe"))
foreach ($d in $drivers) {
  $inf = Join-Path $binDir "$d\$d.inf"     # stamped copy in the WDK package directory
  if (-not (Test-Path -LiteralPath $inf)) { throw "Packaged INF missing: $inf" }
  Get-Content -LiteralPath $inf | Select-String -Pattern '^\s*DriverVer|NTamd64' | ForEach-Object { Write-Host "  $d.inf: $($_.Line.Trim())" }
  Invoke-Native $infVerif @('/w', '/v', $inf)
}

# ---- 5. catalogs (+ optional test signing) ---------------------------------------------------------------------
$inf2Cat = Find-Tool 'Inf2Cat.exe' @((Join-Path $wdkRoot "bin\$kit\x86\Inf2Cat.exe"))
foreach ($d in $drivers) {
  $pkg = Join-Path $binDir $d
  if (-not (Test-Path -LiteralPath (Join-Path $pkg "$d.cat"))) {
    # The WDK packaging step normally runs Inf2Cat already; create the catalog if it did not.
    Invoke-Native $inf2Cat @("/driver:$pkg", '/os:10_GE_X64', '/uselocaltime')
  }
}

$cerPath = $null
if ($TestSign) {
  $signTool = Find-Tool 'signtool.exe' @((Join-Path $sdkRoot "bin\$kit\x64\signtool.exe"))
  $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=SplitPlay Throwaway Test Signing (CI)' `
    -CertStoreLocation 'Cert:\CurrentUser\My' -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddDays(30)
  try {
    foreach ($d in $drivers) {
      $pkg = Join-Path $binDir $d
      Invoke-Native $signTool @('sign', '/fd', 'SHA256', '/sha1', $cert.Thumbprint, '/s', 'My', (Join-Path $pkg "$d.dll"))
      Invoke-Native $signTool @('sign', '/fd', 'SHA256', '/sha1', $cert.Thumbprint, '/s', 'My', (Join-Path $pkg "$d.cat"))
    }
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $cerPath = Join-Path $OutDir 'SplitPlayTestSigning.cer'
    Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT | Out-Null
  }
  finally {
    # Throwaway: the private key never leaves this run and nothing stays in the store.
    Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($cert.Thumbprint)" -DeleteKey -ErrorAction SilentlyContinue
  }
}

# ---- 6. collect -------------------------------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
foreach ($d in $drivers) {
  $pkg = Join-Path $binDir $d
  $dest = Join-Path $OutDir $d
  New-Item -ItemType Directory -Force -Path $dest | Out-Null
  foreach ($ext in 'inf', 'dll', 'cat') {
    $file = Join-Path $pkg "$d.$ext"
    if (-not (Test-Path -LiteralPath $file)) { throw "Driver package file missing: $file" }
    Copy-Item -LiteralPath $file -Destination $dest
  }
  $pdb = Join-Path $binDir "$d.pdb"
  if (Test-Path -LiteralPath $pdb) { Copy-Item -LiteralPath $pdb -Destination $dest }
  if ($TestSign) {
    Get-AuthenticodeSignature -FilePath (Join-Path $dest "$d.cat") |
      ForEach-Object { Write-Host "  $d.cat signer: $($_.SignerCertificate.Subject) status: $($_.Status)" }
  }
}
@"
SplitPlay driver packages $DriverVersion ($Configuration|$Platform)
WDK: $(if ($haveNuGetWdk) { "NuGet Microsoft.Windows.WDK.x64 $wdkVersion" } else { 'installed WDK' })
Signing: $(if ($TestSign) { 'throwaway self-signed test certificate (SplitPlayTestSigning.cer); NOT for distribution' } else { 'unsigned' })
Install for development only, on a test-signing machine: pnputil /add-driver <name>.inf /install
"@ | Set-Content -LiteralPath (Join-Path $OutDir 'README.txt') -Encoding utf8
Get-ChildItem -Recurse -File $OutDir | ForEach-Object { Write-Host ("{0,10}  {1}" -f $_.Length, $_.FullName.Substring($OutDir.Length + 1)) }
