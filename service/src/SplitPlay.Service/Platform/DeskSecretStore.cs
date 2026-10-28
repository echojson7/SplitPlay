// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace SplitPlay.Service.Platform;

/// <summary>Encrypts secrets at rest.</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plain, string purpose);

    byte[] Unprotect(byte[] cipher, string purpose);
}

/// <summary>
/// Desk passwords in <c>%ProgramData%\SplitPlay\secrets\desks\&lt;id&gt;.bin</c>, protected by
/// <see cref="ISecretProtector"/> (DPAPI LocalMachine in production).
/// </summary>
public sealed class DeskSecretStore(string dataRoot, ISecretProtector protector)
{
    public string Directory { get; } = Path.Combine(dataRoot, "secrets", "desks");

    public string PathOf(string deskId) => Path.Combine(Directory, deskId + ".bin");

    /// <summary>The stored password, or null if there is none or it cannot be decrypted (then it is rotated).</summary>
    public string? Read(string deskId)
    {
        var path = PathOf(deskId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(protector.Unprotect(File.ReadAllBytes(path), Purpose(deskId)));
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    public void Write(string deskId, string password)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathOf(deskId);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, protector.Protect(Encoding.UTF8.GetBytes(password), Purpose(deskId)));
        File.Move(temp, path, overwrite: true);
    }

    public bool Delete(string deskId)
    {
        var path = PathOf(deskId);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private static string Purpose(string deskId) => "SplitPlay.desk." + deskId;
}

/// <summary>DPAPI with <c>CRYPTPROTECT_LOCAL_MACHINE</c>: only code running on this machine can decrypt.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiMachineProtector : ISecretProtector
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
    private const int CRYPTPROTECT_LOCAL_MACHINE = 0x4;

    public byte[] Protect(byte[] plain, string purpose) => Transform(plain, purpose, protect: true);

    public byte[] Unprotect(byte[] cipher, string purpose) => Transform(cipher, purpose, protect: false);

    private static unsafe byte[] Transform(byte[] input, string purpose, bool protect)
    {
        var entropy = Encoding.UTF8.GetBytes(purpose);
        fixed (byte* pIn = input)
        fixed (byte* pEntropy = entropy)
        {
            var inBlob = new DataBlob { cbData = input.Length, pbData = (IntPtr)pIn };
            var entropyBlob = new DataBlob { cbData = entropy.Length, pbData = (IntPtr)pEntropy };
            DataBlob output;
            var ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN | CRYPTPROTECT_LOCAL_MACHINE, out output)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), protect ? "CryptProtectData" : "CryptUnprotectData");
            }

            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);
}

/// <summary>
/// Test and non-Windows stand-in: Base64 with a marker, no protection. Never used by <see cref="WindowsDeskPlatform"/>.
/// </summary>
public sealed class PlainTestProtector : ISecretProtector
{
    private const string Marker = "plain-test:";

    public byte[] Protect(byte[] plain, string purpose) => Encoding.UTF8.GetBytes(Marker + purpose + ":" + Convert.ToBase64String(plain));

    public byte[] Unprotect(byte[] cipher, string purpose)
    {
        var text = Encoding.UTF8.GetString(cipher);
        var prefix = Marker + purpose + ":";
        return text.StartsWith(prefix, StringComparison.Ordinal)
            ? Convert.FromBase64String(text[prefix.Length..])
            : throw new FormatException("not a test secret for this purpose");
    }
}
