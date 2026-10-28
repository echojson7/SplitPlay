// SPDX-License-Identifier: MIT
namespace SplitPlay.Core.Ipc;

/// <summary>Pipe naming per docs/contracts/README.md: <c>\\.\pipe\splitplay\&lt;role&gt;-&lt;desk-id&gt;</c>.</summary>
public static class PipeNames
{
    public const string WindowsPrefix = @"\\.\pipe\";

    /// <summary>Default namespace inside the pipe file system.</summary>
    public const string DefaultNamespace = @"splitplay\";

    /// <summary>
    /// The name used with <c>NamedPipeServerStream</c>/<c>NamedPipeClientStream</c> (without <c>\\.\pipe\</c>).
    /// </summary>
    public static string Local(string role, string deskId, string ns = DefaultNamespace) => $"{ns}{role}-{deskId}";

    /// <summary>The full name passed to children with <c>--pipe</c>.</summary>
    public static string Full(string role, string deskId, string ns = DefaultNamespace) => WindowsPrefix + Local(role, deskId, ns);

    /// <summary>Strips a leading <c>\\.\pipe\</c> so that a full name can be opened with .NET pipe streams.</summary>
    public static string ToLocal(string name) =>
        name.StartsWith(WindowsPrefix, StringComparison.OrdinalIgnoreCase) ? name[WindowsPrefix.Length..] : name;
}
