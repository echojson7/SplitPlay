// SPDX-License-Identifier: MIT
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SplitPlay.Service.Supervision;

/// <summary>Creates the per-child pipe server (docs/contracts/ipc.md "Transport").</summary>
public static class PipeServerFactory
{
    /// <summary>
    /// Creates a single-instance, byte-mode, asynchronous pipe server named <paramref name="localName"/>.
    /// On Windows the DACL allows only SYSTEM; when the service itself runs as another account (console mode, CI)
    /// that account is added so that the children it starts can connect.
    /// </summary>
    public static NamedPipeServerStream Create(string localName)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateWindows(localName);
        }

        return new NamedPipeServerStream(localName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateWindows(string localName)
    {
        var security = new PipeSecurity();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));

        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is { } self && !self.Equals(system))
        {
            security.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        // FirstPipeInstance: fail instead of joining a pipe someone else created under our name (squatting).
        return NamedPipeServerStreamAcl.Create(localName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
    }
}
