# SPDX-License-Identifier: MIT
<#
.SYNOPSIS
  Smoke and end-to-end test for the published service, CLI and tray (run elevated on Windows; used by CI).

.DESCRIPTION
  1. Host preparation: "splitplay host prepare --skip-termwrap" (never swaps the runner's TermService DLL), checks
     "host status" and the registry, and at the very end "host revert" restores the previous values.
  2. Console mode with the real Windows platform: a desk whose user name contains spaces is provisioned for real
     (local account, Users + Remote Desktop Users by SID, password never expires, DPAPI secret that logs on,
     firewall rules), runs the "fake-child" stand-in with the real worker command line, survives export/import and
     is deleted together with its account.
  3. The Windows service (LocalSystem): registration checks, then a desk whose worker is launched into the console
     session with the duplicated SYSTEM token (CreateProcessAsUser + TokenSessionId) and a job object; a killed
     worker is relaunched.
  4. If -Native points at a native build with splitplay-seat.exe: the real seat opens a loopback RDP session for a
     fresh desk user and the worker runs inside that session; stop logs the session off. A Stage 2 seat (null RDP
     connector) is reported and skipped.
  5. SplitPlay.Tray.exe --smoke-test.

.PARAMETER Publish
  Folder with service\, cli\ and tray\ (the service-win-x64 artifact).

.PARAMETER Native
  Optional folder with the native build (splitplay-seat.exe), e.g. the native-win-x64 artifact.
#>
param(
    [Parameter(Mandatory = $true)][string]$Publish,
    [string]$Work = (Join-Path ([IO.Path]::GetTempPath()) "splitplay-smoke"),
    [int]$Port = 38299,
    [string]$Native = ''
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
$programData = Join-Path $env:ProgramData 'SplitPlay'
$consoleData = Join-Path $Work 'console-data'
$tsKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server'
$serviceKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\SplitPlay'

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

function Get-Desk([string]$id) { Invoke-RestMethod -Uri "$base/desks/$id" }

function Wait-Desk([string]$id, [scriptblock]$until, [int]$seconds, [string]$what) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ($true) {
        $d = Get-Desk $id
        if (& $until $d) { return $d }
        if ((Get-Date) -gt $deadline) { throw "desk $id did not reach $what within $seconds s: state $($d.state), lastError $($d.lastError)" }
        Start-Sleep -Milliseconds 500
    }
}

# Desk user checks. Groups are compared by SID: the names are localized.
function Assert-DeskUser([string]$user, [string]$deskId, [string]$dataRoot) {
    $u = Get-LocalUser -Name $user -ErrorAction SilentlyContinue
    if (-not $u) { throw "local user '$user' was not created" }
    if (-not $u.Enabled) { throw "local user '$user' is disabled" }
    if ($null -ne $u.PasswordExpires) { throw "password of '$user' expires ($($u.PasswordExpires)); UF_DONT_EXPIRE_PASSWD missing" }
    if ($u.Description -notlike 'SplitPlay desk account*') { throw "unexpected comment on '$user': $($u.Description)" }
    foreach ($group in 'S-1-5-32-545', 'S-1-5-32-555') {
        $members = @(Get-LocalGroupMember -SID $group | Where-Object { $_.SID -eq $u.SID })
        if ($members.Count -ne 1) { throw "'$user' is not a member of $group" }
    }
    $secret = Join-Path $dataRoot "secrets\desks\$deskId.bin"
    if (-not (Test-Path $secret)) { throw "secret $secret missing" }

    # The stored password is DPAPI LocalMachine protected and must log the user on (Windows PowerShell has both APIs).
    $check = @"
Add-Type -AssemblyName System.Security
Add-Type -AssemblyName System.DirectoryServices.AccountManagement
`$bytes = [IO.File]::ReadAllBytes('$secret')
`$entropy = [Text.Encoding]::UTF8.GetBytes('SplitPlay.desk.$deskId')
`$plain = [Security.Cryptography.ProtectedData]::Unprotect(`$bytes, `$entropy, 'LocalMachine')
`$password = [Text.Encoding]::UTF8.GetString(`$plain)
if (`$password.Length -ne 32) { exit 3 }
`$ctx = New-Object System.DirectoryServices.AccountManagement.PrincipalContext('Machine')
if (`$ctx.ValidateCredentials('$user', `$password)) { exit 0 } else { exit 4 }
"@
    & powershell.exe -NoProfile -NonInteractive -Command $check
    if ($LASTEXITCODE -ne 0) { throw "the stored password of '$user' does not work (check exit $LASTEXITCODE)" }
    Write-Host "  user '$user': created, groups by SID, password never expires, DPAPI secret logs on"
}

function Assert-FirewallRules([string]$deskId, [bool]$present) {
    foreach ($proto in 'TCP', 'UDP') {
        $rule = Get-NetFirewallRule -DisplayName "SplitPlay desk $deskId ($proto)" -ErrorAction SilentlyContinue
        if ($present -and -not $rule) { throw "firewall rule for $deskId ($proto) missing" }
        if (-not $present -and $rule) { throw "firewall rule for $deskId ($proto) still present" }
    }
}

function Get-FakeChildren {
    # "<exe> fake-child ...", not the console-mode service whose "--Supervisor:WorkerArguments=fake-child" has the word too.
    @(Get-CimInstance Win32_Process -Filter "Name = 'SplitPlay.Service.exe'" | Where-Object { $_.CommandLine -match '\.exe"?\s+fake-child\s' })
}

# Steam isolation checks read a user's hives from disk (the user is not logged on): NTUSER.DAT under
# HKU\SplitPlaySmoke and, once the profile has one, UsrClass.dat (Software\Classes) under HKU\SplitPlaySmoke_Classes.
function Invoke-WithHive([string]$profileDir, [scriptblock]$action) {
    $mounts = @(@{ Name = 'SplitPlaySmoke'; File = Join-Path $profileDir 'NTUSER.DAT' })
    $classesFile = Join-Path $profileDir 'AppData\Local\Microsoft\Windows\UsrClass.dat'
    if (Test-Path -LiteralPath $classesFile) { $mounts += @{ Name = 'SplitPlaySmoke_Classes'; File = $classesFile } }
    $loaded = @()
    $keys = @()
    try {
        foreach ($m in $mounts) {
            & reg.exe load "HKU\$($m.Name)" $m.File | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "reg load $($m.File) failed ($LASTEXITCODE)" }
            $loaded += $m.Name
            $keys += [Microsoft.Win32.Registry]::Users.OpenSubKey($m.Name, $true)
        }
        & $action $keys[0] $(if ($keys.Count -gt 1) { $keys[1] } else { $null })
    } finally {
        foreach ($k in $keys) { $k.Dispose() }
        [GC]::Collect()
        [GC]::WaitForPendingFinalizers()
        foreach ($name in $loaded) {
            & reg.exe unload "HKU\$name" | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "reg unload HKU\$name failed ($LASTEXITCODE)" }
        }
    }
}

function Get-HiveValue($root, [string]$key, [string]$name) {
    if ($null -eq $root) { return $null }
    $k = $root.OpenSubKey($key)
    if ($null -eq $k) { return $null }
    try {
        $value = $k.GetValue($name, $null, 'DoNotExpandEnvironmentNames')
        if ($null -ne $value -and $k.GetValueKind($name) -ne 'String') { return "unexpected kind $($k.GetValueKind($name))" }
        return $value
    } finally {
        $k.Dispose()
    }
}

# The three Steam isolation values: from the profile on disk, or from the loaded hives (-Sid, user logged on).
function Get-SteamHiveValues([string]$profileDir, [string]$sid) {
    $read = {
        param($root, $classes)
        [pscustomobject]@{
            Env     = Get-HiveValue $root 'Environment' 'steam_master_ipc_name_override'
            Run     = Get-HiveValue $root 'Software\Microsoft\Windows\CurrentVersion\Run' 'Steam'
            Handler = Get-HiveValue $classes 'steam\Shell\Open\Command' ''
            Classes = $null -ne $classes
        }
    }
    if ($sid) {
        $root = [Microsoft.Win32.Registry]::Users.OpenSubKey($sid)
        $classes = [Microsoft.Win32.Registry]::Users.OpenSubKey("${sid}_Classes")
        try { & $read $root $classes } finally { if ($root) { $root.Dispose() }; if ($classes) { $classes.Dispose() } }
    } else {
        Invoke-WithHive $profileDir $read
    }
}

# A profile without a logon (userenv!CreateProfile): NTUSER.DAT copied from the default profile.
function New-UserProfile([string]$sid, [string]$user) {
    if (-not ('SplitPlaySmoke.UserEnv' -as [type])) {
        Add-Type -Namespace SplitPlaySmoke -Name UserEnv -MemberDefinition @'
[DllImport("userenv.dll", CharSet = CharSet.Unicode)]
public static extern int CreateProfile(string sid, string userName, System.Text.StringBuilder profilePath, uint size);
'@
    }
    $path = New-Object System.Text.StringBuilder 260
    $hr = [SplitPlaySmoke.UserEnv]::CreateProfile($sid, $user, $path, 260)
    if ($hr -ne 0) { throw ('CreateProfile({0}) failed: 0x{1:X8}' -f $user, $hr) }
    return $path.ToString()
}

# What Steam's installer puts into HKLM (steam:// handler, InstallPath with a stand-in steam.exe; Steam itself is
# never run in CI). Returns the keys it created, for Remove-MachineSteam.
$steamHandler = '"C:\Program Files (x86)\Steam\steam.exe" -- "%1"'
$fakeSteam = Join-Path $Work 'fake-steam'
function Add-MachineSteam {
    $created = @()
    $hklm = [Microsoft.Win32.Registry]::LocalMachine
    if (-not $hklm.OpenSubKey('SOFTWARE\Classes\steam')) {
        $created += 'SOFTWARE\Classes\steam'
        $k = $hklm.CreateSubKey('SOFTWARE\Classes\steam\Shell\Open\Command'); $k.SetValue('', $steamHandler); $k.Dispose()
        $k = $hklm.CreateSubKey('SOFTWARE\Classes\steam'); $k.SetValue('', 'URL:steam protocol'); $k.SetValue('URL Protocol', ''); $k.Dispose()
    }
    New-Item -ItemType Directory -Path $fakeSteam -Force | Out-Null
    Set-Content -Path (Join-Path $fakeSteam 'steam.exe') -Value 'stand-in'
    if (-not $hklm.OpenSubKey('SOFTWARE\WOW6432Node\Valve')) {
        $created += 'SOFTWARE\WOW6432Node\Valve'
        $k = $hklm.CreateSubKey('SOFTWARE\WOW6432Node\Valve\Steam'); $k.SetValue('InstallPath', $fakeSteam); $k.Dispose()
    }
    return , $created
}

function Remove-MachineSteam([string[]]$keys) {
    foreach ($key in $keys) { [Microsoft.Win32.Registry]::LocalMachine.DeleteSubKeyTree($key, $false) }
}

function Set-ServiceEnvironment([string[]]$values) {
    Set-ItemProperty -Path $serviceKey -Name Environment -Type MultiString -Value $values
}

$fDenyBefore = (Get-ItemProperty -Path $tsKey -Name fDenyTSConnections -ErrorAction SilentlyContinue).fDenyTSConnections
Write-Host "fDenyTSConnections before: $fDenyBefore"

# --------------------------------------------------------------------------- host preparation
Step 'host prepare --skip-termwrap'
$dllBefore = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\TermService\Parameters').ServiceDll
Invoke-Cli host prepare --skip-termwrap
Invoke-Cli --data-root $consoleData host prepare --skip-termwrap
Invoke-Cli host status
if ((Get-ItemProperty -Path $tsKey).fDenyTSConnections -ne 0) { throw 'fDenyTSConnections is not 0 after prepare' }
if ((Get-ItemProperty "$tsKey\WinStations\RDP-Tcp").UserAuthentication -ne 1) { throw 'NLA is not required after prepare' }
if ((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\TermService\Parameters').ServiceDll -ne $dllBefore) { throw 'ServiceDll changed despite --skip-termwrap' }
if (-not (Test-Path (Join-Path $programData 'host-tuning.json'))) { throw 'host-tuning.json not written' }

try {
    # ----------------------------------------------------------------------- console mode
    Step 'console mode: start (real accounts, no seat, fake-child worker with the real command line)'
    $serviceArgs = @(
        '--console',
        '--data-root', "`"$consoleData`"",
        '--Supervisor:Launcher=process',
        '--Supervisor:Seat=none',
        "`"--Supervisor:WorkerExecutable=$svc`"",
        '--Supervisor:WorkerArguments=fake-child'
    )
    $proc = Start-Process -FilePath $svc -ArgumentList $serviceArgs -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $Work 'console.out.log') -RedirectStandardError (Join-Path $Work 'console.err.log')
    try {
        $health = Wait-Health 60
        Write-Host "health: $($health | ConvertTo-Json -Compress)"
        if ($health.version -ne '0.1.0') { throw "unexpected version $($health.version)" }

        Step 'console mode: desk lifecycle via CLI, user name with spaces'
        Invoke-Cli desk create smoke-1 --name "Smoke test" --user "smoke desk 1" --fps 60 --codec hevc
        Invoke-Cli desk list
        Invoke-Cli desk start smoke-1 --wait --timeout 60
        $desk = Get-Desk 'smoke-1'
        if ($desk.state -ne 'Ready') { throw "desk state is $($desk.state), expected Ready ($($desk.lastError))" }
        Assert-DeskUser 'smoke desk 1' 'smoke-1' $consoleData
        Assert-FirewallRules 'smoke-1' $true
        $children = @(Get-FakeChildren)
        if ($children.Count -ne 1) { throw "expected one fake-child worker, found $($children.Count)" }
        if ($children[0].CommandLine -notmatch '--desk-dir' -or $children[0].CommandLine -notmatch 'port=48089') {
            throw "worker command line lacks --desk-dir/port=: $($children[0].CommandLine)"
        }
        Invoke-Cli desk show smoke-1 --json
        Invoke-Cli desk stop smoke-1 --wait --timeout 30
        $desk = Get-Desk 'smoke-1'
        if ($desk.state -ne 'Stopped') { throw "desk state is $($desk.state), expected Stopped" }

        Step 'console mode: config export / import'
        $zip = Join-Path $Work 'config.zip'
        Invoke-Cli config export $zip
        Invoke-Cli desk delete smoke-1
        if (-not (Get-LocalUser -Name 'smoke desk 1' -ErrorAction SilentlyContinue)) { throw 'desk delete without --delete-user removed the account' }
        Invoke-Cli config import $zip
        $desks = @(Invoke-RestMethod -Uri "$base/desks")
        if ($desks.Count -ne 1 -or $desks[0].id -ne 'smoke-1') { throw "import did not restore smoke-1" }

        # The secret went with the first delete: the next start rotates the password of the existing account.
        Invoke-Cli desk start smoke-1 --wait --timeout 60
        Assert-DeskUser 'smoke desk 1' 'smoke-1' $consoleData
        Invoke-Cli desk stop smoke-1 --wait --timeout 30

        Step 'console mode: delete with the Windows account'
        Invoke-Cli desk delete smoke-1 --delete-user
        if (Get-LocalUser -Name 'smoke desk 1' -ErrorAction SilentlyContinue) { throw 'the desk account still exists' }
        if (Test-Path (Join-Path $consoleData 'secrets\desks\smoke-1.bin')) { throw 'the desk secret still exists' }
        Assert-FirewallRules 'smoke-1' $false

        Step 'console mode: an existing Windows user with the owner''s password'
        $existing = 'smoke owner2'
        $existingPassword = 'Sm0ke-' + [guid]::NewGuid().ToString('N').Substring(0, 16) + '!'
        Get-LocalUser -Name $existing -ErrorAction SilentlyContinue | Remove-LocalUser
        New-LocalUser -Name $existing -Password (ConvertTo-SecureString $existingPassword -AsPlainText -Force) -Description 'owner account' | Out-Null
        $existingSid = (Get-LocalUser -Name $existing).SID.Value
        $machineSteamKeys = @()
        try {
            # Steam isolation: the owner's account has a profile (never logged on) with Steam's autostart, and the
            # machine has Steam's steam:// handler and InstallPath.
            $machineSteamKeys = Add-MachineSteam
            $profileDir = New-UserProfile $existingSid $existing
            $steamRun = '"C:\Program Files (x86)\Steam\steam.exe" -silent'
            Invoke-WithHive $profileDir {
                param($root)
                $k = $root.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
                $k.SetValue('Steam', $steamRun)
                $k.Dispose()
            }

            $before = Get-LocalUser -Name $existing
            # A wrong password is refused (400 validation_failed) and nothing is created.
            'wrong-password' | & $cli desk create smoke-4 --user $existing --password-stdin
            if ($LASTEXITCODE -eq 0) { throw 'desk create accepted a wrong password' }
            $existingPassword | & $cli desk create smoke-4 --user $existing --password-stdin
            if ($LASTEXITCODE -ne 0) { throw "desk create with the right password failed ($LASTEXITCODE)" }
            $desk = Get-Desk 'smoke-4'
            if ($desk.userManaged -ne $false -or $desk.hasPassword -ne $true) { throw "unexpected userManaged/hasPassword: $($desk | ConvertTo-Json -Compress)" }
            if ((Get-Content (Join-Path $consoleData 'config.json') -Raw) -match [regex]::Escape($existingPassword)) { throw 'the password reached config.json' }
            Invoke-Cli desk start smoke-4 --wait --timeout 60
            $desk = Get-Desk 'smoke-4'
            if ($desk.state -ne 'Ready') { throw "desk state is $($desk.state) ($($desk.lastError))" }
            if ("$($desk.lastError)" -notmatch 'can expire') { throw "expected a password-expiry warning, got '$($desk.lastError)'" }
            $after = Get-LocalUser -Name $existing
            if ($after.Description -ne 'owner account' -or $after.PasswordExpires -ne $before.PasswordExpires -or $after.PasswordLastSet -ne $before.PasswordLastSet) {
                throw 'SplitPlay changed the existing account'
            }
            if (-not @(Get-LocalGroupMember -SID 'S-1-5-32-555' | Where-Object { $_.SID -eq $after.SID })) { throw 'not added to Remote Desktop Users' }
            if ($desk.steamIsolation -ne $true) { throw "steamIsolation is not on by default: $($desk | ConvertTo-Json -Compress)" }
            if ($machineSteamKeys -contains 'SOFTWARE\WOW6432Node\Valve') {
                $apps = Get-Content (Join-Path $consoleData 'desks\smoke-4\apps.json') -Raw | ConvertFrom-Json
                $steamApp = @($apps.apps | Where-Object { $_.name -eq 'Steam' })
                $expectedApp = "`"$fakeSteam\steam.exe`" -master_ipc_name_override SplitPlay_smoke_4 -gamepadui"
                if ($steamApp.Count -ne 1 -or $steamApp[0].cmd -ne $expectedApp) { throw "apps.json has no isolated Steam entry: $($apps | ConvertTo-Json -Compress -Depth 4)" }
            }
            Invoke-Cli desk stop smoke-4 --wait --timeout 30

            Step 'console mode: Steam isolation values in the desk user''s hive'
            # Written before logon with the profile's NTUSER.DAT mounted by the service (and unloaded again).
            $values = Get-SteamHiveValues $profileDir
            Write-Host "  hive: $($values | ConvertTo-Json -Compress)"
            if ($values.Env -ne 'SplitPlay_smoke_4') { throw "steam_master_ipc_name_override is '$($values.Env)'" }
            if ($values.Run -ne '"C:\Program Files (x86)\Steam\steam.exe" -master_ipc_name_override SplitPlay_smoke_4 -silent') { throw "Run\Steam is '$($values.Run)'" }
            # The steam:// handler lives in UsrClass.dat, which a profile only gets at its first logon.
            if ($values.Classes -and $machineSteamKeys -contains 'SOFTWARE\Classes\steam' -and
                $values.Handler -ne '"C:\Program Files (x86)\Steam\steam.exe" -master_ipc_name_override SplitPlay_smoke_4 -- "%1"') {
                throw "the per-user steam:// handler is '$($values.Handler)'"
            }
            if (-not (Test-Path (Join-Path $consoleData 'desks\smoke-4\steam-isolation.json'))) { throw 'steam-isolation.json not written' }
            & $cli desk delete smoke-4 --delete-user
            if ($LASTEXITCODE -eq 0) { throw 'delete --delete-user removed an account SplitPlay did not create' }
            if (-not (Get-LocalUser -Name $existing -ErrorAction SilentlyContinue)) { throw 'the existing account was deleted' }
            if ((Get-SteamHiveValues $profileDir).Env -ne 'SplitPlay_smoke_4') { throw 'the refused delete reverted Steam isolation' }
            Invoke-Cli desk delete smoke-4
            $global:LASTEXITCODE = 0

            # Deleting the desk restores the hive: no variable, Steam's own autostart, no per-user handler.
            $values = Get-SteamHiveValues $profileDir
            Write-Host "  hive after delete: $($values | ConvertTo-Json -Compress)"
            if ($null -ne $values.Env) { throw "steam_master_ipc_name_override still set: '$($values.Env)'" }
            if ($values.Run -ne $steamRun) { throw "Run\Steam not restored: '$($values.Run)'" }
            if ($null -ne $values.Handler) { throw "per-user steam:// handler still present: '$($values.Handler)'" }
        } finally {
            Remove-MachineSteam $machineSteamKeys
            Get-CimInstance Win32_UserProfile | Where-Object { $_.SID -eq $existingSid } | Remove-CimInstance -ErrorAction SilentlyContinue
            Remove-LocalUser -Name $existing -ErrorAction SilentlyContinue
        }

        $metrics = Invoke-RestMethod -Uri "$base/metrics"
        Write-Host "metrics: $($metrics | ConvertTo-Json -Compress -Depth 5)"
        $index = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/" -UseBasicParsing
        if ($index.Content -notmatch '<title>SplitPlay</title>') { throw 'placeholder index.html not served' }
    } finally {
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
        $proc.WaitForExit(10000) | Out-Null
        Get-Content (Join-Path $Work 'console.out.log') -ErrorAction SilentlyContinue | Select-Object -Last 60
    }
    Wait-PortFree 30

    # ----------------------------------------------------------------------- Windows service
    Step 'service: install'
    Invoke-Cli service install
    try {
        $qc = (& sc.exe qc SplitPlay) -join "`n"
        Write-Host $qc
        if ($qc -notmatch 'AUTO_START' -or $qc -match 'DELAYED') { throw 'service is not plain auto start' }
        if ($qc -notmatch 'SplitPlay Host Service') { throw 'unexpected display name' }
        if ($qc -notmatch 'LocalSystem') { throw 'service does not run as LocalSystem' }
        $qf = (& sc.exe qfailure SplitPlay) -join "`n"
        Write-Host $qf
        if ($qf -notmatch 'RESTART') { throw 'no restart recovery action' }

        # Installing again updates the existing registration.
        Invoke-Cli service install

        Step 'service: worker launched into the console session as SYSTEM (no seat)'
        Set-ServiceEnvironment @(
            'Supervisor__Seat=none',
            'Supervisor__WorkerSession=console',
            "Supervisor__WorkerExecutable=$svc",
            'Supervisor__WorkerArguments=fake-child',
            'Supervisor__Autostart=false'
        )
        Invoke-Cli service start --timeout 90
        $health = Wait-Health 60
        Write-Host "health: $($health | ConvertTo-Json -Compress)"
        Invoke-Cli service status

        Invoke-Cli desk create smoke-2 --user splitplay-smoke2
        Invoke-Cli desk start smoke-2 --wait --timeout 90
        $desk = Get-Desk 'smoke-2'
        if ($desk.state -ne 'Ready') { throw "desk state is $($desk.state), expected Ready ($($desk.lastError))" }
        Assert-DeskUser 'splitplay-smoke2' 'smoke-2' $programData
        $children = @(Get-FakeChildren)
        if ($children.Count -ne 1) { throw "expected one fake-child worker, found $($children.Count)" }
        $child = $children[0]
        $owner = Invoke-CimMethod -InputObject $child -MethodName GetOwner
        Write-Host "worker pid $($child.ProcessId) session $($child.SessionId) owner $($owner.Domain)\$($owner.User); desk sessionId $($desk.sessionId)"
        if ($child.SessionId -eq 0) { throw 'the worker runs in session 0, expected the console session' }
        if ($child.SessionId -ne $desk.sessionId) { throw "worker session $($child.SessionId) differs from desk sessionId $($desk.sessionId)" }
        if ($owner.User -ne 'SYSTEM') { throw "the worker runs as $($owner.User), expected SYSTEM" }

        Step 'service: a killed worker is relaunched in the same session'
        Stop-Process -Id $child.ProcessId -Force
        $desk = Wait-Desk 'smoke-2' { param($d) $d.state -eq 'Ready' -and @(Get-FakeChildren | Where-Object { $_.ProcessId -ne $child.ProcessId }).Count -eq 1 } 60 'a relaunched worker'
        $again = @(Get-FakeChildren)[0]
        if ($again.SessionId -ne $child.SessionId) { throw "relaunched worker is in session $($again.SessionId)" }

        Invoke-Cli desk stop smoke-2 --wait --timeout 30
        Start-Sleep -Seconds 1
        if (@(Get-FakeChildren).Count -ne 0) { throw 'worker still running after stop (job object not closed?)' }
        Invoke-Cli desk delete smoke-2 --delete-user
        if (Get-LocalUser -Name 'splitplay-smoke2' -ErrorAction SilentlyContinue) { throw 'the smoke-2 account still exists' }

        # ------------------------------------------------------------------- real seat (optional)
        $seatExe = if ($Native) { Join-Path $Native 'splitplay-seat.exe' } else { '' }
        if ($seatExe -and (Test-Path $seatExe)) {
            # The native seat comes from another branch's latest build and may lag behind: a failure here is
            # reported as a warning (and in the step summary) instead of failing the service build.
            $seatSteamKeys = @()
            try {
                Step "service: real seat from $Native"
                Invoke-Cli service stop --timeout 60
                Set-ServiceEnvironment @(
                    'Supervisor__Seat=real',
                    "Supervisor__SeatExecutable=$seatExe",
                    "Supervisor__WorkerExecutable=$svc",
                    'Supervisor__WorkerArguments=fake-child',
                    'Supervisor__Autostart=false'
                )
                Invoke-Cli service start --timeout 90
                Wait-Health 60 | Out-Null
                $seatSteamKeys = Add-MachineSteam
                Invoke-Cli desk create smoke-3 --user splitplay-smoke3
                & $cli desk start smoke-3 --wait --timeout 240
                $desk = Get-Desk 'smoke-3'
                Write-Host "desk: $($desk | ConvertTo-Json -Compress -Depth 5)"
                if ($desk.state -eq 'Faulted' -and "$($desk.lastError)" -match "connector '(null|)'") {
                    Write-Warning "the native seat has no RDP connector yet (Stage 2 null connector): real-seat check skipped ($($desk.lastError))"
                    Invoke-Cli desk stop smoke-3 --wait --timeout 60
                } else {
                    if ($desk.state -ne 'Ready') { throw "real seat: desk state is $($desk.state) ($($desk.lastError))" }
                    $consoleSession = (Get-Process -Id $PID).SessionId
                    $children = @(Get-FakeChildren)
                    if ($children.Count -ne 1) { throw "expected one worker, found $($children.Count)" }
                    if ($children[0].SessionId -ne $desk.sessionId) { throw "worker session $($children[0].SessionId) is not the desk session $($desk.sessionId)" }
                    $sessions = (& quser.exe 2>&1) -join "`n"
                    Write-Host $sessions
                    if ($sessions -notmatch 'splitplay-smoke3') { throw 'no RDP session for splitplay-smoke3' }
                    Write-Host "real seat: desk session $($desk.sessionId) (runner session $consoleSession), worker inside it"

                    # Steam isolation in the logged-on hives (written at the first logon, applied by the relogon).
                    $seatSid = (Get-LocalUser -Name 'splitplay-smoke3').SID.Value
                    $live = Get-SteamHiveValues -sid $seatSid
                    Write-Host "  live hive: $($live | ConvertTo-Json -Compress)"
                    if ($live.Env -ne 'SplitPlay_smoke_3') { throw "steam_master_ipc_name_override is '$($live.Env)' in the desk session" }
                    if ($seatSteamKeys -contains 'SOFTWARE\Classes\steam' -and
                        $live.Handler -ne '"C:\Program Files (x86)\Steam\steam.exe" -master_ipc_name_override SplitPlay_smoke_3 -- "%1"') {
                        throw "the per-user steam:// handler is '$($live.Handler)'"
                    }

                    Invoke-Cli desk stop smoke-3 --wait --timeout 60
                    $deadline = (Get-Date).AddSeconds(60)
                    do {
                        Start-Sleep -Seconds 1
                        $sessions = (& quser.exe 2>&1) -join "`n"
                    } until ($sessions -notmatch 'splitplay-smoke3' -or (Get-Date) -gt $deadline)
                    if ($sessions -match 'splitplay-smoke3') { throw 'the desk session was not logged off on stop' }

                    # Turning the option off reverts both hives from disk (NTUSER.DAT and UsrClass.dat mounted).
                    Invoke-RestMethod -Method Put -Uri "$base/desks/smoke-3" -ContentType 'application/json' -Body '{"steamIsolation":false}' | Out-Null
                    $seatProfile = (Get-CimInstance Win32_UserProfile | Where-Object { $_.SID -eq $seatSid }).LocalPath
                    # (Windows may keep a logged-off user's hive loaded for a while: then it is read where it is.)
                    $after = if (Test-Path "Registry::HKEY_USERS\$seatSid") { Get-SteamHiveValues -sid $seatSid } else { Get-SteamHiveValues $seatProfile }
                    Write-Host "  hive after steamIsolation=false: $($after | ConvertTo-Json -Compress)"
                    if ($null -ne $after.Env -or $null -ne $after.Handler) { throw 'steamIsolation=false did not revert the desk user''s hives' }
                }
                Invoke-Cli desk delete smoke-3 --delete-user
            } catch {
                $msg = "real-seat check failed: $($_.Exception.Message)"
                Write-Warning $msg
                Write-Host "::warning title=real seat::$msg"
                if ($env:GITHUB_STEP_SUMMARY) { "**Real seat:** $msg" | Out-File -Append -Encoding utf8 $env:GITHUB_STEP_SUMMARY }
                & $cli desk stop smoke-3 --wait --timeout 60 2>&1 | Out-Host
                & $cli desk delete smoke-3 --delete-user 2>&1 | Out-Host
                $global:LASTEXITCODE = 0
            } finally {
                Remove-MachineSteam $seatSteamKeys
            }
        } else {
            Write-Warning 'no native build with splitplay-seat.exe given (-Native): real-seat check skipped'
        }

        Step 'service: stop'
        Invoke-Cli service stop --timeout 60
        Invoke-Cli service status
    } finally {
        Step 'service: uninstall'
        & $cli service uninstall
        $uninstallExit = $LASTEXITCODE
        $logs = Join-Path $programData 'logs\service.log'
        if (Test-Path $logs) { Get-Content $logs | Select-Object -Last 60 }
    }
    if ($uninstallExit -ne 0) { throw "uninstall exited with $uninstallExit" }
    & sc.exe query SplitPlay | Out-Null
    if ($LASTEXITCODE -ne 1060) { throw "service still registered after uninstall (sc query exit $LASTEXITCODE)" }
    $global:LASTEXITCODE = 0
} finally {
    Step 'host revert'
    & $cli --data-root $consoleData host revert
    & $cli host revert
    $global:LASTEXITCODE = 0
}
$fDenyAfter = (Get-ItemProperty -Path $tsKey -Name fDenyTSConnections -ErrorAction SilentlyContinue).fDenyTSConnections
if ("$fDenyAfter" -ne "$fDenyBefore") { throw "host revert did not restore fDenyTSConnections ($fDenyBefore -> $fDenyAfter)" }
# After revert the host is no longer prepared: status says so with exit code 1.
$statusJson = (& $cli host status --json) -join "`n"
$statusExit = $LASTEXITCODE
Write-Host $statusJson
if ($statusExit -ne 1) { throw "host status after revert exited with $statusExit, expected 1 (not prepared)" }
if (($statusJson | ConvertFrom-Json).prepared -ne $false) { throw 'host status after revert still reports prepared' }
$global:LASTEXITCODE = 0

# --------------------------------------------------------------------------- tray
Step 'tray: headless self-test'
$trayReport = Join-Path $Work 'tray.txt'
$t = Start-Process -FilePath $tray -ArgumentList @('--smoke-test', "`"$trayReport`"") -PassThru -Wait
Get-Content $trayReport
if ($t.ExitCode -ne 0) { throw "tray self-test exited with $($t.ExitCode)" }

Write-Host "`nSmoke test passed." -ForegroundColor Green
