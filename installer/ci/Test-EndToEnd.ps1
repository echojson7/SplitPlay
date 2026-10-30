# SPDX-License-Identifier: MIT
# End-to-end smoke test of a SplitPlay-Setup-*.exe built from the real component artifacts: silent install (with
# /SkipTermWrap=1: the runner's TermService keeps its own termsrv.dll), host preparation by the installer, then the
# installed service (LocalSystem) provisions a desk account and runs a desk with the real splitplay-stubworker.exe in
# the runner's console session through the whole control-IPC lifecycle, driven by the installed CLI and checked over
# the Web API; finally a purging uninstall with /DeleteDeskAccounts=1, which must revert the host settings, delete the
# desk accounts SplitPlay created and leave every other account alone.
# The stub worker and "no seat" are selected through the service's Environment registry value (a loopback RDP
# session from the real seat is covered by service/build/smoke-test.ps1 when a native build is available).
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
$serviceKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\SplitPlay'
$tsKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server'
$termServiceKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\TermService\Parameters'
$deskUser = $null
$deskUser2 = $null
$userListKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList'
# A local account SplitPlay did not create: no uninstall option may delete it.
$ownUser = 'e2e-own'

function Get-UserListValue([string] $user) {
    # Strict mode: a missing value is not a property at all.
    $item = Get-ItemProperty -Path $userListKey -ErrorAction SilentlyContinue
    if ($item -and ($item.PSObject.Properties.Name -contains $user)) { return $item.$user }
    return $null
}

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

# Everything but /health and /auth/* needs the admin: a bearer token from the admin pipe (this shell is elevated).
$script:apiToken = $null
function Get-ApiHeaders {
    if (-not $script:apiToken) {
        $out = (& $cli auth token 2>&1) -join "`n"
        if ($LASTEXITCODE -ne 0) { throw "splitplay auth token exited with ${LASTEXITCODE}: $out" }
        $script:apiToken = $out.Trim()
    }
    @{ Authorization = "Bearer $script:apiToken" }
}

function Get-Desk([string] $id) { Invoke-RestMethod -Headers (Get-ApiHeaders) -Uri "$base/desks/$id" }

$fDenyBefore = (Get-ItemProperty -Path $tsKey -Name fDenyTSConnections -ErrorAction SilentlyContinue).fDenyTSConnections
$dllBefore = (Get-ItemProperty -Path $termServiceKey -Name ServiceDll).ServiceDll
Write-Host "before: fDenyTSConnections=$fDenyBefore, TermService ServiceDll=$dllBefore"

$ok = $false
try {
    Step 'install'
    Invoke-Silent $Setup 'e2e-install.log' '/AllowUnsupportedOS=1 /SkipWebView2=1 /SkipTermWrap=1'
    $svc = Get-Service -Name SplitPlay
    if ($svc.Status -ne 'Running') { throw "service status is $($svc.Status)" }
    foreach ($f in 'termwrap\TermWrap.dll', 'termwrap\UmWrap.dll') {
        if (-not (Test-Path (Join-Path $app $f))) { throw "missing $f" }
    }

    Step 'host prepared by the installer (TermWrap skipped)'
    Invoke-Cli host status
    if ((Get-ItemProperty -Path $tsKey).fDenyTSConnections -ne 0) { throw 'setup did not enable Remote Desktop (fDenyTSConnections)' }
    if ((Get-ItemProperty -Path $termServiceKey).ServiceDll -ne $dllBefore) { throw 'TermService ServiceDll changed despite /SkipTermWrap=1' }
    if (-not (Test-Path (Join-Path $data 'host-tuning.json'))) { throw 'host-tuning.json was not written' }

    Step 'select the stub worker without a seat (service Environment)'
    Set-ItemProperty -Path $serviceKey -Name Environment -Type MultiString -Value @(
        'Supervisor__Worker=stub',
        'Supervisor__Seat=none',
        'Supervisor__WorkerSession=console'
    )
    Invoke-Cli service stop --timeout 60
    Invoke-Cli service start --timeout 90

    Step 'health and web UI'
    $health = Wait-Health 60
    Write-Host "health: $($health | ConvertTo-Json -Compress)"
    $index = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/" -UseBasicParsing
    if ($index.Content -notmatch 'id="app"') { throw 'the web UI (web/dist) is not served from service\wwwroot' }
    $session = Invoke-RestMethod -Uri "$base/auth/session"
    Write-Host "auth session: $($session | ConvertTo-Json -Compress)"
    if (-not $session.setupRequired) { throw 'a fresh install should report setupRequired' }
    if ($session.authenticated) { throw 'an anonymous caller must not be authenticated' }
    $refused = $false
    try { Invoke-RestMethod -Uri "$base/desks" | Out-Null } catch { $refused = $_.Exception.Response.StatusCode.value__ -eq 401 }
    if (-not $refused) { throw 'GET /desks without a token was not refused with 401' }

    Step 'desk lifecycle with the real stub worker (service as LocalSystem)'
    # The stub worker renders and checks synthetic frames on the CPU (WARP); keep it light so that it does not starve
    # the service on a 4-core runner.
    Invoke-Cli desk create e2e-1 --name "End to end" --fps 15 --width 640 --height 360
    $deskUser = (Get-Desk 'e2e-1').user
    Invoke-Cli desk start e2e-1 --wait --timeout 90
    $desk = Get-Desk 'e2e-1'
    Write-Host "desk: $($desk | ConvertTo-Json -Compress -Depth 5)"
    if ($desk.state -ne 'Ready') { throw "desk state is $($desk.state), expected Ready ($($desk.lastError))" }
    $workers = @(Get-Process -Name 'splitplay-stubworker' -ErrorAction SilentlyContinue)
    if ($workers.Count -ne 1) { throw "expected one splitplay-stubworker process, found $($workers.Count)" }
    if ($workers[0].SessionId -eq 0 -or $workers[0].SessionId -ne $desk.sessionId) {
        throw "the stub worker runs in session $($workers[0].SessionId), expected the desk session $($desk.sessionId)"
    }

    Step 'desk account provisioned'
    $account = Get-LocalUser -Name $deskUser
    if ($null -ne $account.PasswordExpires) { throw "the password of $deskUser expires ($($account.PasswordExpires))" }
    $rdu = Get-LocalGroupMember -SID 'S-1-5-32-555' | Where-Object { $_.SID -eq $account.SID }
    if (-not $rdu) { throw "$deskUser is not in Remote Desktop Users" }
    if (-not (Test-Path (Join-Path $data 'secrets\desks\e2e-1.bin'))) { throw 'the desk secret was not stored' }
    if (-not (Get-NetFirewallRule -DisplayName 'SplitPlay desk e2e-1 *' -ErrorAction SilentlyContinue)) { throw 'no per-desk firewall rules' }
    if ((Get-UserListValue $deskUser) -ne 0) { throw "$deskUser is not hidden from the sign-in screen (UserList)" }

    # Heartbeats keep the desk Ready for longer than the 3 s hang timeout.
    Start-Sleep -Seconds 6
    $desk = Get-Desk 'e2e-1'
    if ($desk.state -ne 'Ready') { throw "desk left Ready after 6 s: $($desk.state) ($($desk.lastError))" }

    $metrics = Invoke-RestMethod -Headers (Get-ApiHeaders) -Uri "$base/metrics"
    Write-Host "metrics: $($metrics | ConvertTo-Json -Compress -Depth 5)"
    $logs = @(Invoke-RestMethod -Headers (Get-ApiHeaders) -Uri "$base/logs?desk=e2e-1&tail=50")
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
    Invoke-Cli desk delete e2e-1 --delete-user
    if (Get-LocalUser -Name $deskUser -ErrorAction SilentlyContinue) { throw "$deskUser still exists after delete --delete-user" }
    if (Test-Path (Join-Path $data 'secrets\desks\e2e-1.bin')) { throw 'the desk secret is still there after delete --delete-user' }
    if (Get-NetFirewallRule -DisplayName 'SplitPlay desk e2e-1 *' -ErrorAction SilentlyContinue) { throw 'per-desk firewall rules left after delete' }
    if ($null -ne (Get-UserListValue $deskUser)) { throw "the UserList entry of $deskUser is still there after delete" }

    Step 'a second desk for the uninstall to clean up'
    New-LocalUser -Name $ownUser -Password (ConvertTo-SecureString ('Zq7#vR' + (Get-Random -Minimum 100000 -Maximum 999999) + '!mK') -AsPlainText -Force) -AccountNeverExpires | Out-Null
    Invoke-Cli desk create e2e-2 --name "End to end 2" --fps 15 --width 640 --height 360
    $deskUser2 = (Get-Desk 'e2e-2').user
    Invoke-Cli desk start e2e-2 --wait --timeout 90
    if ((Get-Desk 'e2e-2').state -ne 'Ready') { throw "desk e2e-2 did not start: $((Get-Desk 'e2e-2').lastError)" }
    if ((Get-UserListValue $deskUser2) -ne 0) { throw "$deskUser2 is not hidden from the sign-in screen (UserList)" }
    Invoke-Cli desk stop e2e-2 --wait --timeout 30
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
    if (Test-Path $uninstaller) { Invoke-Silent $uninstaller 'e2e-uninstall.log' '/PurgeData=1 /DeleteDeskAccounts=1' }
    $ownLeft = $null -ne (Get-LocalUser -Name $ownUser -ErrorAction SilentlyContinue)
    $desk2Left = $deskUser2 -and (Get-LocalUser -Name $deskUser2 -ErrorAction SilentlyContinue)
    $desk2Listed = $deskUser2 -and ($null -ne (Get-UserListValue $deskUser2))
    foreach ($u in @($deskUser, $deskUser2, $ownUser)) {
        if ($u -and (Get-LocalUser -Name $u -ErrorAction SilentlyContinue)) { Remove-LocalUser -Name $u }
    }
}
if ($ok) {
    if (-not $ownLeft) { throw "the uninstaller deleted $ownUser, an account SplitPlay did not create" }
    if ($desk2Left) { throw "the uninstaller with /DeleteDeskAccounts=1 did not delete $deskUser2" }
    if ($desk2Listed) { throw "the UserList entry of $deskUser2 is still there after the uninstall" }
}
$fDenyAfter = (Get-ItemProperty -Path $tsKey -Name fDenyTSConnections -ErrorAction SilentlyContinue).fDenyTSConnections
if ($fDenyAfter -ne $fDenyBefore) { throw "uninstall did not revert fDenyTSConnections ($fDenyAfter, was $fDenyBefore)" }
if ((Get-ItemProperty -Path $termServiceKey).ServiceDll -ne $dllBefore) { throw 'TermService ServiceDll differs after uninstall' }
if ($ok) { Write-Host 'End-to-end smoke test passed.' }
