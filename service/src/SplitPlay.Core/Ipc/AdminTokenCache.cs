// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text;

namespace SplitPlay.Core.Ipc;

/// <summary>
/// The CLI's per-user cache of its admin-pipe bearer token, so that a script running <c>splitplay</c> in a loop does not
/// ask the pipe (at most 30 requests a minute per session) every time. The file holds the token and its expiry,
/// encrypted by <paramref name="protect"/> (DPAPI for the current user on Windows); a token with less than
/// <see cref="MinRemaining"/> left is not used. Any problem reading or writing it just means no cache.
/// </summary>
public sealed class AdminTokenCache(string path, Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect, TimeProvider time)
{
    /// <summary>A cached token is used only while at least this much of its lifetime is left.</summary>
    public static readonly TimeSpan MinRemaining = TimeSpan.FromMinutes(1);

    public string Path => path;

    /// <summary>The cached token, or null (none, expired, unreadable).</summary>
    public string? Read()
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var text = Encoding.UTF8.GetString(unprotect(File.ReadAllBytes(path)));
            var newline = text.IndexOf('\n', StringComparison.Ordinal);
            if (newline <= 0 || !long.TryParse(text.AsSpan(0, newline), NumberStyles.None, CultureInfo.InvariantCulture, out var expires))
            {
                return null;
            }

            var token = text[(newline + 1)..];
            return token.Length > 0 && DateTimeOffset.FromUnixTimeSeconds(expires) - time.GetUtcNow() >= MinRemaining ? token : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException
                                       or ArgumentException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Stores <paramref name="token"/> (written to a temporary file, then moved over the old one).</summary>
    public void Write(AdminToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
            var text = token.ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + "\n" + token.Token;
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, protect(Encoding.UTF8.GetBytes(text)));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // No cache then.
        }
    }

    /// <summary>Forgets the cached token (the service no longer accepts it).</summary>
    public void Clear()
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to do.
        }
    }
}
