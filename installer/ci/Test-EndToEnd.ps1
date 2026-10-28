# SPDX-License-Identifier: MIT
# End-to-end smoke test of a SplitPlay-Setup-*.exe built from the real component artifacts: silent install, then the
# installed service (LocalSystem) runs a desk with the real splitplay-stubworker.exe through the whole control-IPC
# lifecycle, driven by the installed CLI and checked over the Web API; finally a purging uninstall.
# Needs an elevated shell; GitHub-hosted Windows runners are.
#
#   pwsh installer/ci/Test-EndToEnd.ps1 -Setup build/installer/SplitPlay-Setup-0.1.0-ci.exe -LogDir build/logs
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Setup,
    [Parameter(Mandatory)] [string] $LogDir,
    [int] $Port = 38299
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Setup = (Resolve-Path $Setup).Path
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$LogDir = (Resolve-Path $LogDir).Path

$app = Join-Path $env:ProgramFiles 'SplitPlay'
$data = Join-Path $env:ProgramData 'SplitPlay'
$cli = Join-Path $app 'service\splitplay.exe'
$base = "http://127.0.0.1:$Port/api/v1"

function Step([string] $what) { Write-Host "== $what" }

function Invoke-Silent([string] $exe, [string] $logName, [string] $extra = '') {
    $log = Join-Path $LogDir $logName
    $argLine = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LOG=`"$log`" $extra"
    Write-Host "> $exe $argLine"
    $p = Start-Process -FilePath $exe -ArgumentList $argLine -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "$exe exited with $($p.ExitCode); see $log" }
}

function Invoke-Cli {
    Write-Host "> splitplay $($args -join ' ')"
    & $cli @args
    if ($LASTEXITCODE -ne 0) { throw "splitplay $($args -join ' ') exited with $LASTEXITCODE" }
}

function Wait-Health([int] $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ($true) {
        try { return Invoke-RestMethod -Uri "$base/health" -TimeoutSec 5 }
        catch { if ((Get-Date) -gt $deadline) { throw "service did not answer /health within $seconds s: $_" } }
        Start-Sleep -Milliseconds 500
    }
}

function Get-Desk([string] $id) { Invoke-RestMethod -Uri "$base/desks/$id" }

$ok = $false
try {
    Step 'install'
    Invoke-Silent $Setup 'e2e-install.log' '/AllowUnsupportedOS=1 /SkipWebView2=1'
    $svc = Get-Service -Name SplitPlay
    if ($svc.Status -ne 'Running') { throw "service status is $($svc.Status)" }

    Step 'health and web UI'
    $health = Wait-Health 60
    Write-Host "health: $($health | ConvertTo-Json -Compress)"
    $index = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/" -UseBasicParsing
    if ($index.Content -notmatch 'id="app"') { throw 'the web UI (web/dist) is not served from service\wwwroot' }
    $session = Invoke-RestMethod -Uri "$base/auth/session"
    Write-Host "auth session: $($session | ConvertTo-Json -Compress)"
    if (-not $session.setupRequired) { throw 'a fresh install should report setupRequired' }

    Step 'desk lifecycle with the real stub worker (service as LocalSystem)'
    # The stub worker renders and checks synthetic frames on the CPU (WARP); keep it light so that it does not starve
    # the service on a 4-core runner.
    Invoke-Cli desk create e2e-1 --name "End to end" --fps 15 --width 640 --height 360
    Invoke-Cli desk start e2e-1 --wait --timeout 90
    $desk = Get-Desk 'e2e-1'
    Write-Host "desk: $($desk | ConvertTo-Json -Compress -Depth 5)"
    if ($desk.state -ne 'Ready') { throw "desk state is $($desk.state), expected Ready ($($desk.lastError))" }
    $workers = @(Get-Process -Name 'splitplay-stubworker' -ErrorAction SilentlyContinue)
    if ($workers.Count -ne 1) { throw "expected one splitplay-stubworker process, found $($workers.Count)" }

    # Heartbeats keep the desk Ready for longer than the 3 s hang timeout.
    Start-Sleep -Seconds 6
    $desk = Get-Desk 'e2e-1'
    if ($desk.state -ne 'Ready') { throw "desk left Ready after 6 s: $($desk.state) ($($desk.lastError))" }

    $metrics = Invoke-RestMethod -Uri "$base/metrics"
    Write-Host "metrics: $($metrics | ConvertTo-Json -Compress -Depth 5)"
    $logs = @(Invoke-RestMethod -Uri "$base/logs?desk=e2e-1&tail=50")
    Write-Host "desk log lines: $($logs.Count)"

    Step 'worker crash is recovered'
    Stop-Process -Id $workers[0].Id -Force
    $deadline = (Get-Date).AddSeconds(60)
    do {
        Start-Sleep -Milliseconds 500
        $desk = Get-Desk 'e2e-1'
        $now = @(Get-Process -Name 'splitplay-stubworker' -ErrorAction SilentlyContinue)
    } until (($desk.state -eq 'Ready' -and $now.Count -eq 1 -and $now[0].Id -ne $workers[0].Id) -or (Get-Date) -gt $deadline)
    if ($desk.state -ne 'Ready') { throw "desk did not recover after the worker was killed: $($desk.state) ($($desk.lastError))" }
    Write-Host "recovered with a new worker (pid $($now[0].Id))"

    Step 'stop and delete'
    Invoke-Cli desk stop e2e-1 --wait --timeout 30
    if ((Get-Desk 'e2e-1').state -ne 'Stopped') { throw 'desk did not stop' }
    Start-Sleep -Seconds 2
    if (@(Get-Process -Name 'splitplay-stubworker' -ErrorAction SilentlyContinue).Count -ne 0) { throw 'stub worker still running after stop' }
    Invoke-Cli desk delete e2e-1
    $ok = $true
} finally {
    if (-not $ok) {
        Write-Host '--- service log (tail) ---'
        Get-Content (Join-Path $data 'logs\service.log') -Tail 120 -ErrorAction SilentlyContinue
        Get-Process -Name 'splitplay-*', 'SplitPlay.*' -ErrorAction SilentlyContinue | Format-Table Id, ProcessName, CPU, WorkingSet64 | Out-String | Write-Host
    }
    Copy-Item -Recurse -Force -Path (Join-Path $data 'logs') -Destination (Join-Path $LogDir 'programdata-logs') -ErrorAction SilentlyContinue
    Step 'uninstall (purge)'
    $uninstaller = Join-Path $app 'unins000.exe'
    if (Test-Path $uninstaller) { Invoke-Silent $uninstaller 'e2e-uninstall.log' '/PurgeData=1' }
}
if ($ok) { Write-Host 'End-to-end smoke test passed.' }
