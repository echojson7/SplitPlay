# SPDX-License-Identifier: MIT
# Silent install / upgrade / uninstall smoke test for SplitPlay-Setup-*.exe built from the fake stage
# (installer/ci/New-FakeStage.ps1), or with -RealStage from the real component artifacts (ci.yml), where the stub's
# record of splitplay.exe calls is not available and the host preparation is checked by its result instead.
# Needs an elevated shell; GitHub-hosted Windows runners are.
#
#   pwsh installer/ci/Test-Installer.ps1 -Setup build/installer/SplitPlay-Setup-0.1.0-ci.exe -LogDir build/logs
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Setup,
    [Parameter(Mandatory)] [string] $LogDir,
    [switch] $RealStage
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
$stubCalls = Join-Path $env:ProgramData 'SplitPlay-ci-stub-calls.log'
$termServiceKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\TermService\Parameters'
$splitPlayKey = 'HKLM:\Software\SplitPlay'
$uninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
$failures = 0

# The runner's own Remote Desktop stack must never be touched (/SkipTermWrap=1 everywhere below).
$termServiceDll = (Get-ItemProperty -Path $termServiceKey -Name ServiceDll).ServiceDll
Write-Host "TermService ServiceDll before: $termServiceDll"

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

# Commands the stub splitplay.exe was run with since the last call (installer/ci/stub.cs records them).
function Get-StubCalls {
    $calls = @(if (Test-Path $stubCalls) { Get-Content $stubCalls })
    Remove-Item $stubCalls -ErrorAction SilentlyContinue
    $calls | ForEach-Object { Write-Host "  stub: $_" }
    return $calls
}

# ViGEmBus: setup installs it only when no ViGEmBus driver is present and records that in HKLM\Software\SplitPlay.
# Same test as setup's ViGEmBusPresent: the driver binary exists in System32\drivers or where the service points (a
# service key without its binary does not count).
function Test-ViGEmBus {
    if (Test-Path (Join-Path $env:SystemRoot 'System32\drivers\ViGEmBus.sys')) { return $true }
    $svc = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\ViGEmBus' -ErrorAction SilentlyContinue
    if (-not $svc -or -not $svc.PSObject.Properties['ImagePath'] -or -not $svc.ImagePath) { return $false }
    $image = [string] $svc.ImagePath
    if ($image -match '^\\SystemRoot\\') { $image = Join-Path $env:SystemRoot $image.Substring(12) }
    elseif ($image.StartsWith('\??\')) { $image = $image.Substring(4) }
    elseif (-not $image.Contains(':')) { $image = Join-Path $env:SystemRoot $image }
    return (Test-Path -LiteralPath $image)
}

# The product code setup recorded for the ViGEmBus it installed, or $null.
function Get-ViGEmRecord {
    $p = Get-ItemProperty -Path $splitPlayKey -ErrorAction SilentlyContinue
    if (-not $p -or -not $p.PSObject.Properties['ViGEmBusInstalledBySplitPlay']) { return $null }
    if ($p.PSObject.Properties['ViGEmBusProductCode']) { return [string] $p.ViGEmBusProductCode }
    return ''
}

function Test-TermServiceUntouched {
    $now = (Get-ItemProperty -Path $termServiceKey -Name ServiceDll).ServiceDll
    Check "TermService ServiceDll unchanged ($now)" ($now -eq $termServiceDll)
}

# Rights granted by one ACE of the service DACL ("CCLCSWRPLOCRRC" -> CC, LC, SW, RP, LO, CR, RC), or none.
function Get-AceRights([string] $sddl, [string] $sid) {
    if ($sddl -match "\(A;;([A-Z]+);;;$sid\)") { return @($Matches[1] -split '(..)' | Where-Object { $_ }) }
    return @()
}

# Setup lets signed-in users start the service (the tray starts it without elevation), never stop or change it.
function Test-ServiceDacl {
    $sddl = (& sc.exe sdshow SplitPlay | Where-Object { $_ -match '^D:' }) -join ''
    Write-Host "  service SDDL: $sddl"
    $rights = @(Get-AceRights $sddl 'IU')
    Check "service DACL: interactive users may query and start ($($rights -join ' '))" (($rights -contains 'RP') -and ($rights -contains 'LC'))
    Check 'service DACL: interactive users may not stop, reconfigure or delete' (-not (@($rights | Where-Object { $_ -in 'WP', 'DC', 'SD', 'WD', 'WO', 'DT' }).Count))
    Check 'service DACL: no Authenticated Users ACE' (@(Get-AceRights $sddl 'AU').Count -eq 0)
    $admins = @(Get-AceRights $sddl 'BA')
    Check "service DACL: Administrators keep full control ($($admins -join ' '))" (@('RP', 'WP', 'DC', 'SD', 'WD', 'WO' | Where-Object { $admins -notcontains $_ }).Count -eq 0)
}

function Test-Installed([string] $label, [bool] $termWrap = $true) {
    Write-Host "== $label"
    foreach ($f in 'service\SplitPlay.Service.exe', 'service\splitplay.exe', 'tray\SplitPlay.Tray.exe',
                   'service\wwwroot\index.html', 'native\splitplay-seat.exe', 'native\splitplay-rdsprov.dll',
                   'native\splitplay-stubworker.exe', 'worker\splitplay-worker.exe', 'worker\COPYING',
                   'LICENSE.txt', 'NOTICE.md', 'unins000.exe') {
        Check "file $f" (Test-Path (Join-Path $app $f))
    }
    foreach ($f in 'termwrap\TermWrap.dll', 'termwrap\UmWrap.dll', 'termwrap\Zydis.dll', 'termwrap\LICENSE') {
        if ($termWrap) { Check "file $f (TermWrap component)" (Test-Path (Join-Path $app $f)) }
        else { Check "no $f (TermWrap component not selected)" (-not (Test-Path (Join-Path $app $f))) }
    }

    # Post-install host preparation, always with --skip-termwrap here (the parameter or the deselected component).
    if ($RealStage) {
        Check 'setup prepared the host (host-tuning.json written)' (Test-Path (Join-Path $data 'host-tuning.json'))
    } else {
        $calls = @(Get-StubCalls)
        Check 'setup ran "splitplay host prepare --skip-termwrap"' (@($calls | Where-Object { $_ -eq 'splitplay.exe host prepare --skip-termwrap' }).Count -eq 1)
        Check 'host prepare ran before the service was started' (
            [array]::IndexOf($calls, 'splitplay.exe host prepare --skip-termwrap') -ge 0 -and
            [array]::IndexOf($calls, 'splitplay.exe host prepare --skip-termwrap') -lt [array]::IndexOf($calls, 'SplitPlay.Service.exe --stub-service'))
        $vigemCalls = @($calls | Where-Object { $_ -like 'ViGEmBus_*.exe /exenoui /qn /norestart /exelog *' })
        if ($vigemPreinstalled) { Check 'setup did not run the ViGEmBus setup (ViGEmBus already present)' ($vigemCalls.Count -eq 0) }
        else { Check 'setup ran the ViGEmBus setup silently (/exenoui /qn /norestart)' ($vigemCalls.Count -eq 1) }
        Check 'no ViGEmBus record (the stub setup installs no driver)' ($null -eq (Get-ViGEmRecord))
    }
    if ($RealStage) {
        $record = Get-ViGEmRecord
        if ($vigemPreinstalled) {
            Check 'ViGEmBus was already on the runner: setup did not record it as its own' ($null -eq $record)
        } elseif ($null -ne $record) {
            Check "ViGEmBus installed by setup (product $record) and present" (Test-ViGEmBus)
        } else {
            Write-Host '::warning::setup did not install ViGEmBus on this runner (see vigembus-setup.log and the setup log)'
        }
    }
    Test-TermServiceUntouched
    $svc = Get-Service -Name SplitPlay -ErrorAction SilentlyContinue
    Check 'service SplitPlay exists' ($null -ne $svc)
    if ($svc) {
        Check "service is running (status $($svc.Status))" ($svc.Status -eq 'Running')
        Check "service display name ($($svc.DisplayName))" ($svc.DisplayName -eq 'SplitPlay Host Service')
        Test-ServiceDacl
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
    if ($run) {
        $expected = '"' + (Join-Path $app 'tray\SplitPlay.Tray.exe') + '" --autostart'
        Check "tray autostart starts minimized ($($run.SplitPlayTray))" ($run.SplitPlayTray -eq $expected)
    }
}

# A per-desk rule as the service creates it; the uninstaller must remove every "SplitPlay desk *" rule.
function New-DeskRule {
    New-NetFirewallRule -DisplayName 'SplitPlay desk ci-1 (TCP)' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 48084, 48089, 48110 -Profile Private | Out-Null
}

function Test-Removed([string] $label, [bool] $dataKept, $vigemRecord = $null) {
    Write-Host "== $label"
    Wait-Until { -not (Test-Path $app) }
    Check 'no ViGEmBus record left in HKLM\Software\SplitPlay' ($null -eq (Get-ViGEmRecord))
    if ($vigemRecord) {
        Check "ViGEmBus ($vigemRecord), installed by setup, was uninstalled" (-not (Test-Path (Join-Path $uninstallKey $vigemRecord)))
        Write-Host "  ViGEmBus driver left behind: $(Test-ViGEmBus)"
    } elseif ($vigemPreinstalled) {
        Check 'ViGEmBus that setup did not install was left alone' (Test-ViGEmBus)
    }
    if (-not $RealStage) {
        # With the real CLI the revert itself is checked by Test-EndToEnd.ps1.
        $calls = @(Get-StubCalls)
        Check 'uninstaller ran "splitplay host revert"' (@($calls | Where-Object { $_ -eq 'splitplay.exe host revert' }).Count -eq 1)
    }
    Test-TermServiceUntouched
    Check 'service removed' ($null -eq (Get-Service -Name SplitPlay -ErrorAction SilentlyContinue))
    Check 'firewall rule removed' ($null -eq (Get-Rule))
    Check 'per-desk firewall rules removed' ($null -eq (Get-NetFirewallRule -DisplayName 'SplitPlay desk *' -ErrorAction SilentlyContinue))
    Check 'Program Files\SplitPlay removed' (-not (Test-Path $app))
    Check 'Start menu folder removed' (-not (Test-Path $startMenu))
    Check 'tray autostart removed' ($null -eq (Get-ItemProperty -Path $runKey -Name SplitPlayTray -ErrorAction SilentlyContinue))
    if ($dataKept) { Check 'ProgramData\SplitPlay kept' (Test-Path $data) }
    else { Check 'ProgramData\SplitPlay purged' (-not (Test-Path $data)) }
}

$common = '/AllowUnsupportedOS=1 /SkipWebView2=1 /SkipTermWrap=1'
# Setup skips ViGEmBus on Windows Server (the runners); /ViGEmBusOnServer=1 runs it anyway. With the fake stage that is
# the stub on every install; with the real stage only the first install runs the real setup, which does not return on
# Windows Server, so that pass checks the timeout path once (30 s here) and later passes take the default skip.
$vigemTry = '/ViGEmBusOnServer=1 /ViGEmBusTimeout=30'
$vigemLater = if ($RealStage) { '' } else { " $vigemTry" }
$uninstaller = Join-Path $app 'unins000.exe'
Remove-Item $stubCalls -ErrorAction SilentlyContinue
$vigemPreinstalled = Test-ViGEmBus
Write-Host "ViGEmBus present before: $vigemPreinstalled"

# 1. Fresh install.
$started = Get-Date
Check 'install exit code 0' ((Invoke-Silent $Setup 'install.log' "$common $vigemTry") -eq 0)
Write-Host ("  install took {0:N0} s" -f ((Get-Date) - $started).TotalSeconds)
Check 'no ViGEmBus setup or nefconw process left running' (
    $null -eq (Get-Process -Name 'ViGEmBus_*', 'nefconw' -ErrorAction SilentlyContinue))
$outcome = Select-String -Path (Join-Path $LogDir 'install.log') -Pattern 'ViGEmBus (installed by SplitPlay|setup timed out|setup failed|setup reported success|is already present|is present)' |
    Select-Object -First 1
Check "setup log states the ViGEmBus outcome ($(if ($outcome) { $outcome.Matches[0].Value }))" ($null -ne $outcome)
Test-Installed 'after install'

# 2. Upgrade in place: the running service must be stopped, files replaced and the service started again.
Set-Content -Path (Join-Path $data 'config.json') -Value '{"schemaVersion":1}'
Check 'reinstall exit code 0' ((Invoke-Silent $Setup 'reinstall.log' "$common$vigemLater") -eq 0)
Test-Installed 'after reinstall'

# 3. Uninstall, keeping ProgramData (the silent default).
New-DeskRule
$vigemRecord = Get-ViGEmRecord
if (-not $RealStage) {
    # As if setup had installed ViGEmBus: the uninstaller must try to remove that product (msiexec answers 1605,
    # unknown product) and then drop the record.
    $vigemFake = '{00000000-0000-0000-0000-000000000000}'
    New-Item -Path $splitPlayKey -Force | Out-Null
    New-ItemProperty -Path $splitPlayKey -Name ViGEmBusInstalledBySplitPlay -PropertyType DWord -Value 1 -Force | Out-Null
    New-ItemProperty -Path $splitPlayKey -Name ViGEmBusProductCode -PropertyType String -Value $vigemFake -Force | Out-Null
}
Check 'uninstall exit code 0' ((Invoke-Silent $uninstaller 'uninstall.log') -eq 0)
Test-Removed 'after uninstall (keep data)' $true $vigemRecord
if (-not $RealStage) {
    Check 'uninstaller ran msiexec /x for the recorded ViGEmBus product' (
        $null -ne (Select-String -Path (Join-Path $LogDir 'uninstall.log') -SimpleMatch "msiexec.exe`" /x $vigemFake /qn /norestart"))
}
Check 'config.json kept' (Test-Path (Join-Path $data 'config.json'))

# 4. Install again over the kept data without the TermWrap component, then uninstall with /PurgeData=1.
Check 'install over kept data exit code 0' ((Invoke-Silent $Setup 'install2.log' "$common /COMPONENTS=core$vigemLater") -eq 0)
Test-Installed 'after install over kept data (core only)' $false
New-DeskRule
$vigemRecord = Get-ViGEmRecord
Check 'uninstall /PurgeData=1 exit code 0' ((Invoke-Silent $uninstaller 'uninstall2.log' '/PurgeData=1') -eq 0)
Test-Removed 'after uninstall (purge)' $false $vigemRecord
if (-not $vigemRecord) {
    Check 'uninstaller did not run msiexec (no ViGEmBus installed by setup)' (
        $null -eq (Select-String -Path (Join-Path $LogDir 'uninstall2.log') -SimpleMatch 'msiexec.exe'))
}

if ($failures -gt 0) {
    Write-Host "$failures check(s) failed; setup logs are in $LogDir"
    exit 1
}
Write-Host 'All installer smoke checks passed.'
