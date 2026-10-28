# SPDX-License-Identifier: MIT
# Silent install / upgrade / uninstall smoke test for SplitPlay-Setup-*.exe built from the fake stage
# (installer/ci/New-FakeStage.ps1). Needs an elevated shell; GitHub-hosted Windows runners are.
#
#   pwsh installer/ci/Test-Installer.ps1 -Setup build/installer/SplitPlay-Setup-0.1.0-ci.exe -LogDir build/logs
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Setup,
    [Parameter(Mandatory)] [string] $LogDir
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Setup = (Resolve-Path $Setup).Path
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$LogDir = (Resolve-Path $LogDir).Path

$app = Join-Path $env:ProgramFiles 'SplitPlay'
$data = Join-Path $env:ProgramData 'SplitPlay'
$startMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\SplitPlay'
$runKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'
$ruleName = 'SplitPlay Web UI'
$failures = 0

function Check([string] $what, [bool] $ok) {
    if ($ok) { Write-Host "  ok   $what" }
    else { Write-Host "  FAIL $what"; $script:failures++ }
}

function Invoke-Silent([string] $exe, [string] $logName, [string] $extra = '') {
    $log = Join-Path $LogDir $logName
    $argLine = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LOG=`"$log`" $extra"
    Write-Host "> $exe $argLine"
    $p = Start-Process -FilePath $exe -ArgumentList $argLine -Wait -PassThru
    Write-Host "  exit code $($p.ExitCode)"
    return $p.ExitCode
}

# The uninstaller deletes unins000.exe and the app folder just after its first phase exits.
function Wait-Until([scriptblock] $condition, [int] $seconds = 60) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while (-not (& $condition) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
}

function Get-Rule { Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue }

function Test-Installed([string] $label) {
    Write-Host "== $label"
    foreach ($f in 'service\SplitPlay.Service.exe', 'service\splitplay.exe', 'tray\SplitPlay.Tray.exe',
                   'service\wwwroot\index.html', 'native\splitplay-seat.exe', 'native\splitplay-rdsprov.dll',
                   'native\splitplay-stubworker.exe', 'worker\splitplay-worker.exe', 'worker\COPYING',
                   'LICENSE.txt', 'NOTICE.md', 'unins000.exe') {
        Check "file $f" (Test-Path (Join-Path $app $f))
    }
    $svc = Get-Service -Name SplitPlay -ErrorAction SilentlyContinue
    Check 'service SplitPlay exists' ($null -ne $svc)
    if ($svc) {
        Check "service is running (status $($svc.Status))" ($svc.Status -eq 'Running')
        Check "service display name ($($svc.DisplayName))" ($svc.DisplayName -eq 'SplitPlay Host Service')
    }

    $rule = Get-Rule
    Check 'firewall rule exists' ($null -ne $rule)
    if ($rule) {
        Check 'firewall rule is disabled by default (loopback only)' ($rule.Enabled -eq 'False')
        $port = $rule | Get-NetFirewallPortFilter
        Check "firewall rule port ($($port.LocalPort))" ($port.LocalPort -eq '38299')
    }

    Check 'ProgramData\SplitPlay exists' (Test-Path $data)
    if (Test-Path $data) {
        $acl = Get-Acl $data
        $sids = @($acl.Access | ForEach-Object {
            $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value
        } | Sort-Object -Unique)
        Write-Host "  ACL: $($acl.Access | ForEach-Object { "$($_.IdentityReference)=$($_.FileSystemRights)" })"
        Check 'ProgramData ACL is protected (no inheritance)' $acl.AreAccessRulesProtected
        Check "ProgramData ACL is SYSTEM + Administrators only ($($sids -join ', '))" (($sids -join ',') -eq 'S-1-5-18,S-1-5-32-544')
        Check 'ProgramData\SplitPlay\logs exists' (Test-Path (Join-Path $data 'logs'))
    }

    Check 'Start menu shortcut' (Test-Path (Join-Path $startMenu 'SplitPlay.lnk'))
    Check 'Start menu web UI link' (@(Get-ChildItem $startMenu -Filter '*.url' -ErrorAction SilentlyContinue).Count -eq 1)
    $run = Get-ItemProperty -Path $runKey -Name SplitPlayTray -ErrorAction SilentlyContinue
    Check 'tray autostart (HKLM Run)' ($null -ne $run)
}

function Test-Removed([string] $label, [bool] $dataKept) {
    Write-Host "== $label"
    Wait-Until { -not (Test-Path $app) }
    Check 'service removed' ($null -eq (Get-Service -Name SplitPlay -ErrorAction SilentlyContinue))
    Check 'firewall rule removed' ($null -eq (Get-Rule))
    Check 'Program Files\SplitPlay removed' (-not (Test-Path $app))
    Check 'Start menu folder removed' (-not (Test-Path $startMenu))
    Check 'tray autostart removed' ($null -eq (Get-ItemProperty -Path $runKey -Name SplitPlayTray -ErrorAction SilentlyContinue))
    if ($dataKept) { Check 'ProgramData\SplitPlay kept' (Test-Path $data) }
    else { Check 'ProgramData\SplitPlay purged' (-not (Test-Path $data)) }
}

$common = '/AllowUnsupportedOS=1 /SkipWebView2=1'
$uninstaller = Join-Path $app 'unins000.exe'

# 1. Fresh install.
Check 'install exit code 0' ((Invoke-Silent $Setup 'install.log' $common) -eq 0)
Test-Installed 'after install'

# 2. Upgrade in place: the running service must be stopped, files replaced and the service started again.
Set-Content -Path (Join-Path $data 'config.json') -Value '{"schemaVersion":1}'
Check 'reinstall exit code 0' ((Invoke-Silent $Setup 'reinstall.log' $common) -eq 0)
Test-Installed 'after reinstall'

# 3. Uninstall, keeping ProgramData (the silent default).
Check 'uninstall exit code 0' ((Invoke-Silent $uninstaller 'uninstall.log') -eq 0)
Test-Removed 'after uninstall (keep data)' $true
Check 'config.json kept' (Test-Path (Join-Path $data 'config.json'))

# 4. Install again over the kept data, then uninstall with /PurgeData=1.
Check 'install over kept data exit code 0' ((Invoke-Silent $Setup 'install2.log' $common) -eq 0)
Test-Installed 'after install over kept data'
Check 'uninstall /PurgeData=1 exit code 0' ((Invoke-Silent $uninstaller 'uninstall2.log' '/PurgeData=1') -eq 0)
Test-Removed 'after uninstall (purge)' $false

if ($failures -gt 0) {
    Write-Host "$failures check(s) failed; setup logs are in $LogDir"
    exit 1
}
Write-Host 'All installer smoke checks passed.'
