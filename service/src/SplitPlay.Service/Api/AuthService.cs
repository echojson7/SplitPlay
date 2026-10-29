// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SplitPlay.Core.Ipc;
using SplitPlay.Service.Hosting;

namespace SplitPlay.Service.Api;

/// <summary>
/// Who may manage the host (web-api.md "Authentication"): browser sessions from the admin password, and the sessions
/// and bearer tokens that <see cref="AdminPipeServer"/> hands to the tray and the CLI of the host's administrator.
/// </summary>
/// <remarks>
/// The password hash is PBKDF2-SHA256 in <c>secrets\admin-password.json</c> under the data root (ARCHITECTURE.md §6
/// plans Argon2id under DPAPI-machine). Sessions and tokens live in memory: a service restart ends them, and the tray
/// asks for a new one. A request is the admin's when it carries a valid session cookie or bearer token.
/// </remarks>
public sealed class AuthService
{
    public const string CookieName = AdminPipe.SessionCookie;
    public const string CsrfHeader = "X-CSRF-Token";
    public const int MinPasswordLength = 8;
    private const int Iterations = 600_000;

    /// <summary>Lifetime of a browser session (password login, or the tray's session from the admin pipe).</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    /// <summary>Lifetime of a bearer token from the admin pipe (one CLI run).</summary>
    public static readonly TimeSpan BearerLifetime = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _bearers = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly string _file;
    private readonly TimeProvider _time;
    private readonly ILogger<AuthService> _logger;
    private PasswordRecord? _password;

    public AuthService(DataPaths paths, TimeProvider time, ILogger<AuthService> logger)
    {
        _file = Path.Combine(paths.Root, "secrets", "admin-password.json");
        _time = time;
        _logger = logger;
        _password = Load();
    }

    public bool HasPassword
    {
        get
        {
            lock (_gate)
            {
                return _password is not null;
            }
        }
    }

    /// <param name="FromPipe">Handed out over the admin pipe: a password change does not end it.</param>
    public sealed record Session(string Id, string CsrfToken, DateTimeOffset Expires, bool FromPipe = false);

    /// <summary>True when the request carries a valid session cookie or bearer token: it comes from the host's admin.</summary>
    public bool IsAdmin(HttpContext context) => HasValidBearer(context) || Current(context) is not null;

    /// <summary>The valid session named by the request's cookie, if any.</summary>
    public Session? Current(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Request.Cookies.TryGetValue(CookieName, out var id) || string.IsNullOrEmpty(id))
        {
            return null;
        }

        if (_sessions.TryGetValue(id, out var session))
        {
            if (session.Expires > _time.GetUtcNow())
            {
                return session;
            }

            _sessions.TryRemove(id, out _);
        }

        return null;
    }

    /// <summary>True when the request carries a valid <c>Authorization: Bearer</c> token (no CSRF token needed).</summary>
    public bool HasValidBearer(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var token = header["Bearer ".Length..].Trim();
        if (token.Length == 0 || !_bearers.TryGetValue(token, out var expires))
        {
            return false;
        }

        if (expires > _time.GetUtcNow())
        {
            return true;
        }

        _bearers.TryRemove(token, out _);
        return false;
    }

    /// <summary>A new bearer token (admin pipe; tests).</summary>
    public AdminToken IssueBearer(TimeSpan? lifetime = null)
    {
        Prune();
        var token = Token();
        var expires = _time.GetUtcNow() + (lifetime ?? BearerLifetime);
        _bearers[token] = expires;
        return new AdminToken(token, null, expires);
    }

    /// <summary>A new session for the tray's WebView (admin pipe): the cookie value and its CSRF token.</summary>
    public AdminToken IssuePipeSession()
    {
        Prune();
        var session = new Session(Token(), Token(), _time.GetUtcNow() + SessionLifetime, FromPipe: true);
        _sessions[session.Id] = session;
        return new AdminToken(session.Id, session.CsrfToken, session.Expires);
    }

    public bool Verify(string? password)
    {
        PasswordRecord? record;
        lock (_gate)
        {
            record = _password;
        }

        if (record is null || password is null)
        {
            return false;
        }

        var hash = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(record.Salt), record.Iterations, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(record.Hash));
    }

    /// <summary>Stores a new password and ends every session that was opened with a password.</summary>
    public void SetPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var record = new PasswordRecord("pbkdf2-sha256", Iterations, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(record, AuthJsonContext.Default.PasswordRecord));
            File.Move(temp, _file, overwrite: true);
            _password = record;
        }

        foreach (var (id, session) in _sessions)
        {
            if (!session.FromPipe)
            {
                _sessions.TryRemove(id, out _);
            }
        }

        _logger.LogInformation("Admin password changed");
    }

    /// <summary>Starts a session and sets the HttpOnly cookie on the response.</summary>
    public Session StartSession(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Prune();
        var session = new Session(Token(), Token(), _time.GetUtcNow() + SessionLifetime);
        _sessions[session.Id] = session;
        context.Response.Cookies.Append(CookieName, session.Id, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Path = "/",
            Expires = session.Expires,
        });
        return session;
    }

    public void EndSession(HttpContext context)
    {
        if (Current(context) is { } session)
        {
            _sessions.TryRemove(session.Id, out _);
        }

        context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });
    }

    private static string Token() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private void Prune()
    {
        var now = _time.GetUtcNow();
        foreach (var (token, expires) in _bearers)
        {
            if (expires <= now)
            {
                _bearers.TryRemove(token, out _);
            }
        }

        foreach (var (id, session) in _sessions)
        {
            if (session.Expires <= now)
            {
                _sessions.TryRemove(id, out _);
            }
        }
    }

    private PasswordRecord? Load()
    {
        try
        {
            return File.Exists(_file) ? JsonSerializer.Deserialize(File.ReadAllText(_file), AuthJsonContext.Default.PasswordRecord) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogError("Cannot read {File}: {Message}; treating the admin password as unset", _file, ex.Message);
            return null;
        }
    }
}

/// <summary>On-disk password hash.</summary>
public sealed record PasswordRecord(string Algorithm, int Iterations, string Salt, string Hash);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PasswordRecord))]
internal sealed partial class AuthJsonContext : JsonSerializerContext;
