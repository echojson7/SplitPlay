// SPDX-License-Identifier: MIT
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SplitPlay.Core.Api;
using SplitPlay.Core.Ipc;
using SplitPlay.Service.Supervision;

namespace SplitPlay.Service.Api;

/// <summary>
/// Serves <c>\\.\pipe\splitplay\admin</c> (web-api.md "Authentication"): the tray and the CLI of the host's
/// administrator get a session or a bearer token there without typing a password. The DACL lets SYSTEM,
/// Administrators and interactive users connect; <see cref="IAdminCallerCheck"/> then decides from the caller's token
/// (an elevated administrator anywhere, a UAC-filtered administrator at the console; never a desk session).
/// </summary>
/// <remarks>
/// Nobody can hold the pipe: there is no instance limit; a caller from a desk session or one that is clearly not the
/// admin is dropped right after it connects (<see cref="IAdminCallerCheck.Screen"/>); at most
/// <see cref="MaxConnectionsPerSession"/> connections per session run at once and at most
/// <see cref="MaxRequestsPerSession"/> are accepted per session within <see cref="RateWindow"/>; every connection ends
/// after <see cref="RequestTimeout"/>. Stopping the service cancels and disposes every connection.
/// </remarks>
public sealed class AdminPipeServer(
    AuthService auth,
    IAdminCallerCheck check,
    IOptions<SupervisorOptions> supervisor,
    IOptions<AuthOptions> options,
    ILogger<AdminPipeServer> logger,
    TimeProvider time) : BackgroundService
{
    public const int MaxConnectionsPerSession = 4;

    public const int MaxRequestsPerSession = 30;

    public static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);

    /// <summary>Time a client has from connecting to having read the answer.</summary>
    public static TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan MaxCreateRetry = TimeSpan.FromSeconds(30);

    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly Dictionary<int, int> _active = [];
    private readonly Dictionary<int, Queue<DateTimeOffset>> _recent = [];
    private readonly HashSet<NamedPipeServerStream> _open = [];

    /// <summary>The pipe name for .NET pipe streams.</summary>
    public string LocalName => AdminPipe.LocalName(supervisor.Value.PipeNamespace);

    /// <summary>Completes once the first instance listens, or creating it failed once (tests).</summary>
    public Task Listening => _listening.Task;

    /// <summary>Connections dropped right after connecting (tests).</summary>
    public int Dropped { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.AdminPipe)
        {
            _listening.TrySetResult();
            return;
        }

        var handlers = new List<Task>();
        var retry = TimeSpan.FromSeconds(1);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                NamedPipeServerStream listening;
                try
                {
                    // The first instance must be ours (FILE_FLAG_FIRST_PIPE_INSTANCE): a pipe someone created earlier under
                    // this name is refused, and nobody gets the tray's or the CLI's requests.
                    listening = Create(first: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogError("Cannot create the admin pipe {Pipe}: {Message}. Another process may hold this name; the tray and the CLI " +
                                    "cannot sign in until it is gone. Retrying in {Delay} s", LocalName, ex.Message, (int)retry.TotalSeconds);
                    _listening.TrySetResult();
                    await Task.Delay(retry, time, stoppingToken).ConfigureAwait(false);
                    retry = retry * 2 > MaxCreateRetry ? MaxCreateRetry : retry * 2;
                    continue;
                }

                retry = TimeSpan.FromSeconds(1);
                Track(listening, add: true);
                _listening.TrySetResult();
                try
                {
                    while (true)
                    {
                        await listening.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                        var connected = listening;
                        // Create the next instance while this one still exists, so that the name is never free for a squatter.
                        listening = Create(first: false);
                        Track(listening, add: true);
                        handlers.RemoveAll(t => t.IsCompleted);
                        handlers.Add(HandleAsync(connected, stoppingToken));
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    logger.LogWarning("Admin pipe failed: {Message}; recreating it", ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(1), time, stoppingToken).ConfigureAwait(false);
                }
                finally
                {
                    Track(listening, add: false);
                    await listening.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Every handler is bounded by RequestTimeout and cancelled with stoppingToken; disposing the open pipes ends
            // any I/O that ignores cancellation.
            List<NamedPipeServerStream> open;
            lock (_gate)
            {
                open = [.. _open];
                _open.Clear();
            }

            foreach (var pipe in open)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }

            try
            {
                await Task.WhenAll(handlers).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                logger.LogWarning("Admin pipe connections did not end within 2 s of stopping");
            }
        }
    }

    private void Track(NamedPipeServerStream pipe, bool add)
    {
        lock (_gate)
        {
            if (add)
            {
                _open.Add(pipe);
            }
            else
            {
                _open.Remove(pipe);
            }
        }
    }

    /// <summary>Admits a connection from <paramref name="session"/>, or returns why not.</summary>
    private string? Admit(int session)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (!_recent.TryGetValue(session, out var recent))
            {
                recent = new Queue<DateTimeOffset>();
                _recent[session] = recent;
            }

            while (recent.Count > 0 && now - recent.Peek() > RateWindow)
            {
                recent.Dequeue();
            }

            if (recent.Count >= MaxRequestsPerSession)
            {
                return "too many requests from this session";
            }

            if (_active.GetValueOrDefault(session) >= MaxConnectionsPerSession)
            {
                return "too many open connections from this session";
            }

            recent.Enqueue(now);
            _active[session] = _active.GetValueOrDefault(session) + 1;
            return null;
        }
    }

    private void Release(int session)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(session, out var n))
            {
                if (n <= 1)
                {
                    _active.Remove(session);
                }
                else
                {
                    _active[session] = n - 1;
                }
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        int? admitted = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cts.CancelAfter(RequestTimeout);

            // Before reading anything: a desk session or a caller that is clearly not the admin is dropped at once.
            AdminCallerDecision screen;
            try
            {
                screen = check.Screen(pipe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                                           or InvalidOperationException)
            {
                screen = new AdminCallerDecision(false, "unknown caller", $"could not identify the caller: {ex.Message}");
            }

            var session = screen.Session ?? -1;
            var refusal = screen.Allowed ? Admit(session) : screen.Reason;
            if (refusal is not null)
            {
                Dropped++;
                logger.LogWarning("Admin pipe: dropped {Caller} at connect: {Reason}", screen.Caller, refusal);
                await TryAnswerAsync(pipe, Refused(ApiErrorCodes.Forbidden, refusal), cts.Token).ConfigureAwait(false);
                return;
            }

            admitted = session;
            var line = await AdminPipe.ReadLineAsync(pipe, cts.Token).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            await TryAnswerAsync(pipe, Answer(pipe, line), cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            logger.LogDebug("Admin pipe client dropped: {Message}", ex.Message);
        }
        finally
        {
            if (admitted is int s)
            {
                Release(s);
            }

            Track(pipe, add: false);
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes the answer line and waits (bounded by <paramref name="ct"/>) for the client to close its end, so that the
    /// answer is read before the pipe goes away. No unbounded WaitForPipeDrain. The client's side is read from before
    /// the answer is written: a caller refused at connect is still writing its request, and a write on a full pipe only
    /// completes once the other end reads, so both ends would otherwise wait on each other until the deadline.
    /// </summary>
    private static async Task TryAnswerAsync(NamedPipeServerStream pipe, AdminTokenResponse response, CancellationToken ct)
    {
        var drain = DrainAsync(pipe, ct);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(response, AdminPipeJsonContext.Default.AdminTokenResponse);
            await pipe.WriteAsync(bytes, ct).ConfigureAwait(false);
            await pipe.WriteAsync("\n"u8.ToArray(), ct).ConfigureAwait(false);
            await pipe.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }

        await drain.ConfigureAwait(false);
    }

    /// <summary>Reads and discards until the client closes its end or <paramref name="ct"/> fires.</summary>
    private static async Task DrainAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            var sink = new byte[256];
            while (await pipe.ReadAsync(sink, ct).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    private AdminTokenResponse Answer(NamedPipeServerStream pipe, string line)
    {
        AdminTokenRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(line, AdminPipeJsonContext.Default.AdminTokenRequest);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request?.Kind is not (AdminPipe.Kinds.Bearer or AdminPipe.Kinds.Session))
        {
            return Refused(ApiErrorCodes.BadRequest, "expected {\"kind\":\"bearer\"|\"session\"}");
        }

        AdminCallerDecision decision;
        try
        {
            decision = check.Check(pipe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                                       or InvalidOperationException)
        {
            logger.LogWarning("Admin pipe: could not identify the caller: {Message}", ex.Message);
            return Refused(ApiErrorCodes.Forbidden, "could not identify the caller");
        }

        if (!decision.Allowed)
        {
            logger.LogWarning("Admin pipe: refused a {Kind} token to {Caller}: {Reason}", request.Kind, decision.Caller, decision.Reason);
            return Refused(ApiErrorCodes.Forbidden, decision.Reason ?? "not allowed");
        }

        var token = request.Kind == AdminPipe.Kinds.Session ? auth.IssuePipeSession() : auth.IssueBearer();
        logger.LogDebug("Admin pipe: issued a {Kind} token to {Caller} ({Client})", request.Kind, decision.Caller, request.Client ?? "unknown client");
        return new AdminTokenResponse(true, token.Token, token.CsrfToken, token.ExpiresAt);
    }

    private static AdminTokenResponse Refused(string code, string message) => new(false, Error: new AdminPipeError(code, message));

    private NamedPipeServerStream Create(bool first)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateWindows(first);
        }

        return new NamedPipeServerStream(LocalName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, AdminPipe.MaxLineBytes, AdminPipe.MaxLineBytes);
    }

    [SupportedOSPlatform("windows")]
    private NamedPipeServerStream CreateWindows(bool first)
    {
        var security = new PipeSecurity();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        // Console and RDP sign-ins (UAC-filtered administrators included); IAdminCallerCheck decides who gets a token.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        using (var identity = WindowsIdentity.GetCurrent())
        {
            // Console mode (tests, CI): the account running the service creates the further instances.
            if (identity.User is { } self && !self.Equals(system))
            {
                security.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
            }
        }

        var flags = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(LocalName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            // Room for one request and one answer line, so that neither side's single write waits on the other's read.
            PipeTransmissionMode.Byte, flags, AdminPipe.MaxLineBytes, AdminPipe.MaxLineBytes, security);
    }
}
