# SPDX-License-Identifier: GPL-3.0-only
#
# Test helper: start a program the way SplitPlay.Service starts a desk worker (PowerShell 7, Windows, run as SYSTEM).
#
#   pwsh session-launch.ps1 -Request <request.json>
#
# request.json: {"session": N, "exe": "C:\\...\\splitplay-worker.exe", "args": "--desk-dir ... port=48189",
#                "cwd": "C:\\...", "timeoutSec": 120, "result": "C:\\...\\result.json"}
#
# The program gets a duplicate of this process's SYSTEM token with TokenSessionId = N, lpDesktop winsta0\default,
# CREATE_NO_WINDOW, and its own job object. This script waits for it (killing it after timeoutSec) and writes
# {"pid":..., "exitCode":..., "error":...} to the result file. session-test.ps1 runs it through a scheduled task,
# because the CI runner itself is an administrator, not SYSTEM.
param(
  [Parameter(Mandatory)] [string]$Request
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class SplitPlaySessionLauncher {
  const uint MAXIMUM_ALLOWED = 0x02000000;
  const int SecurityImpersonation = 2;
  const int TokenPrimary = 1;
  const int TokenSessionId = 12;
  const uint CREATE_SUSPENDED = 0x00000004;
  const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
  const uint CREATE_NO_WINDOW = 0x08000000;
  const uint WAIT_OBJECT_0 = 0;

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  struct STARTUPINFO {
    public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
    public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
    public short wShowWindow, cbReserved2;
    public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
  }

  [StructLayout(LayoutKind.Sequential)]
  struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

  [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
  [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(IntPtr h);
  [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr h, uint ms);
  [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
  [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr h, uint code);
  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObjectW(IntPtr attrs, string name);
  [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
  [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
  [DllImport("advapi32.dll", SetLastError = true)]
  static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attrs, int level, int type, out IntPtr newToken);
  [DllImport("advapi32.dll", SetLastError = true)]
  static extern bool SetTokenInformation(IntPtr token, int cls, ref uint value, uint length);
  [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  static extern bool CreateProcessAsUserW(IntPtr token, string app, StringBuilder cmdLine, IntPtr pa, IntPtr ta, bool inherit,
                                          uint flags, IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

  static Exception Win32(string what) { return new Win32Exception(Marshal.GetLastWin32Error(), what); }

  // Starts exe in `session`, reports the pid, waits up to timeoutMs (then kills it) and returns the exit code.
  public static uint Run(uint session, string exe, string args, string cwd, uint timeoutMs, Action<int> started) {
    IntPtr own, token = IntPtr.Zero, job = IntPtr.Zero;
    if (!OpenProcessToken(GetCurrentProcess(), MAXIMUM_ALLOWED, out own)) throw Win32("OpenProcessToken");
    try {
      if (!DuplicateTokenEx(own, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out token)) throw Win32("DuplicateTokenEx");
      if (!SetTokenInformation(token, TokenSessionId, ref session, 4)) throw Win32("SetTokenInformation(TokenSessionId)");

      var si = new STARTUPINFO();
      si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
      si.lpDesktop = "winsta0\\default";
      var cmd = new StringBuilder("\"" + exe + "\" " + args);
      PROCESS_INFORMATION pi;
      if (!CreateProcessAsUserW(token, exe, cmd, IntPtr.Zero, IntPtr.Zero, false,
                                CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, IntPtr.Zero, cwd, ref si, out pi)) {
        throw Win32("CreateProcessAsUser");
      }
      try {
        job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero || !AssignProcessToJobObject(job, pi.hProcess)) throw Win32("job object");
        ResumeThread(pi.hThread);
        started(pi.dwProcessId);
        if (WaitForSingleObject(pi.hProcess, timeoutMs) != WAIT_OBJECT_0) {
          TerminateProcess(pi.hProcess, 0xDEAD);
          WaitForSingleObject(pi.hProcess, 10000);
        }
        uint code;
        GetExitCodeProcess(pi.hProcess, out code);
        return code;
      } finally {
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        if (job != IntPtr.Zero) CloseHandle(job);
      }
    } finally {
      if (token != IntPtr.Zero) CloseHandle(token);
      CloseHandle(own);
    }
  }
}
'@

# Written to a temporary name and renamed, so the reader never sees a partial file.
function Write-Json([object]$Value, [string]$Path) {
  ($Value | ConvertTo-Json -Compress) | Set-Content -Encoding utf8 "$Path.tmp"
  Move-Item -Force "$Path.tmp" $Path
}

$req = Get-Content -Raw $Request | ConvertFrom-Json
$result = [ordered]@{ pid = $null; exitCode = $null; error = $null; user = [Security.Principal.WindowsIdentity]::GetCurrent().Name }
try {
  $code = [SplitPlaySessionLauncher]::Run([uint32]$req.session, $req.exe, $req.args, $req.cwd, [uint32]($req.timeoutSec * 1000),
    [Action[int]] {
      param($procId)
      $result.pid = $procId
      Write-Json $result "$($req.result).pid"
    })
  $result.exitCode = [int64]$code
} catch {
  $result.error = $_.Exception.Message
}
Write-Json $result $req.result
