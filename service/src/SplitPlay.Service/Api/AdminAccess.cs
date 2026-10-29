// SPDX-License-Identifier: MIT
using System.IO.Pipes;

namespace SplitPlay.Service.Api;

/// <summary>Settings of the <c>Auth</c> configuration section.</summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>Serve the admin pipe (<see cref="AdminPipeServer"/>).</summary>
    public bool AdminPipe { get; set; } = true;

    /// <summary>Refuse Web API requests whose TCP peer is a process in a desk session (<see cref="IPeerInspector"/>).</summary>
    public bool PeerCheck { get; set; } = true;
}

/// <summary>Whether a caller of the admin pipe may get a credential, and who it is (for the log).</summary>
public sealed record AdminCallerDecision(bool Allowed, string Caller, string? Reason = null);

/// <summary>Decides whether the client of an admin pipe connection is the host's administrator.</summary>
public interface IAdminCallerCheck
{
    /// <summary>Called after the request line was read (Windows can only identify a pipe client once it has written).</summary>
    AdminCallerDecision Check(NamedPipeServerStream pipe);
}

/// <summary>The Windows session of the process at the other end of a Web API connection.</summary>
public interface IPeerInspector
{
    /// <summary>The peer's session id, or null when it cannot be told (not loopback, not Windows, process gone).</summary>
    int? PeerSession(HttpContext context);
}

/// <summary>Tests and non-Windows hosts: every pipe caller is the admin unless a test says otherwise.</summary>
public sealed class FakeAdminCallerCheck : IAdminCallerCheck
{
    /// <summary>When set, callers are refused with this reason.</summary>
    public string? Refuse { get; set; }

    public AdminCallerDecision Check(NamedPipeServerStream pipe) =>
        Refuse is { } reason ? new AdminCallerDecision(false, "test caller", reason) : new AdminCallerDecision(true, "test caller");
}

/// <summary>Tests and non-Windows hosts: the peer's session is whatever the test sets (null: unknown).</summary>
public sealed class FakePeerInspector : IPeerInspector
{
    public int? Session { get; set; }

    public int? PeerSession(HttpContext context) => Session;
}
