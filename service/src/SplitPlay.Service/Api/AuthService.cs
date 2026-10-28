// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SplitPlay.Service.Hosting;

namespace SplitPlay.Service.Api;

/// <summary>
/// Admin password and browser sessions (web-api.md "Authentication").
/// </summary>
/// <remarks>
/// Stage 2: the password hash is PBKDF2-SHA256 in <c>secrets\admin-password.json</c> under the data root; Stage 3 moves
/// it to Argon2id under DPAPI-machine (ARCHITECTURE.md §6). Sessions live in memory (a service restart logs browsers
/// out). Loopback callers are still served without a session because the CLI/tray token pipe does not exist yet; the
/// session and CSRF checks apply to requests that carry the session cookie.
/// </remarks>
public sealed class AuthService
{
    public const string CookieName = "splitplay_session";
    public const string CsrfHeader = "X-CSRF-Token";
    public const int MinPasswordLength = 8;
    private const int Iterations = 600_000;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
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

    public sealed record Session(string Id, string CsrfToken, DateTimeOffset Expires);

    /// <summary>The valid session named by the request's cookie, if any.</summary>
    public Session? Current(HttpContext context)
    {
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

    /// <summary>Stores a new password and ends every existing session.</summary>
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

        _sessions.Clear();
        _logger.LogInformation("Admin password changed");
    }

    /// <summary>Starts a session and sets the HttpOnly cookie on the response.</summary>
    public Session StartSession(HttpContext context)
    {
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
