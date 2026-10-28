# SPDX-License-Identifier: MIT
# Builds a fake stage folder for compiling and smoke-testing the installer without the real binaries.
# Every executable is the same small stub (installer/ci/stub.cs); see installer/README.md for the real layout.
#
#   pwsh installer/ci/New-FakeStage.ps1 -Stage build/stage -WebDist web/dist
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Stage,
    # Built web UI (web/dist). If missing, a placeholder index.html is written.
    [string] $WebDist = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }

if (Test-Path $Stage) { Remove-Item -Recurse -Force $Stage }
foreach ($d in 'service\wwwroot', 'tray', 'native', 'worker', 'drivers') {
    New-Item -ItemType Directory -Force -Path (Join-Path $Stage $d) | Out-Null
}
$Stage = (Resolve-Path $Stage).Path

$stub = Join-Path $Stage 'stub.exe'
& $csc /nologo /target:exe /optimize+ "/out:$stub" /reference:System.ServiceProcess.dll (Join-Path $PSScriptRoot 'stub.cs')
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }

$exes = @(
    'service\SplitPlay.Service.exe',
    'service\splitplay.exe',
    'tray\SplitPlay.Tray.exe',
    'native\splitplay-seat.exe',
    'native\splitplay-stubworker.exe',
    'worker\splitplay-worker.exe'
)
foreach ($e in $exes) { Copy-Item $stub (Join-Path $Stage $e) }
# Not a real DLL: the installer only copies it.
Copy-Item $stub (Join-Path $Stage 'native\splitplay-rdsprov.dll')
Remove-Item $stub

Set-Content -Encoding utf8 -Path (Join-Path $Stage 'worker\COPYING') -Value @'
Placeholder for the GNU General Public License v3 text (CI fake stage).
The real stage copies worker/COPYING from the repository.
'@
Set-Content -Encoding utf8 -Path (Join-Path $Stage 'worker\README-source.txt') -Value @'
Placeholder source offer (CI fake stage). The real worker stage contains the written offer and the
link to the exact Sunshine commit and patch queue used for this build.
'@
Set-Content -Encoding utf8 -Path (Join-Path $Stage 'drivers\README.txt') -Value 'Driver packages are not part of the Stage 2 installer.'

$www = Join-Path $Stage 'service\wwwroot'
if ($WebDist -and (Test-Path (Join-Path $WebDist 'index.html'))) {
    Copy-Item -Recurse -Path (Join-Path $WebDist '*') -Destination $www
} else {
    Write-Warning "No web build at '$WebDist'; writing a placeholder index.html"
    Set-Content -Encoding utf8 -Path (Join-Path $www 'index.html') -Value '<!doctype html><title>SplitPlay</title><p>placeholder'
}

Get-ChildItem -Recurse -File $Stage | ForEach-Object { $_.FullName.Substring($Stage.Length + 1) } | Sort-Object
