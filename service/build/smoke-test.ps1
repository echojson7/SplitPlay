# SPDX-License-Identifier: MIT
<#
.SYNOPSIS
  Smoke test for the published service, CLI and tray (run elevated on Windows; used by CI).

.DESCRIPTION
  1. Starts SplitPlay.Service.exe in console mode with the "fake-child" stand-in as desk child, checks
     GET /api/v1/health and drives a desk through create / start / stop / export / import / delete via splitplay.exe.
  2. Installs the Windows service with "splitplay service install", checks the SCM configuration
     (delayed auto start, restart on failure), starts it, queries health, stops and uninstalls it.
  3. Runs SplitPlay.Tray.exe --smoke-test.

.PARAMETER Publish
  Folder with service\, cli\ and tray\ (the service-win-x64 artifact).
#>
param(
    [Parameter(Mandatory = $true)][string]$Publish,
    [string]$Work = (Join-Path ([IO.Path]::GetTempPath()) "splitplay-smoke"),
    [int]$Port = 38299
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Publish = (Resolve-Path $Publish).Path
$svc = Join-Path $Publish 'service\SplitPlay.Service.exe'
$cli = Join-Path $Publish 'cli\splitplay.exe'
$tray = Join-Path $Publish 'tray\SplitPlay.Tray.exe'
$base = "http://127.0.0.1:$Port/api/v1"
foreach ($f in @($svc, $cli, $tray)) { if (-not (Test-Path $f)) { throw "missing $f" } }

if (Test-Path $Work) { Remove-Item $Work -Recurse -Force }
New-Item -ItemType Directory -Path $Work | Out-Null

function Step([string]$text) { Write-Host "`n=== $text" -ForegroundColor Cyan }

function Invoke-Cli {
    Write-Host "> splitplay $($args -join ' ')"
    & $cli @args
    if ($LASTEXITCODE -ne 0) { throw "splitplay $($args -join ' ') exited with $LASTEXITCODE" }
}

function Wait-Health([int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ($true) {
        try {
            $h = Invoke-RestMethod -Uri "$base/health" -TimeoutSec 5
            if ($h.status -eq 'ok') { return $h }
        } catch {
            if ((Get-Date) -gt $deadline) { throw "health endpoint did not answer within $Seconds s: $_" }
        }
        Start-Sleep -Milliseconds 500
    }
}

function Wait-PortFree([int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) {
        if ((Get-Date) -gt $deadline) { throw "port $Port is still in use" }
        Start-Sleep -Milliseconds 250
    }
}

# --------------------------------------------------------------------------- console mode
Step 'console mode: start'
$data = Join-Path $Work 'console-data'
$serviceArgs = @(
    '--console',
    '--data-root', "`"$data`"",
    '--Supervisor:Launcher=process',
    "`"--Supervisor:ChildExecutable=$svc`"",
    '--Supervisor:ChildArguments=fake-child'
)
$proc = Start-Process -FilePath $svc -ArgumentList $serviceArgs -PassThru -NoNewWindow `
    -RedirectStandardOutput (Join-Path $Work 'console.out.log') -RedirectStandardError (Join-Path $Work 'console.err.log')
try {
    $health = Wait-Health 60
    Write-Host "health: $($health | ConvertTo-Json -Compress)"
    if ($health.version -ne '0.1.0') { throw "unexpected version $($health.version)" }

    Step 'console mode: desk lifecycle via CLI'
    Invoke-Cli desk create smoke-1 --name "Smoke test" --fps 60 --codec hevc
    Invoke-Cli desk list
    Invoke-Cli desk start smoke-1 --wait --timeout 60
    $desk = Invoke-RestMethod -Uri "$base/desks/smoke-1"
    if ($desk.state -ne 'Ready') { throw "desk state is $($desk.state), expected Ready ($($desk.lastError))" }
    Invoke-Cli desk show smoke-1 --json
    Invoke-Cli desk stop smoke-1 --wait --timeout 30
    $desk = Invoke-RestMethod -Uri "$base/desks/smoke-1"
    if ($desk.state -ne 'Stopped') { throw "desk state is $($desk.state), expected Stopped" }

    Step 'console mode: config export / import'
    $zip = Join-Path $Work 'config.zip'
    Invoke-Cli config export $zip
    Invoke-Cli desk delete smoke-1
    Invoke-Cli config import $zip
    $desks = @(Invoke-RestMethod -Uri "$base/desks")
    if ($desks.Count -ne 1 -or $desks[0].id -ne 'smoke-1') { throw "import did not restore smoke-1" }
    Invoke-Cli desk delete smoke-1

    $metrics = Invoke-RestMethod -Uri "$base/metrics"
    Write-Host "metrics: $($metrics | ConvertTo-Json -Compress -Depth 5)"
    $index = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/" -UseBasicParsing
    if ($index.Content -notmatch '<title>SplitPlay</title>') { throw 'placeholder index.html not served' }
} finally {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    $proc.WaitForExit(10000) | Out-Null
    Get-Content (Join-Path $Work 'console.out.log') -ErrorAction SilentlyContinue | Select-Object -Last 40
}
Wait-PortFree 30

# --------------------------------------------------------------------------- Windows service
Step 'service: install'
Invoke-Cli service install
try {
    $qc = (& sc.exe qc SplitPlay) -join "`n"
    Write-Host $qc
    if ($qc -notmatch 'AUTO_START\s+\(DELAYED\)') { throw 'service is not delayed auto start' }
    if ($qc -notmatch 'SplitPlay Host Service') { throw 'unexpected display name' }
    if ($qc -notmatch 'LocalSystem') { throw 'service does not run as LocalSystem' }
    $qf = (& sc.exe qfailure SplitPlay) -join "`n"
    Write-Host $qf
    if ($qf -notmatch 'RESTART') { throw 'no restart recovery action' }

    # Installing again updates the existing registration.
    Invoke-Cli service install

    Step 'service: start and health'
    Invoke-Cli service start --timeout 90
    $health = Wait-Health 60
    Write-Host "health: $($health | ConvertTo-Json -Compress)"
    Invoke-Cli service status

    Step 'service: stop'
    Invoke-Cli service stop --timeout 60
    Invoke-Cli service status
} finally {
    Step 'service: uninstall'
    & $cli service uninstall
    $uninstallExit = $LASTEXITCODE
    $logs = Join-Path $env:ProgramData 'SplitPlay\logs\service.log'
    if (Test-Path $logs) { Get-Content $logs | Select-Object -Last 20 }
}
if ($uninstallExit -ne 0) { throw "uninstall exited with $uninstallExit" }
& sc.exe query SplitPlay | Out-Null
if ($LASTEXITCODE -ne 1060) { throw "service still registered after uninstall (sc query exit $LASTEXITCODE)" }
$global:LASTEXITCODE = 0

# --------------------------------------------------------------------------- tray
Step 'tray: headless self-test'
$trayReport = Join-Path $Work 'tray.txt'
$t = Start-Process -FilePath $tray -ArgumentList @('--smoke-test', "`"$trayReport`"") -PassThru -Wait
Get-Content $trayReport
if ($t.ExitCode -ne 0) { throw "tray self-test exited with $($t.ExitCode)" }

Write-Host "`nSmoke test passed." -ForegroundColor Green
