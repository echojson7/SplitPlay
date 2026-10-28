// SPDX-License-Identifier: MIT
using SplitPlay.Core.Configuration;

namespace SplitPlay.Service.Hosting;

/// <summary>Locations under the data root (docs/contracts/README.md).</summary>
public sealed class DataPaths
{
    public DataPaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public string ConfigFile => Path.Combine(Root, ConfigStore.FileName);

    public string DesksDirectory => Path.Combine(Root, "desks");

    public string LogsDirectory => Path.Combine(Root, "logs");

    public string DeskDirectory(string deskId) => Path.Combine(DesksDirectory, deskId);

    /// <summary>Creates the directory tree. ACL hardening (SYSTEM + Administrators only) is done by the installer.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(DesksDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
