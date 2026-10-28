# SPDX-License-Identifier: GPL-3.0-only
#
# In-session test for splitplay-worker.exe (PowerShell 7, Windows, run as an administrator on a throw-away machine
# such as a GitHub Actions runner: it enables RDP and creates a local user).
#
#   pwsh worker/scripts/session-test.ps1 -FreeRdp <dir or exe> [-Exe <splitplay-worker.exe>] [-WorkDir <dir>]
#                                        [-SelfTestSeconds 10] [-RequireAudio] [-ProbeOnly]
#
# 1. Prepares the RDP host (fDenyTSConnections=0, TermService, Windows Audio), creates a local user in
#    Users + Remote Desktop Users and logs it on over loopback RDP (127.0.0.2) with FreeRDP, audio channel on.
#    FreeRDP stands in for splitplay-seat here.
# 2. Starts the worker the way SplitPlay.Service does (session-launch.ps1 through a SYSTEM scheduled task):
#    SYSTEM token with TokenSessionId = N, winsta0\default, CREATE_NO_WINDOW, own job object.
# 3. --self-test: frames are captured from the session's display (DDA), the cursor follows SendInput, and
#    the session's default render endpoint is captured through WASAPI loopback (required with -RequireAudio).
# 4. Control IPC in the session: hello reports session N, ready comes after the capture probe found the
#    session's output, shutdown ends the worker with exit code 0.
# -ProbeOnly stops after step 1 and checks the launch mechanism with a PowerShell child instead of the worker.
param(
  [string]$Exe = (Join-Path $PSScriptRoot '..\out\splitplay-worker.exe'),
  [string]$WorkDir = (Join-Path ([System.IO.Path]::GetTempPath()) 'splitplay-session-test'),
  [Parameter(Mandatory)] [string]$FreeRdp,
  [int]$SelfTestSeconds = 10,
  [switch]$RequireAudio,
  [switch]$ProbeOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$User = 'splitplay-ci'
$Launcher = Join-Path $PSScriptRoot 'session-launch.ps1'
$Pwsh = (Get-Process -Id $PID).Path
$BasePort = 48189
$script:Tasks = @()
$script:Rdp = $null
$script:Session = -1

function Fail([string]$Message) {
  Write-Host "::error::session-test: $Message"
  throw $Message
}

function Assert([bool]$Condition, [string]$Message) {
  if (-not $Condition) { Fail $Message }
}

function Wait-Until([scriptblock]$Condition, [int]$TimeoutSec, [string]$What) {
  $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
  while (-not (& $Condition)) {
    if ([DateTime]::UtcNow -gt $deadline) { Fail "timed out after $TimeoutSec s waiting for $What" }
    Start-Sleep -Milliseconds 500
  }
}

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class SplitPlayWts {
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  struct WTS_SESSION_INFO { public int SessionId; public string pWinStationName; public int State; }

  [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr info, out int count);
  [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  static extern bool WTSQuerySessionInformationW(IntPtr server, int session, int cls, out IntPtr buffer, out int bytes);
  [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr memory);

  const int WTSUserName = 5;
  public const int WTSActive = 0;

  static string UserOf(int session) {
    IntPtr buffer; int bytes;
    if (!WTSQuerySessionInformationW(IntPtr.Zero, session, WTSUserName, out buffer, out bytes)) return "";
    try { return Marshal.PtrToStringUni(buffer) ?? ""; } finally { WTSFreeMemory(buffer); }
  }

  // "id state station user" per session.
  public static string[] List() {
    IntPtr info; int count;
    var lines = new List<string>();
    if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out info, out count)) return lines.ToArray();
    try {
      int size = Marshal.SizeOf(typeof(WTS_SESSION_INFO));
      for (int i = 0; i < count; i++) {
        var s = (WTS_SESSION_INFO)Marshal.PtrToStructure(new IntPtr(info.ToInt64() + i * size), typeof(WTS_SESSION_INFO));
        lines.Add(s.SessionId + " " + s.State + " " + s.pWinStationName + " " + UserOf(s.SessionId));
      }
    } finally { WTSFreeMemory(info); }
    return lines.ToArray();
  }

  // Id of the active session of `user`, or -1.
  public static int FindActive(string user) {
    foreach (var line in List()) {
      var f = line.Split(new[] { ' ' }, 4);
      if (f.Length == 4 && f[1] == WTSActive.ToString() && string.Equals(f[3], user, StringComparison.OrdinalIgnoreCase)) return int.Parse(f[0]);
    }
    return -1;
  }
}
'@

# --- RDP host and user -------------------------------------------------------------------------------------------

function Initialize-RdpHost {
  $ts = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server'
  Set-ItemProperty $ts -Name fDenyTSConnections -Value 0 -Type DWord
  $port = (Get-ItemProperty "$ts\WinStations\RDP-Tcp").PortNumber
  # Remote Audio needs the Windows Audio service, which is off on Windows Server.
  foreach ($svc in 'AudioEndpointBuilder', 'Audiosrv', 'TermService') {
    Set-Service $svc -StartupType Automatic
    Start-Service $svc
  }
  Wait-Until {
    $c = [System.Net.Sockets.TcpClient]::new()
    try { $c.Connect('127.0.0.2', $port); $true } catch { $false } finally { $c.Dispose() }
  } 60 "RDP listening on 127.0.0.2:$port"
  Write-Host "    RDP listening on port $port; NLA (UserAuthentication) = $((Get-ItemProperty "$ts\WinStations\RDP-Tcp").UserAuthentication)"
  $port
}

function New-DeskUser {
  $chars = 'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'.ToCharArray()
  $bytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24)
  $password = 'Sp1!' + -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
  $secure = ConvertTo-SecureString $password -AsPlainText -Force
  if (Get-LocalUser -Name $User -ErrorAction SilentlyContinue) { Remove-LocalUser -Name $User }
  New-LocalUser -Name $User -Password $secure -PasswordNeverExpires -AccountNeverExpires -Description 'SplitPlay CI desk' | Out-Null
  foreach ($group in 'Users', 'Remote Desktop Users') {
    try { Add-LocalGroupMember -Group $group -Member $User } catch { if ($_.FullyQualifiedErrorId -notmatch 'MemberExists') { throw } }
  }
  $password
}

function Find-FreeRdp {
  if (Test-Path $FreeRdp -PathType Leaf) { return (Resolve-Path $FreeRdp).Path }
  foreach ($name in 'wfreerdp.exe', 'sdl3-freerdp.exe', 'sdl-freerdp.exe', 'sdl2-freerdp.exe') {
    $candidate = Join-Path $FreeRdp $name
    if (Test-Path $candidate) { return $candidate }
  }
  Write-Host "FreeRDP clients in ${FreeRdp}:"
  Get-ChildItem $FreeRdp -Filter '*freerdp*' | ForEach-Object { Write-Host "    $($_.Name)" }
  Fail "no FreeRDP client (wfreerdp/sdl-freerdp) in $FreeRdp"
}

# Log the user on over loopback RDP. FreeRDP plays the seat: NLA, the audio playback channel on with the "fake"
# backend that discards the samples (so the session gets "Remote Audio" although the runner has no sound device),
# certificate check off (loopback, test only).
function Connect-Session([int]$Port, [string]$Password) {
  $client = Find-FreeRdp
  $logDir = Join-Path $WorkDir 'freerdp'
  New-Item -ItemType Directory -Force $logDir | Out-Null
  $psi = [System.Diagnostics.ProcessStartInfo]::new($client)
  foreach ($a in @("/v:127.0.0.2:$Port", "/u:$User", "/p:$Password", '/cert:ignore', '/size:1280x720', '/sound:sys:fake',
      '/log-level:INFO', '-clipboard')) { $psi.ArgumentList.Add($a) }
  $psi.UseShellExecute = $false
  $psi.WorkingDirectory = Split-Path $client
  $psi.Environment['PATH'] = (Split-Path $client) + ';' + $env:PATH
  $psi.Environment['WLOG_APPENDER'] = 'FILE'
  $psi.Environment['WLOG_FILEAPPENDER_OUTPUT_FILE_PATH'] = $logDir
  $psi.Environment['WLOG_FILEAPPENDER_OUTPUT_FILE_NAME'] = 'freerdp.log'
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  Write-Host "    $client /v:127.0.0.2:$Port /u:$User /p:*** ..."
  $script:Rdp = [System.Diagnostics.Process]::Start($psi)
  $script:RdpOut = $script:Rdp.StandardOutput.ReadToEndAsync()
  $script:RdpErr = $script:Rdp.StandardError.ReadToEndAsync()

  Wait-Until {
    if ($script:Rdp.HasExited) { Show-RdpLog; Fail "FreeRDP exited with code $($script:Rdp.ExitCode)" }
    [SplitPlayWts]::FindActive($User) -ge 0
  } 180 "an active RDP session for $User"
  $session = [SplitPlayWts]::FindActive($User)
  Write-Host "    session $session is active"
  # The desktop is ready once the shell runs in the session (first logon creates the profile).
  Wait-Until { @(Get-Process explorer -ErrorAction SilentlyContinue | Where-Object SessionId -eq $session).Count -gt 0 } 240 "explorer.exe in session $session"
  Start-Sleep -Seconds 5
  $session
}

function Show-RdpLog {
  if ($null -ne $script:Rdp -and $script:Rdp.HasExited) {
    Write-Host "::group::FreeRDP output"
    Write-Host $script:RdpOut.Result
    Write-Host $script:RdpErr.Result
    Write-Host '::endgroup::'
  }
  Get-ChildItem (Join-Path $WorkDir 'freerdp') -File -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "::group::$($_.FullName)"
    Get-Content $_.FullName -Tail 200 | Write-Host
    Write-Host '::endgroup::'
  }
}

# --- Launch as SYSTEM in the session --------------------------------------------------------------------------------

# Start $Program in session $Session through session-launch.ps1 run by a SYSTEM scheduled task.
# Returns the path of the result file that session-launch.ps1 writes when the program has exited.
function Start-InSession([string]$Name, [string]$Program, [string]$Arguments, [int]$TimeoutSec) {
  $dir = Join-Path $WorkDir 'launch'
  New-Item -ItemType Directory -Force $dir | Out-Null
  $request = Join-Path $dir "$Name.request.json"
  $result = Join-Path $dir "$Name.result.json"
  Remove-Item -Force "$result", "$result.pid" -ErrorAction SilentlyContinue
  @{ session = $script:Session; exe = $Program; args = $Arguments; cwd = (Split-Path $Program); timeoutSec = $TimeoutSec; result = $result } |
    ConvertTo-Json | Set-Content -Encoding utf8 $request

  $task = "SplitPlay-session-test-$Name"
  $action = New-ScheduledTaskAction -Execute $Pwsh -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$Launcher`" -Request `"$request`""
  $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
  $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 30) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
  Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings -Force | Out-Null
  $script:Tasks += $task
  Start-ScheduledTask -TaskName $task
  Write-Host "    [$Name] SYSTEM task started: $(Split-Path -Leaf $Program) $Arguments"

  Wait-Until { (Test-Path "$result.pid") -or (Test-Path $result) } 60 "$Name to start"
  if (Test-Path $result) {
    $r = Get-Content -Raw $result | ConvertFrom-Json
    if ($r.error) { Fail "[$Name] launch failed: $($r.error)" }
  }
  $started = Get-Content -Raw "$result.pid" | ConvertFrom-Json
  Write-Host "    [$Name] pid $($started.pid), launched by $($started.user)"
  $p = Get-Process -Id $started.pid -ErrorAction SilentlyContinue
  if ($p) { Write-Host "    [$Name] runs in session $($p.SessionId)"; Assert ($p.SessionId -eq $script:Session) "[$Name] runs in session $($p.SessionId), expected $($script:Session)" }
  $result
}

function Wait-Exit([string]$Result, [int]$TimeoutSec, [string]$Name) {
  Wait-Until { Test-Path $Result } $TimeoutSec "$Name to exit"
  $r = Get-Content -Raw $Result | ConvertFrom-Json
  if ($r.error) { Fail "[$Name] $($r.error)" }
  Write-Host "    [$Name] exited with code $($r.exitCode)"
  [int64]$r.exitCode
}

function Show-DeskLog([string]$DeskDir, [int]$Tail = 400) {
  $f = Join-Path $DeskDir 'sunshine.log'
  if (Test-Path $f) {
    Write-Host "::group::$f"
    Get-Content $f -Tail $Tail | Write-Host
    Write-Host '::endgroup::'
  }
}

# --- Control pipe (as in smoke-test.ps1) ------------------------------------------------------------------------------

function Read-Message([int]$TimeoutSec, [string]$What, [scriptblock]$Predicate) {
  $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
  while ($true) {
    $left = [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds
    if ($left -le 0) { Fail "timed out after $TimeoutSec s waiting for $What" }
    if ($null -eq $script:Pending) { $script:Pending = $script:Reader.ReadLineAsync() }
    if (-not $script:Pending.Wait([Math]::Min($left, 1000))) { continue }
    $line = $script:Pending.Result
    $script:Pending = $null
    if ($null -eq $line) { Fail "control pipe closed by the worker while waiting for $What" }
    $msg = $line | ConvertFrom-Json
    if ($msg.type -ne 'heartbeat') { Write-Host "    <- $line" }
    if (& $Predicate $msg) { return $msg }
  }
}

function Send-Message([string]$Json) {
  Write-Host "    -> $Json"
  $script:Writer.WriteLine($Json)
}

# --- Tests --------------------------------------------------------------------------------------------------------------

function Test-Probe {
  Write-Host '==> launch mechanism probe (PowerShell child)'
  $out = Join-Path $WorkDir 'probe.txt'
  $cmd = "`$p = [System.Diagnostics.Process]::GetCurrentProcess(); " +
    "'{0}|{1}|{2}' -f `$p.SessionId, [Security.Principal.WindowsIdentity]::GetCurrent().Name, " +
    "[Environment]::UserInteractive | Set-Content -Encoding utf8 '$out'"
  $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($cmd))
  $r = Start-InSession 'probe' $Pwsh "-NoProfile -NonInteractive -EncodedCommand $encoded" 60
  Assert ((Wait-Exit $r 90 'probe') -eq 0) 'the probe child failed'
  $line = (Get-Content -Raw $out).Trim()
  Write-Host "    probe: $line"
  $fields = $line -split '\|'
  Assert ([int]$fields[0] -eq $script:Session) "the probe ran in session $($fields[0]), expected $($script:Session)"
  Assert ($fields[1] -eq 'NT AUTHORITY\SYSTEM') "the probe ran as $($fields[1]), expected NT AUTHORITY\SYSTEM"
}

function Test-SelfTest {
  Write-Host "==> --self-test $SelfTestSeconds in session $($script:Session)"
  $deskDir = Join-Path $WorkDir 'desks\self-test'
  $r = Start-InSession 'self-test' $Exe "--desk-dir `"$deskDir`" --self-test $SelfTestSeconds min_log_level=debug" ($SelfTestSeconds + 240)
  $code = Wait-Exit $r ($SelfTestSeconds + 270) 'self-test'
  $jsonPath = Join-Path $deskDir 'self-test.json'
  if ($code -ne 0 -or -not (Test-Path $jsonPath)) {
    Show-DeskLog $deskDir 600
    Fail "self-test exited with code $code"
  }
  $t = Get-Content -Raw $jsonPath | ConvertFrom-Json
  Write-Host "    $(Get-Content -Raw $jsonPath)"
  $log = Get-Content -Raw (Join-Path $deskDir 'sunshine.log')
  Show-DeskLog $deskDir 150

  Assert ($t.session -eq $script:Session) "self-test ran in session $($t.session), expected $($script:Session)"
  Assert ($log -match "SplitPlay session\s+: $($script:Session)") 'the log does not show the capture banner for this session'
  Assert ($log -match 'Desktop resolution \[') 'the log does not show a DDA duplication of the session output'
  Assert ($t.capture.frames -gt 0) 'no frame was captured'
  Assert ($t.input.cursor_moved) 'the cursor did not follow SendInput'
  Write-Host "    encoder $($t.encoder), $($t.capture.frames) frame(s) at $($t.capture.width)x$($t.capture.height), cursor moved"
  $endpoints = @($log -split "`n" | Where-Object { $_ -match 'render endpoint\(s\) in session|SplitPlay:   ' })
  $endpoints | ForEach-Object { Write-Host "    endpoint: $($_.Trim())" }
  if ($RequireAudio) {
    Assert ($log -match 'SplitPlay:   \[default\] Remote Audio ') "the session's default render endpoint is not Remote Audio"
  }
  if ($t.audio.ok) {
    Write-Host "    audio: $($t.audio.packets) packet(s) from $($t.audio.endpoint)"
  } elseif ($RequireAudio) {
    Fail "no audio from the session's default endpoint: $($t.audio.error)"
  } else {
    Write-Host "::warning::session-test: no audio from the session's default endpoint: $($t.audio.error)"
  }
}

function Test-Ipc {
  Write-Host "==> control IPC in session $($script:Session)"
  $desk = 'ci-1'
  $pipeName = "splitplay\worker-$desk"
  $deskDir = Join-Path $WorkDir "desks\$desk"
  $server = [System.IO.Pipes.NamedPipeServerStream]::new($pipeName, [System.IO.Pipes.PipeDirection]::InOut, 1,
    [System.IO.Pipes.PipeTransmissionMode]::Byte, [System.IO.Pipes.PipeOptions]::Asynchronous)
  try {
    $r = Start-InSession 'ipc' $Exe "--desk-dir `"$deskDir`" --pipe \\.\pipe\$pipeName --desk $desk port=$BasePort" 600
    $connect = $server.WaitForConnectionAsync()
    Wait-Until { $connect.IsCompleted -or (Test-Path $r) } 60 'the worker to connect to the control pipe'
    if (-not $connect.IsCompleted) { Show-DeskLog $deskDir; Fail 'the worker exited before connecting' }
    $enc = [System.Text.UTF8Encoding]::new($false)
    $script:Reader = [System.IO.StreamReader]::new($server, $enc, $false, 4096, $true)
    $script:Writer = [System.IO.StreamWriter]::new($server, $enc, 4096, $true)
    $script:Writer.NewLine = "`n"
    $script:Writer.AutoFlush = $true
    $script:Pending = $null

    $hello = Read-Message 30 'hello' { param($m) $true }
    Assert ($hello.type -eq 'hello' -and $hello.role -eq 'worker') 'first message is not a worker hello'
    Assert ($hello.session -eq $script:Session) "hello.session is $($hello.session), expected $($script:Session)"
    Send-Message '{"type":"welcome","protocol":1,"profile":{"fps":60,"width":1280,"height":720}}'
    $ready = Read-Message 240 'ready' { param($m) $m.type -eq 'ready' }
    Assert ($ready.ports.base -eq $BasePort) "ready.ports.base is $($ready.ports.base)"

    $log = Get-Content -Raw (Join-Path $deskDir 'sunshine.log')
    Assert ($log -match "SplitPlay session\s+: $($script:Session)") 'the encoder probe did not open the session output'
    Assert ($log -match 'Found H.264 encoder') 'no encoder was found in the session'
    Assert ($log -match 'libdisplaydevice is not used') 'libdisplaydevice was not disabled'
    ($log -split "`n" | Select-String 'Found .* encoder|Device Description|Output Name|Desktop resolution|Adapter LUID' | Select-Object -First 8) |
      ForEach-Object { Write-Host "    log: $($_.Line.Trim())" }

    Send-Message '{"type":"shutdown","id":1}'
    $null = Read-Message 15 'the shutdown result' { param($m) $m.type -eq 'result' -and $m.id -eq 1 }
    $code = Wait-Exit $r 30 'ipc'
    Assert ($code -eq 0) "exit code after shutdown is $code, expected 0"
  } catch {
    Show-DeskLog $deskDir
    throw
  } finally {
    $server.Dispose()
  }
}

# --- Main ---------------------------------------------------------------------------------------------------------------

Remove-Item -Recurse -Force $WorkDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $WorkDir | Out-Null
if (-not $ProbeOnly) {
  $Exe = (Resolve-Path $Exe).Path
  Write-Host "worker: $Exe"
}
Write-Host "runner: $([Security.Principal.WindowsIdentity]::GetCurrent().Name) in session $((Get-Process -Id $PID).SessionId)"

try {
  Write-Host '==> RDP host'
  $port = Initialize-RdpHost
  Write-Host "==> user $User"
  $password = New-DeskUser
  Write-Host '==> loopback RDP logon'
  $script:Session = Connect-Session $port $password
  [SplitPlayWts]::List() | ForEach-Object { Write-Host "    session: $_" }

  Test-Probe
  if (-not $ProbeOnly) {
    Test-SelfTest
    Test-Ipc
  }
  Write-Host 'session test passed'
} catch {
  Show-RdpLog
  [SplitPlayWts]::List() | ForEach-Object { Write-Host "    session: $_" }
  throw
} finally {
  foreach ($t in $script:Tasks) { Unregister-ScheduledTask -TaskName $t -Confirm:$false -ErrorAction SilentlyContinue }
  if ($script:Session -ge 0) { & logoff.exe $script:Session 2>$null }
  if ($null -ne $script:Rdp -and -not $script:Rdp.HasExited) { $script:Rdp.Kill($true) }
}
