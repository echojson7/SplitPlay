// SPDX-License-Identifier: MIT
using System.Reflection;

namespace SplitPlay.Core;

/// <summary>Well-known names and the product version shared by all .NET components.</summary>
public static class BuildInfo
{
    /// <summary>Product version in <c>major.minor.patch</c> form, taken from the assembly.</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>Windows service name (docs/contracts/README.md).</summary>
    public const string ServiceName = "SplitPlay";

    /// <summary>Windows service display name (docs/contracts/README.md).</summary>
    public const string ServiceDisplayName = "SplitPlay Host Service";

    /// <summary>Default Web API port (docs/contracts/web-api.md).</summary>
    public const int DefaultWebPort = 38299;

    /// <summary>Web API path prefix.</summary>
    public const string ApiPrefix = "/api/v1";

    private static string ReadVersion()
    {
        var info = typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info))
        {
            return "0.0.0";
        }

        // Strip the "+<commit>" suffix the SDK appends for source link.
        var plus = info.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? info[..plus] : info;
    }
}
