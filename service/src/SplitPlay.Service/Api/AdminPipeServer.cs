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
public sealed class AdminPipeServer(
    AuthService auth,
    IAdminCallerCheck check,
    IOptions<SupervisorOptions> supervisor,
    IOptions<AuthOptions> options,
    ILogger<AdminPipeServer> logger) : BackgroundService
{
    /// <summary>Concurrent connections (a client that never writes is dropped after <see cref="RequestTimeout"/>).</summary>
    private const int MaxInstances = 4;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The pipe name for .NET pipe streams.</summary>
    public string LocalName => AdminPipe.LocalName(supervisor.Value.PipeNamespace);

    /// <summary>Completes once the first instance listens (tests).</summary>
    public Task Listening => _listening.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.AdminPipe)
        {
            _listening.TrySetResult();
            return;
        }

        var handlers = new List<Task>();
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream listening;
            try
            {
                // The first instance must be ours: a pipe someone created earlier under this name is refused.
                listening = Create(first: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError("Cannot create the admin pipe {Pipe}: {Message}; retrying in 10 s", LocalName, ex.Message);
                _listening.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                continue;
            }

            _listening.TrySetResult();
            try
            {
                while (true)
                {
                    await listening.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                    var connected = listening;
                    // Create the next instance while this one still exists, so that the name is never free for a squatter.
                    listening = await NextInstanceAsync(stoppingToken).ConfigureAwait(false);
                    handlers.RemoveAll(t => t.IsCompleted);
                    handlers.Add(HandleAsync(connected, stoppingToken));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await listening.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                logger.LogWarning("Admin pipe failed: {Message}; recreating it", ex.Message);
                await listening.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
        }

        await Task.WhenAll(handlers).ConfigureAwait(false);
    }

    private async Task<NamedPipeServerStream> NextInstanceAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            try
            {
                return Create(first: false);
            }
            catch (IOException)
            {
                // All instances busy: wait for a handler to finish (each one is bounded by RequestTimeout).
                await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cts.CancelAfter(RequestTimeout);
            try
            {
                var line = await AdminPipe.ReadLineAsync(pipe, cts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                var response = Answer(pipe, line);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(response, AdminPipeJsonContext.Default.AdminTokenResponse);
                await pipe.WriteAsync(bytes, cts.Token).ConfigureAwait(false);
                await pipe.WriteAsync("\n"u8.ToArray(), cts.Token).ConfigureAwait(false);
                await pipe.FlushAsync(cts.Token).ConfigureAwait(false);
                if (OperatingSystem.IsWindows())
                {
                    pipe.WaitForPipeDrain();
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                logger.LogDebug("Admin pipe client dropped: {Message}", ex.Message);
            }
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

        return new NamedPipeServerStream(LocalName, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
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
        return NamedPipeServerStreamAcl.Create(LocalName, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte, flags, 0, 0, security);
    }
}
