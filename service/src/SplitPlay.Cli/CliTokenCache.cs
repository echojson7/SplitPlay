// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using SplitPlay.Core;
using SplitPlay.Core.Ipc;

namespace SplitPlay.Cli;

/// <summary>
/// Where and how the CLI caches its admin-pipe token: <c>%LOCALAPPDATA%\SplitPlay\cli-token-s{session}.bin</c>,
/// DPAPI-encrypted for the current user, with a medium mandatory label that forbids reading from below (a
/// low-integrity process of the owner, such as a sandboxed browser, can otherwise read and decrypt it). Medium, not
/// high: the owner's non-elevated CLI must read it, and the pipe already hands medium-integrity console processes a
/// token. Only at the console session: there the pipe hands a token to the owner's processes whether elevated or not,
/// so sharing one between them gives nothing away. Elsewhere (an elevated prompt in
/// another session) a non-elevated process of the same user could read what only the elevated one may have, so there
/// is no cache; use <c>SPLITPLAY_TOKEN</c> instead.
/// </summary>
internal static partial class CliTokenCache
{
    /// <summary>The cache for this process, or null when it may not have one.</summary>
    public static AdminTokenCache? Open()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var session = Process.GetCurrentProcess().SessionId;
        var console = WTSGetActiveConsoleSessionId();
        if (console == uint.MaxValue || session != (int)console)
        {
            return null;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
        {
            return null;
        }

        var path = Path.Combine(local, BuildInfo.ServiceName, string.Create(CultureInfo.InvariantCulture, $"cli-token-s{session}.bin"));
        return new AdminTokenCache(path, Protect, Unprotect, TimeProvider.System, LabelMediumNoReadUp);
    }

    /// <summary>Mandatory label: medium integrity, no read, write or execute up.</summary>
    private const string MediumNoReadUp = "S:(ML;;NRNWNX;;;ME)";

    private static void LabelMediumNoReadUp(string file)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(MediumNoReadUp, 1, out var descriptor, 0))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            if (!GetSecurityDescriptorSacl(descriptor, out var present, out var sacl, out _) || !present)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            }

            var error = SetNamedSecurityInfoW(file, SeFileObject, LabelSecurityInformation, 0, 0, 0, sacl);
            if (error != 0)
            {
                throw new System.ComponentModel.Win32Exception((int)error);
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private const int SeFileObject = 1;
    private const uint LabelSecurityInformation = 0x10;

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out nint descriptor, nint size);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSecurityDescriptorSacl(nint descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present, out nint sacl,
        [MarshalAs(UnmanagedType.Bool)] out bool defaulted);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint SetNamedSecurityInfoW(string name, int type, uint info, nint owner, nint group, nint dacl, nint sacl);

    private static byte[] Protect(byte[] data) => Crypt(data, protect: true);

    private static byte[] Unprotect(byte[] data) => Crypt(data, protect: false);

    private static unsafe byte[] Crypt(byte[] data, bool protect)
    {
        fixed (byte* p = data)
        {
            var input = new DataBlob { Size = (uint)data.Length, Data = (nint)p };
            var ok = protect
                ? CryptProtectData(in input, 0, 0, 0, 0, CryptProtectUiForbidden, out var output)
                : CryptUnprotectData(in input, 0, 0, 0, 0, CryptProtectUiForbidden, out output);
            if (!ok)
            {
                throw new System.Security.Cryptography.CryptographicException(Marshal.GetLastPInvokeError());
            }

            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
    }

    private const uint CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public uint Size;
        public nint Data;
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint WTSGetActiveConsoleSessionId();

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(in DataBlob dataIn, nint description, nint entropy, nint reserved, nint prompt, uint flags, out DataBlob dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(in DataBlob dataIn, nint description, nint entropy, nint reserved, nint prompt, uint flags, out DataBlob dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
