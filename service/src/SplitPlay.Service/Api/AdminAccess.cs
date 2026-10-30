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
/// <param name="Session">The caller's Windows session, when known (rate limiting).</param>
public sealed record AdminCallerDecision(bool Allowed, string Caller, string? Reason = null, int? Session = null);

/// <summary>What the admin rules need to know about a pipe caller.</summary>
/// <param name="IntegrityRid">Mandatory integrity level RID (0x2000 medium, 0x3000 high, 0x4000 system); -1 when unknown.</param>
/// <param name="DeskSession">The caller's session is a desk session (<c>DeskSupervisor.IsDeskSession</c>).</param>
public sealed record AdminCallerFacts(
    string Name,
    bool IsSystem,
    bool AdminEnabled,
    bool AdminFiltered,
    int IntegrityRid,
    int Session,
    int? ConsoleSession,
    bool DeskSession);

/// <summary>
/// Who may get a credential from the admin pipe:
/// <list type="bullet">
/// <item>never a process in a desk session (desk users are other people, even when their account is an administrator);</item>
/// <item>never a process below medium integrity (a sandboxed or low-integrity process of the owner);</item>
/// <item>SYSTEM, and an administrator whose token is elevated (an elevated prompt, the installer, SSH);</item>
/// <item>an administrator with a UAC-filtered token in the active console session (the owner's tray and CLI).</item>
/// </list>
/// Everybody else (a standard user at the console, any other RDP session) is refused and can still sign in with the
/// admin password.
/// </summary>
public static class AdminCallerRules
{
    public const int MediumIntegrity = 0x2000;

    public const string NotAdmin =
        "only an administrator signed in at this PC (or an elevated prompt) can manage SplitPlay; sign in with the admin password instead";

    public static AdminCallerDecision Decide(AdminCallerFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var caller = $"{facts.Name} (session {facts.Session})";
        if (facts.DeskSession)
        {
            return new AdminCallerDecision(false, caller, "callers in a desk session cannot manage SplitPlay", facts.Session);
        }

        if (!facts.IsSystem && facts.IntegrityRid < MediumIntegrity)
        {
            return new AdminCallerDecision(false, caller, "a process below medium integrity cannot manage SplitPlay", facts.Session);
        }

        if (facts.IsSystem || facts.AdminEnabled)
        {
            return new AdminCallerDecision(true, caller, Session: facts.Session);
        }

        if (facts.AdminFiltered && facts.ConsoleSession == facts.Session)
        {
            return new AdminCallerDecision(true, caller, Session: facts.Session);
        }

        return new AdminCallerDecision(false, caller, NotAdmin, facts.Session);
    }
}

/// <summary>Decides whether the client of an admin pipe connection is the host's administrator.</summary>
public interface IAdminCallerCheck
{
    /// <summary>
    /// Right after the client connected, before anything is read: refuses desk sessions and callers that are clearly not
    /// the admin, so that they cannot hold a pipe instance. Must be fast.
    /// </summary>
    AdminCallerDecision Screen(NamedPipeServerStream pipe);

    /// <summary>The client's session (one cheap call, before <see cref="Screen"/>), or null when it cannot be read.</summary>
    int? SessionOf(NamedPipeServerStream pipe);

    /// <summary>The final decision, after the request line was read (Windows can only impersonate a client that has written).</summary>
    AdminCallerDecision Check(NamedPipeServerStream pipe);
}

/// <summary>How the session of a Web API caller was found.</summary>
public enum PeerLookupKind
{
    /// <summary>Not looked up (not a loopback connection, not Windows, a test server).</summary>
    NotApplicable,

    /// <summary>The peer process and its session were found.</summary>
    Found,

    /// <summary>A loopback peer whose process or session could not be found.</summary>
    Failed,
}

/// <summary>The result of <see cref="IPeerInspector.PeerSession"/>.</summary>
public readonly record struct PeerLookup(PeerLookupKind Kind, int Session = 0)
{
    public static PeerLookup NotApplicable => new(PeerLookupKind.NotApplicable);

    public static PeerLookup Failed => new(PeerLookupKind.Failed);

    public static PeerLookup Found(int session) => new(PeerLookupKind.Found, session);
}

/// <summary>The Windows session of the process at the other end of a Web API connection.</summary>
public interface IPeerInspector
{
    PeerLookup PeerSession(HttpContext context);
}

/// <summary>Tests and non-Windows hosts: every pipe caller is the admin unless a test says otherwise.</summary>
public sealed class FakeAdminCallerCheck : IAdminCallerCheck
{
    /// <summary>When set, callers are refused with this reason (after reading the request).</summary>
    public string? Refuse { get; set; }

    /// <summary>When set, callers are dropped with this reason right after they connect.</summary>
    public string? RefuseAtConnect { get; set; }

    /// <summary>The session every caller is in.</summary>
    public int Session { get; set; } = 1;

    /// <summary>When set, screens of callers in <see cref="HoldSession"/> wait on it (tests: screens still running).</summary>
    public ManualResetEventSlim? HoldScreen { get; set; }

    public int? HoldSession { get; set; }

    private int _holding;
    private int _refuseConnects;

    /// <summary>The next this many connections are dropped at connect, then callers are screened as usual.</summary>
    public int RefuseConnects
    {
        get => Math.Max(0, Volatile.Read(ref _refuseConnects));
        set => Volatile.Write(ref _refuseConnects, value);
    }

    /// <summary>Screens waiting on <see cref="HoldScreen"/> right now.</summary>
    public int Holding => Volatile.Read(ref _holding);

    // A connection keeps the session it had when it was first asked about.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<NamedPipeServerStream, object> _sessions = [];

    public int? SessionOf(NamedPipeServerStream pipe) => (int)_sessions.GetValue(pipe, _ => Session);

    public AdminCallerDecision Screen(NamedPipeServerStream pipe)
    {
        var session = SessionOf(pipe)!.Value;
        if (HoldScreen is { } hold && session == HoldSession)
        {
            Interlocked.Increment(ref _holding);
            hold.Wait(TimeSpan.FromSeconds(10));
            Interlocked.Decrement(ref _holding);
        }

        if (Interlocked.Decrement(ref _refuseConnects) >= 0)
        {
            return new AdminCallerDecision(false, "test caller", "refused once", session);
        }

        Interlocked.Exchange(ref _refuseConnects, 0);
        return RefuseAtConnect is { } reason
            ? new AdminCallerDecision(false, "test caller", reason, session)
            : new AdminCallerDecision(true, "test caller", Session: session);
    }

    public AdminCallerDecision Check(NamedPipeServerStream pipe) =>
        Refuse is { } reason
            ? new AdminCallerDecision(false, "test caller", reason, Session)
            : new AdminCallerDecision(true, "test caller", Session: Session);
}

/// <summary>Tests and non-Windows hosts: the peer's session is whatever the test sets (default: not applicable).</summary>
public sealed class FakePeerInspector : IPeerInspector
{
    public PeerLookup Result { get; set; } = PeerLookup.NotApplicable;

    /// <summary>Shorthand: the peer was found in this session (null: not applicable).</summary>
    public int? Session
    {
        get => Result.Kind == PeerLookupKind.Found ? Result.Session : null;
        set => Result = value is int s ? PeerLookup.Found(s) : PeerLookup.NotApplicable;
    }

    public PeerLookup PeerSession(HttpContext context) => Result;
}
