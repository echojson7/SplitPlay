# SPDX-License-Identifier: GPL-3.0-only
#
# Smoke test for splitplay-worker.exe (PowerShell 7, Windows).
#
#   pwsh worker/scripts/smoke-test.ps1 [-Exe <path to splitplay-worker.exe>] [-WorkDir <scratch dir>]
#
# 1. --version exits 0 and names SplitPlay; --help exits 0 and lists the SplitPlay and Sunshine options.
# 2. Control IPC (docs/contracts/ipc.md) driven the way the service drives it:
#    hello -> welcome -> heartbeats -> ready -> requests -> shutdown -> exit code 0.
# 3. Losing the pipe makes the worker exit on its own with code 3 (it never runs unsupervised).
param(
  [string]$Exe = (Join-Path $PSScriptRoot '..\out\splitplay-worker.exe'),
  [string]$WorkDir = (Join-Path ([System.IO.Path]::GetTempPath()) 'splitplay-worker-smoke')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Exe = (Resolve-Path $Exe).Path
$BasePort = 48089
$script:Current = $null
$script:Pending = $null
$script:Reader = $null
$script:Writer = $null
$script:Heartbeats = 0

function Fail([string]$Message) {
  Write-Host "::error::smoke-test: $Message"
  throw $Message
}

function Assert([bool]$Condition, [string]$Message) {
  if (-not $Condition) { Fail $Message }
}

function New-StartInfo([string[]]$Arguments) {
  $psi = [System.Diagnostics.ProcessStartInfo]::new($Exe)
  foreach ($a in $Arguments) { $psi.ArgumentList.Add($a) }
  $psi.UseShellExecute = $false
  $psi.WorkingDirectory = $WorkDir
  $psi
}

# Run to completion and capture the output.
function Invoke-Worker([string[]]$Arguments, [int]$TimeoutMs = 60000) {
  $psi = New-StartInfo $Arguments
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $proc = [System.Diagnostics.Process]::Start($psi)
  $stdout = $proc.StandardOutput.ReadToEndAsync()
  $stderr = $proc.StandardError.ReadToEndAsync()
  if (-not $proc.WaitForExit($TimeoutMs)) {
    $proc.Kill($true)
    Fail "splitplay-worker $($Arguments -join ' ') did not exit within $($TimeoutMs / 1000) s"
  }
  $proc.WaitForExit()
  [pscustomobject]@{ ExitCode = $proc.ExitCode; Output = $stdout.Result + $stderr.Result }
}

# Start supervised; the worker's console log goes straight to this console.
function Start-Worker([string]$PipeName, [string]$Desk, [string]$DeskDir) {
  $psi = New-StartInfo @('--pipe', "\\.\pipe\$PipeName", '--desk', $Desk, '--desk-dir', $DeskDir, "port=$BasePort")
  [System.Diagnostics.Process]::Start($psi)
}

function Open-Channel([string]$PipeName, [System.Diagnostics.Process]$Proc, [System.IO.Pipes.NamedPipeServerStream]$Server) {
  $task = $Server.WaitForConnectionAsync()
  $deadline = [DateTime]::UtcNow.AddSeconds(30)
  while (-not $task.Wait(250)) {
    if ($Proc.HasExited) { Fail "worker exited with code $($Proc.ExitCode) before connecting" }
    if ([DateTime]::UtcNow -gt $deadline) { Fail 'worker did not connect to the control pipe within 30 s' }
  }
  $enc = [System.Text.UTF8Encoding]::new($false)
  $script:Reader = [System.IO.StreamReader]::new($Server, $enc, $false, 4096, $true)
  $script:Writer = [System.IO.StreamWriter]::new($Server, $enc, 4096, $true)
  $script:Writer.NewLine = "`n"
  $script:Writer.AutoFlush = $true
  $script:Pending = $null
}

function Send-Message([string]$Json) {
  Write-Host "    -> $Json"
  $script:Writer.WriteLine($Json)
}

# Read messages until $Predicate matches one and return it. A read that is still pending when the
# per-iteration wait expires is kept and picked up by the next iteration.
function Wait-Message([scriptblock]$Predicate, [int]$TimeoutSec, [string]$What) {
  $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
  while ($true) {
    $left = [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds
    if ($left -le 0) { Fail "timed out after $TimeoutSec s waiting for $What" }
    if ($null -eq $script:Pending) { $script:Pending = $script:Reader.ReadLineAsync() }
    if (-not $script:Pending.Wait([Math]::Min($left, 1000))) { continue }
    $line = $script:Pending.Result
    $script:Pending = $null
    if ($null -eq $line) { Fail "control pipe closed by the worker while waiting for $What" }
    Write-Host "    <- $line"
    $msg = $line | ConvertFrom-Json
    if ($msg.type -eq 'heartbeat') { $script:Heartbeats++ }
    if (& $Predicate $msg) { return $msg }
  }
}

function New-PipeServer([string]$PipeName) {
  [System.IO.Pipes.NamedPipeServerStream]::new($PipeName, [System.IO.Pipes.PipeDirection]::InOut, 1,
    [System.IO.Pipes.PipeTransmissionMode]::Byte, [System.IO.Pipes.PipeOptions]::Asynchronous)
}

function Show-Log([string]$DeskDir) {
  $f = Join-Path $DeskDir 'sunshine.log'
  if (Test-Path $f) {
    Write-Host "::group::$f"
    Get-Content $f -Tail 300 | Write-Host
    Write-Host '::endgroup::'
  }
}

# Start a supervised worker and take it to "ready". Returns the process and the pipe server.
function Start-ReadyWorker([string]$Desk) {
  $pipeName = "splitplay\worker-$Desk"
  $deskDir = Join-Path $WorkDir "desks\$Desk"
  $server = New-PipeServer $pipeName
  $proc = Start-Worker $pipeName $Desk $deskDir
  $script:Current = @{ Proc = $proc; Server = $server; DeskDir = $deskDir }
  Open-Channel $pipeName $proc $server

  $hello = Wait-Message { param($m) $true } 30 'hello'
  Assert ($hello.type -eq 'hello') "first message is '$($hello.type)', expected hello"
  Assert ($hello.protocol -eq 1) 'hello.protocol is not 1'
  Assert ($hello.role -eq 'worker') 'hello.role is not worker'
  Assert ($hello.desk -eq $Desk) "hello.desk is '$($hello.desk)'"
  Assert ($hello.pid -eq $proc.Id) "hello.pid $($hello.pid) is not the worker pid $($proc.Id)"
  Assert (-not [string]::IsNullOrEmpty($hello.version)) 'hello.version is empty'
  Assert ($hello.session -is [long] -or $hello.session -is [int]) 'hello.session is not an integer'

  Send-Message '{"type":"welcome","protocol":1,"profile":{"fps":60,"width":1280,"height":720}}'

  $script:Heartbeats = 0
  $started = [DateTime]::UtcNow
  $ready = Wait-Message { param($m) $m.type -eq 'ready' } 180 'ready'
  Write-Host ("    ready after {0:n1} s, {1} heartbeat(s) before it" -f ([DateTime]::UtcNow - $started).TotalSeconds, $script:Heartbeats)
  Assert ($ready.ports.base -eq $BasePort) "ready.ports.base is $($ready.ports.base), expected $BasePort"
  $script:Current
}

function Stop-Current {
  if ($null -ne $script:Current) {
    if (-not $script:Current.Proc.HasExited) { $script:Current.Proc.Kill($true) }
    $script:Current.Server.Dispose()
  }
}

Remove-Item -Recurse -Force $WorkDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $WorkDir | Out-Null
Write-Host "worker: $Exe"

# 1. Command line
Write-Host '==> --version'
$r = Invoke-Worker @('--version')
Write-Host $r.Output
Assert ($r.ExitCode -eq 0) "--version exited with $($r.ExitCode)"
Assert ($r.Output -match 'splitplay-worker \S+ \(SplitPlay') '--version output does not name SplitPlay'

Write-Host '==> --help'
$r = Invoke-Worker @('--help')
Write-Host $r.Output
Assert ($r.ExitCode -eq 0) "--help exited with $($r.ExitCode)"
foreach ($needle in @('--desk-dir', '--pipe', '--desk', '--self-test', 'Usage:')) {
  Assert ($r.Output.Contains($needle)) "--help output lacks $needle"
}

Write-Host '==> invalid arguments'
$r = Invoke-Worker @('--pipe', '\\.\pipe\splitplay\worker-x')
Write-Host $r.Output
Assert ($r.ExitCode -eq 2) "--pipe without --desk exited with $($r.ExitCode), expected 2"
foreach ($bad in @(@('--self-test', '0'), @('--self-test=abc'), @('--pipe', '\\.\pipe\splitplay\worker-x', '--desk', 'x', '--self-test', '5'))) {
  $r = Invoke-Worker $bad
  Write-Host $r.Output
  Assert ($r.ExitCode -eq 2) "$($bad -join ' ') exited with $($r.ExitCode), expected 2"
}
Assert (-not (Test-Path (Join-Path (Split-Path $Exe) 'config'))) 'the command-line checks created a config directory next to the exe'

# 2. Lifecycle
Write-Host '==> control IPC lifecycle'
try {
  $w = Start-ReadyWorker 'smoke-1'
  Assert (Test-Path (Join-Path $w.DeskDir 'sunshine.conf')) 'sunshine.conf was not created in --desk-dir'
  Assert (Test-Path (Join-Path $w.DeskDir 'credentials')) 'credentials/ was not created in --desk-dir'

  $hb = Wait-Message { param($m) $m.type -eq 'heartbeat' } 3 'a heartbeat after ready'
  Assert ($hb.state -eq 'idle') "heartbeat.state is '$($hb.state)', expected idle"
  Assert ($hb.metrics.clients -eq 0) 'heartbeat.metrics.clients is not 0'
  $hb2 = Wait-Message { param($m) $m.type -eq 'heartbeat' } 3 'a second heartbeat'
  Assert ($hb2.seq -gt $hb.seq) 'heartbeat.seq does not increase'

  Send-Message '{"type":"list_clients","id":1}'
  $res = Wait-Message { param($m) $m.type -eq 'result' -and $m.id -eq 1 } 10 'the list_clients result'
  Assert ($res.ok -eq $true) 'list_clients failed'
  Assert (@($res.data.clients).Count -eq 0) 'a fresh desk has paired clients'

  Send-Message '{"type":"no_such_request","id":2}'
  $res = Wait-Message { param($m) $m.type -eq 'result' -and $m.id -eq 2 } 10 'the unknown_type result'
  Assert ($res.ok -eq $false -and $res.error.code -eq 'unknown_type') 'an unknown request was not answered with unknown_type'

  Send-Message '{"type":"send_sas","id":3}'
  $res = Wait-Message { param($m) $m.type -eq 'result' -and $m.id -eq 3 } 10 'the send_sas result'
  Assert ($res.ok -eq $false -and $res.error.code -eq 'unsupported') 'send_sas was not answered with unsupported'

  Send-Message '{"type":"shutdown","id":4}'
  $sent = [DateTime]::UtcNow
  $res = Wait-Message { param($m) $m.type -eq 'result' -and $m.id -eq 4 } 10 'the shutdown result'
  Assert ($res.ok -eq $true) 'shutdown was not acknowledged'
  Assert ($w.Proc.WaitForExit(30000)) 'worker did not exit within 30 s of shutdown'
  $w.Proc.WaitForExit()
  $elapsed = ([DateTime]::UtcNow - $sent).TotalSeconds
  Write-Host ("    exited with code {0} {1:n1} s after shutdown" -f $w.Proc.ExitCode, $elapsed)
  if ($elapsed -gt 5) { Write-Host "::warning::worker took $([Math]::Round($elapsed, 1)) s to exit after shutdown; the contract allows 5 s" }
  Assert ($w.Proc.ExitCode -eq 0) "exit code after shutdown is $($w.Proc.ExitCode), expected 0"
} catch {
  if ($null -ne $script:Current) { Show-Log $script:Current.DeskDir }
  throw
} finally {
  Stop-Current
}

# 3. Pipe loss
Write-Host '==> pipe loss'
try {
  $w = Start-ReadyWorker 'smoke-2'
  $w.Server.Dispose()
  Assert ($w.Proc.WaitForExit(30000)) 'worker kept running after the control pipe closed'
  $w.Proc.WaitForExit()
  Write-Host "    exited with code $($w.Proc.ExitCode)"
  Assert ($w.Proc.ExitCode -eq 3) "exit code after pipe loss is $($w.Proc.ExitCode), expected 3"
} catch {
  if ($null -ne $script:Current) { Show-Log $script:Current.DeskDir }
  throw
} finally {
  Stop-Current
}

Write-Host 'smoke test passed'
