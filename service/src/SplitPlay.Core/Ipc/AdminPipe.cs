// SPDX-License-Identifier: MIT
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SplitPlay.Core.Ipc;

/// <summary>
/// The admin token pipe <c>\\.\pipe\splitplay\admin</c> (web-api.md "Authentication"): the tray and the CLI ask the
/// service for a short-lived Web API credential. One JSON line each way, then the service closes the connection.
/// The service decides from the caller's Windows token whether it is the host's administrator.
/// </summary>
public static class AdminPipe
{
    /// <summary>Kinds of credential a caller can ask for.</summary>
    public static class Kinds
    {
        /// <summary><c>Authorization: Bearer</c> token (CLI, scripts).</summary>
        public const string Bearer = "bearer";

        /// <summary>A browser session: the value of the <c>splitplay_session</c> cookie plus its CSRF token (tray WebView).</summary>
        public const string Session = "session";
    }

    /// <summary>The Web API session cookie a <see cref="Kinds.Session"/> credential is the value of.</summary>
    public const string SessionCookie = "splitplay_session";

    /// <summary>Longest request or answer line.</summary>
    public const int MaxLineBytes = 4096;

    /// <summary>The pipe name for .NET pipe streams (without <c>\\.\pipe\</c>).</summary>
    public static string LocalName(string ns = PipeNames.DefaultNamespace) => ns + "admin";

    /// <summary>What a caller is told when the service closed the connection without an answer twice.</summary>
    public const string ClosedMessage = "the SplitPlay service closed the connection without an answer (restarting, or the request was refused)";

    /// <summary>Pause before the one retry after the service closed the connection without an answer.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Asks the service for a credential. Returns null when the pipe is not there (service stopped, older build) or does
    /// not answer within <paramref name="timeout"/>; throws <see cref="AdminPipeException"/> when the service refuses.
    /// A connection the service closes without an answer (how it drops callers it will not serve, or a restart) is
    /// tried once more; if that is closed too, the exception says so (<see cref="ClosedMessage"/>).
    /// </summary>
    public static async Task<AdminToken?> RequestAsync(string kind, string client, TimeSpan timeout, string ns = PipeNames.DefaultNamespace,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await RequestOnceAsync(kind, client, ns, cts, cancellationToken).ConfigureAwait(false);
            }
            catch (ClosedException) when (attempt == 0)
            {
                try
                {
                    await Task.Delay(RetryDelay, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return null;
                }
            }
            catch (ClosedException)
            {
                throw new AdminPipeException("forbidden", ClosedMessage);
            }
        }
    }

    /// <summary>The service closed the connection without an answer.</summary>
    private sealed class ClosedException : Exception;

    private static async Task<AdminToken?> RequestOnceAsync(string kind, string client, string ns, CancellationTokenSource cts,
        CancellationToken cancellationToken)
    {
        // Identification is enough for the service to read who we are; it cannot act as us.
        await using var pipe = new NamedPipeClientStream(".", LocalName(ns), PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            switch (ServerIsTrusted(pipe))
            {
                case null:
                    throw new ClosedException();
                case false:
                    // Someone else created the pipe before the service did (the service was not running): never talk to it.
                    throw new AdminPipeException("forbidden", "the admin pipe is not owned by the SplitPlay service");
            }
        }

        try
        {
            var request = JsonSerializer.SerializeToUtf8Bytes(new AdminTokenRequest(kind, client), AdminPipeJsonContext.Default.AdminTokenRequest);
            await pipe.WriteAsync(request, cts.Token).ConfigureAwait(false);
            await pipe.WriteAsync("\n"u8.ToArray(), cts.Token).ConfigureAwait(false);
            await pipe.FlushAsync(cts.Token).ConfigureAwait(false);
            var line = await ReadLineAsync(pipe, cts.Token).ConfigureAwait(false);
            if (line is null)
            {
                throw new ClosedException();
            }

            var answer = JsonSerializer.Deserialize(line, AdminPipeJsonContext.Default.AdminTokenResponse);
            if (answer is null)
            {
                return null;
            }

            if (!answer.Ok || answer.Token is null)
            {
                throw new AdminPipeException(answer.Error?.Code ?? "forbidden", answer.Error?.Message ?? "the service refused the request");
            }

            return new AdminToken(answer.Token, answer.CsrfToken, answer.ExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(5));
        }
        catch (IOException) when (!cts.IsCancellationRequested)
        {
            // Connected, then the service closed its end before answering.
            cancellationToken.ThrowIfCancellationRequested();
            throw new ClosedException();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or JsonException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    /// <summary>Reads one <c>\n</c>-terminated line of at most <see cref="MaxLineBytes"/>; null at end of stream.</summary>
    public static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = new byte[MaxLineBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return length == 0 ? null : Encoding.UTF8.GetString(buffer, 0, length);
            }

            if (buffer[length] == (byte)'\n')
            {
                return Encoding.UTF8.GetString(buffer, 0, length).TrimEnd('\r');
            }

            length++;
        }

        throw new IOException($"admin pipe line longer than {MaxLineBytes} bytes");
    }

    /// <summary>
    /// The pipe's owner must be SYSTEM (the service), the Administrators group (an elevated console-mode service) or
    /// the caller itself: a pipe created by anybody else is a squatter. Null when the pipe was closed meanwhile.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool? ServerIsTrusted(NamedPipeClientStream pipe)
    {
        try
        {
            var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is null)
            {
                return false;
            }

            using var self = WindowsIdentity.GetCurrent();
            return owner.IsWellKnown(WellKnownSidType.LocalSystemSid)
                   || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                   || (self.User is { } me && owner.Equals(me));
        }
        catch (IOException)
        {
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException
                                       or System.Security.AccessControl.PrivilegeNotHeldException)
        {
            return false;
        }
    }
}

/// <summary>A credential from the admin pipe.</summary>
/// <param name="Token">Bearer token, or the session cookie value.</param>
/// <param name="CsrfToken">For a session: the CSRF token to send with state changes.</param>
/// <param name="ExpiresAt">When it stops working.</param>
public sealed record AdminToken(string Token, string? CsrfToken, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"admin token (expires {ExpiresAt:O})";
}

/// <summary>The service refused to hand out a credential (the caller is not the host's administrator).</summary>
public sealed class AdminPipeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Request line: <c>{"kind":"bearer","client":"cli"}</c>.</summary>
public sealed record AdminTokenRequest(string? Kind, string? Client);

/// <summary>Answer line: <c>{"ok":true,"token":"…","csrfToken":"…","expiresAt":"…"}</c> or <c>{"ok":false,"error":{…}}</c>.</summary>
public sealed record AdminTokenResponse(
    bool Ok,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Token = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CsrfToken = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ExpiresAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AdminPipeError? Error = null);

/// <summary>Why a request was refused.</summary>
public sealed record AdminPipeError(string Code, string Message);

/// <summary>Source-generated metadata for the admin pipe (camelCase).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AdminTokenRequest))]
[JsonSerializable(typeof(AdminTokenResponse))]
public sealed partial class AdminPipeJsonContext : JsonSerializerContext;
