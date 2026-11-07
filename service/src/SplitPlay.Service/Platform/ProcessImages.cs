// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace SplitPlay.Service.Platform;

/// <summary>Which program a process runs.</summary>
public interface IProcessImages
{
    /// <summary>The full path of the process's executable, or null when it is gone or cannot be opened.</summary>
    string? PathOf(int pid);

    /// <summary>%WINDIR%, whose programs are never games (null: unknown).</summary>
    string? WindowsDirectory { get; }
}

/// <summary>For tests and the fake platform: paths set by hand.</summary>
public sealed class FakeProcessImages : IProcessImages
{
    public ConcurrentDictionary<int, string> Paths { get; } = new();

    public string? WindowsDirectory => @"C:\Windows";

    public string? PathOf(int pid) => Paths.TryGetValue(pid, out var path) ? path : null;
}

/// <summary><c>QueryFullProcessImageNameW</c> with limited query rights (enough for any process from LocalSystem).</summary>
public sealed class WindowsProcessImages : IProcessImages
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public string? WindowsDirectory { get; } = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public string? PathOf(int pid)
    {
        if (pid <= 4)
        {
            return null;
        }

        var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
