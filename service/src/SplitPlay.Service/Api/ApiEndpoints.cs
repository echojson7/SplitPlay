// SPDX-License-Identifier: MIT
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using SplitPlay.Core;
using SplitPlay.Core.Api;
using SplitPlay.Core.Configuration;
using SplitPlay.Core.Host;
using SplitPlay.Core.Ipc;
using SplitPlay.Core.Json;
using SplitPlay.Service.Configuration;
using SplitPlay.Service.Hosting;
using SplitPlay.Service.Logging;
using SplitPlay.Service.Platform;
using SplitPlay.Service.Supervision;

namespace SplitPlay.Service.Api;

/// <summary>Web API v1 (docs/contracts/web-api.md) on <c>/api/v1</c>.</summary>
public static partial class ApiEndpoints
{
    private const long MaxImportBytes = 16 * 1024 * 1024;

    private static SplitPlayJsonContext Ctx => SplitPlayJsonContext.Default;

    [GeneratedRegex("^[0-9]{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex PinRegex();

    /// <summary>Error handling, the loopback and origin guards and the admin check for <c>/api</c>.</summary>
    public static IApplicationBuilder UseApiErrorHandling(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // LAN access (HTTPS) is not implemented yet: only loopback callers are served (web-api.md).
        var remote = context.Connection.RemoteIpAddress;
        if (remote is not null && !IPAddress.IsLoopback(remote))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, "only loopback requests are allowed").ConfigureAwait(false);
            return;
        }

        // DNS rebinding: a page on an attacker's name that resolves to 127.0.0.1 would be "same origin" with us.
        if (!HostAllowed(context))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden,
                $"unexpected Host header '{context.Request.Host}'").ConfigureAwait(false);
            return;
        }

        var changesState = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
                           && !HttpMethods.IsOptions(context.Request.Method);

        // Another site's page (a form post, fetch without CORS) carries no SameSite=Strict cookie, so the CSRF check
        // below would not apply to it: refuse state changes that a browser marks as coming from another origin.
        if (changesState && CrossOriginProblem(context.Request) is { } crossOrigin)
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, crossOrigin).ConfigureAwait(false);
            return;
        }

        // Desk users are other people: a process in a desk session gets nothing but /health, token or not.
        var isHealth = IsRoute(context, "GET", "/health");
        if (!isHealth && PeerInDeskSession(context))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden,
                "requests from a desk session are not accepted").ConfigureAwait(false);
            return;
        }

        // Everything but health and the sign-in endpoints needs the admin: a session (password login, or the tray's
        // from the admin pipe) or a bearer token from the admin pipe (CLI).
        var auth = context.RequestServices.GetRequiredService<AuthService>();
        if (!isHealth && !IsAuthRoute(context) && !auth.IsAdmin(context))
        {
            await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthorized,
                "sign in with the admin password, or open SplitPlay from the tray").ConfigureAwait(false);
            return;
        }

        // A browser session must echo its CSRF token on state-changing requests (a bearer token is no ambient credential).
        if (changesState
            && !IsRoute(context, "POST", "/auth/login")
            && !auth.HasValidBearer(context)
            && auth.Current(context) is { } session
            && context.Request.Headers[AuthService.CsrfHeader] != session.CsrfToken)
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, ApiErrorCodes.CsrfFailed,
                $"missing or wrong {AuthService.CsrfHeader} header").ConfigureAwait(false);
            return;
        }

        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (ApiException ex) when (!context.Response.HasStarted)
        {
            await WriteErrorAsync(context, ex.Status, ex.Code, ex.Message).ConfigureAwait(false);
        }
        catch (ConfigException ex) when (!context.Response.HasStarted)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, ex.Message).ConfigureAwait(false);
        }
        catch (BadHttpRequestException ex) when (!context.Response.HasStarted)
        {
            await WriteErrorAsync(context, ex.StatusCode, ApiErrorCodes.BadRequest, ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex) when (!context.Response.HasStarted && ex is not OperationCanceledException)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SplitPlay.Api").LogError(ex, "Unhandled API error");
            await WriteErrorAsync(context, StatusCodes.Status500InternalServerError, ApiErrorCodes.Internal, ex.Message).ConfigureAwait(false);
        }
    });

    private static bool IsRoute(HttpContext context, string method, string path) =>
        string.Equals(context.Request.Method, method, StringComparison.OrdinalIgnoreCase)
        && string.Equals(context.Request.Path.Value?.TrimEnd('/'), BuildInfo.ApiPrefix + path, StringComparison.OrdinalIgnoreCase);

    /// <summary>The sign-in endpoints, open without a session (<c>/auth/password</c> checks the caller itself).</summary>
    private static bool IsAuthRoute(HttpContext context) =>
        IsRoute(context, "GET", "/auth/session") || IsRoute(context, "POST", "/auth/login")
        || IsRoute(context, "POST", "/auth/logout") || IsRoute(context, "POST", "/auth/password");

    /// <summary>True when the TCP peer is a process in a desk session (Auth:PeerCheck).</summary>
    private static bool PeerInDeskSession(HttpContext context)
    {
        if (!context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthOptions>>().Value.PeerCheck)
        {
            return false;
        }

        return context.RequestServices.GetRequiredService<IPeerInspector>().PeerSession(context) is int session
               && context.RequestServices.GetRequiredService<DeskSupervisor>().IsDeskSession(session);
    }

    /// <summary>
    /// Why a state-changing request is refused as cross-origin, or null: <c>Sec-Fetch-Site: cross-site</c>, or an
    /// <c>Origin</c> other than the request's own scheme://host:port (<c>Origin: null</c> included). Requests without
    /// either header (CLI, scripts) pass; so does the UI, which is served from the same origin (loopback or LAN host).
    /// </summary>
    internal static string? CrossOriginProblem(HttpRequest request)
    {
        if (string.Equals(request.Headers["Sec-Fetch-Site"].ToString(), "cross-site", StringComparison.OrdinalIgnoreCase))
        {
            return "cross-site request refused";
        }

        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0)
        {
            return null;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var o) || o.Scheme is not ("http" or "https") || !request.Host.HasValue)
        {
            return $"request from origin '{origin}' refused";
        }

        var port = request.Host.Port ?? (request.IsHttps ? 443 : 80);
        var same = string.Equals(o.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(o.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase)
                   && o.Port == port;
        return same ? null : $"request from origin '{origin}' refused";
    }

    /// <summary>
    /// On a loopback connection the Host header must be <c>localhost</c> or an IP address: any other name can only come
    /// from a DNS name that resolves to this machine (DNS rebinding). LAN connections are not restricted here.
    /// </summary>
    internal static bool HostAllowed(HttpContext context)
    {
        var local = context.Connection.LocalIpAddress;
        if (local is not null && !IPAddress.IsLoopback(local))
        {
            return true;
        }

        var host = context.Request.Host.Host;
        return host.Length == 0
               || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
               || IPAddress.TryParse(host.Trim('[', ']'), out _);
    }

    public static void MapSplitPlayApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(BuildInfo.ApiPrefix);

        api.MapGet("/health", (DeskSupervisor s) =>
            Results.Json(new HealthDto("ok", BuildInfo.Version, (long)s.Uptime.TotalSeconds), Ctx.HealthDto));

        // ---- auth
        api.MapGet("/auth/session", (HttpContext context, AuthService auth) =>
        {
            // setupRequired: no admin password yet (only the tray, the CLI or an elevated prompt can manage the host).
            var session = auth.Current(context);
            return Results.Json(new AuthSessionDto(!auth.HasPassword, auth.IsAdmin(context), session?.CsrfToken), Ctx.AuthSessionDto);
        });

        api.MapPost("/auth/login", async (HttpContext context, AuthService auth) =>
        {
            var body = await ReadBodyAsync(context.Request, Ctx.LoginRequest).ConfigureAwait(false);
            if (!auth.HasPassword)
            {
                throw ApiException.Conflict(ApiErrorCodes.InvalidState, "no admin password is set yet; use POST /auth/password");
            }

            if (!auth.Verify(body.Password))
            {
                throw new ApiException(StatusCodes.Status401Unauthorized, ApiErrorCodes.InvalidPassword, "wrong password");
            }

            return Results.Json(new LoginResponse(auth.StartSession(context).CsrfToken), Ctx.LoginResponse);
        });

        api.MapPost("/auth/logout", (HttpContext context, AuthService auth) =>
        {
            auth.EndSession(context);
            return Results.NoContent();
        });

        api.MapPost("/auth/password", async (HttpContext context, AuthService auth) =>
        {
            var body = await ReadBodyAsync(context.Request, Ctx.PasswordRequest).ConfigureAwait(false);
            if (body.NewPassword is null || body.NewPassword.Length < AuthService.MinPasswordLength)
            {
                throw ApiException.Validation($"newPassword: must be at least {AuthService.MinPasswordLength} characters");
            }

            if (!auth.HasPassword && !auth.IsAdmin(context))
            {
                // Otherwise anybody on this PC (a desk user) could set the first password and take the host over.
                throw new ApiException(StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthorized,
                    "the first admin password can only be set from the tray, the CLI or an elevated prompt");
            }

            if (auth.HasPassword && !auth.Verify(body.CurrentPassword))
            {
                throw new ApiException(StatusCodes.Status401Unauthorized, ApiErrorCodes.InvalidPassword, "currentPassword is wrong");
            }

            auth.SetPassword(body.NewPassword);
            // Setting or changing the password logs this browser in (and every other session out).
            return Results.Json(new LoginResponse(auth.StartSession(context).CsrfToken), Ctx.LoginResponse);
        });

        // ---- host settings (the "host" object of config.json)
        api.MapGet("/host", (ConfigService config) => Results.Json(config.Snapshot().Host, Ctx.HostConfig));

        api.MapPut("/host", async (HttpRequest request, ConfigService config, IFirewallRule firewall, IDeskPlatform platform, ILoggerFactory loggers) =>
        {
            var host = await ReadBodyAsync(request, Ctx.HostConfig).ConfigureAwait(false);
            var before = config.Snapshot().Host;
            var saved = config.Update(cfg =>
            {
                cfg.Host = host;
                return host;
            });

            if (saved.WebPort != before.WebPort)
            {
                loggers.CreateLogger("SplitPlay.Api").LogWarning("host.webPort changed to {Port}; it takes effect after a service restart", saved.WebPort);
            }

            if (saved.ComposeIntervalMs != before.ComposeIntervalMs)
            {
                try
                {
                    platform.ApplyComposeInterval(saved.ComposeIntervalMs);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HostTuningException or System.Security.SecurityException)
                {
                    loggers.CreateLogger("SplitPlay.Api").LogWarning("Could not apply host.composeIntervalMs: {Message}", ex.Message);
                }
            }

            if (saved.LanAccess != before.LanAccess)
            {
                await firewall.SetWebUiRuleEnabledAsync(saved.LanAccess).ConfigureAwait(false);
            }

            return Results.Json(saved, Ctx.HostConfig);
        });

        // ---- desks
        api.MapGet("/desks", (DeskSupervisor s) => Results.Json(s.List().ToList(), Ctx.ListDeskDto));

        api.MapGet("/host/users", (IDeskPlatform platform) => Results.Json(platform.ListUsers().ToList(), Ctx.ListHostUserDto));

        api.MapGet("/host/network", () => Results.Json(new HostNetworkDto(HostNetwork.Addresses()), Ctx.HostNetworkDto));

        // ---- Windows services SplitPlay depends on (allow-list in RelatedServices)
        api.MapGet("/host/services", (RelatedServices services) => Results.Json(services.List().ToList(), Ctx.ListHostServiceDto));
        api.MapPost("/host/services/{name}/start", (string name, RelatedServices services) =>
            Results.Json(services.Start(name), Ctx.HostServiceDto, statusCode: StatusCodes.Status202Accepted));
        api.MapPost("/host/services/{name}/stop", (string name, RelatedServices services) =>
            Results.Json(services.Stop(name), Ctx.HostServiceDto, statusCode: StatusCodes.Status202Accepted));

        api.MapPost("/desks", async (HttpRequest request, DeskSupervisor s) =>
        {
            var (desk, password) = await ReadDeskBodyAsync(request).ConfigureAwait(false);
            var created = s.Create(desk, password);
            return Results.Json(created, Ctx.DeskDto, statusCode: StatusCodes.Status201Created);
        });

        api.MapGet("/desks/{id}", (string id, DeskSupervisor s) => Results.Json(s.Get(id), Ctx.DeskDto));

        api.MapPut("/desks/{id}", async (string id, HttpRequest request, DeskSupervisor s) =>
        {
            var (desk, password) = await ReadDeskBodyAsync(request).ConfigureAwait(false);
            return Results.Json(s.Replace(id, desk, password), Ctx.DeskDto);
        });

        api.MapDelete("/desks/{id}", (string id, bool? deleteUser, DeskSupervisor s) =>
        {
            s.Delete(id, deleteUser ?? false);
            return Results.NoContent();
        });

        api.MapPost("/desks/{id}/start", (string id, DeskSupervisor s) => Results.Json(s.Start(id), Ctx.DeskDto, statusCode: StatusCodes.Status202Accepted));
        api.MapPost("/desks/{id}/stop", (string id, DeskSupervisor s) => Results.Json(s.Stop(id), Ctx.DeskDto, statusCode: StatusCodes.Status202Accepted));
        api.MapPost("/desks/{id}/restart", async (string id, DeskSupervisor s) =>
            Results.Json(await s.RestartAsync(id).ConfigureAwait(false), Ctx.DeskDto, statusCode: StatusCodes.Status202Accepted));

        // ---- pairing (forwarded to the worker over the pipe)
        api.MapGet("/desks/{id}/clients", async (string id, DeskSupervisor s) =>
        {
            var result = EnsureOk(await s.RequestAsync(id, rid => new ListClientsMessage { Id = rid }).ConfigureAwait(false));
            return Results.Json(ParseClients(result.Data), Ctx.ListClientDto);
        });

        api.MapPost("/desks/{id}/pair", async (string id, HttpRequest request, DeskSupervisor s) =>
        {
            var body = await ReadBodyAsync(request, Ctx.PairRequest).ConfigureAwait(false);
            if (body.Pin is null || !PinRegex().IsMatch(body.Pin))
            {
                throw ApiException.Validation("pin: must be 4 digits");
            }

            if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 64)
            {
                throw ApiException.Validation("name: must be 1-64 characters");
            }

            s.Get(id);
            EnsureOk(await s.RequestAsync(id, rid => new PairMessage { Id = rid, Pin = body.Pin, Name = body.Name }).ConfigureAwait(false));
            return Results.NoContent();
        });

        api.MapDelete("/desks/{id}/clients/{clientId}", async (string id, string clientId, DeskSupervisor s) =>
        {
            EnsureOk(await s.RequestAsync(id, rid => new UnpairMessage { Id = rid, Client = clientId }).ConfigureAwait(false));
            return Results.NoContent();
        });

        // ---- observability
        api.MapGet("/metrics", (DeskSupervisor s) => Results.Json(s.Metrics(), Ctx.MetricsDto));

        api.MapGet("/logs", (string? component, string? desk, int? tail, LogStore logs) =>
            Results.Json(logs.Tail(Math.Clamp(tail ?? 200, 1, 5000), component, desk), Ctx.ListLogLineDto));

        // ---- import / export
        api.MapGet("/config/export", (bool? includeSecrets, ConfigService config) =>
        {
            if (includeSecrets == true)
            {
                throw ApiException.NotImplemented("secrets are not stored yet, so there is nothing to include");
            }

            var json = ConfigStore.Serialize(config.Snapshot());
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry(ConfigStore.FileName, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(json);
            }

            var name = $"splitplay-config-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";
            return Results.File(buffer.ToArray(), "application/zip", name);
        });

        api.MapPost("/config/import", async (HttpRequest request, DeskSupervisor s, ConfigService config, IFirewallRule firewall) =>
        {
            var lanBefore = config.Snapshot().Host.LanAccess;
            var bytes = await ReadUploadAsync(request).ConfigureAwait(false);
            string json;
            try
            {
                using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                var entry = zip.GetEntry(ConfigStore.FileName)
                            ?? throw ApiException.Validation($"the archive has no {ConfigStore.FileName}");
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                json = await reader.ReadToEndAsync().ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                throw ApiException.Validation($"not a zip archive: {ex.Message}");
            }

            var parsed = ConfigStore.Parse(json);
            var desks = s.Import(parsed.Config).ToList();
            if (parsed.Config.Host.LanAccess != lanBefore)
            {
                await firewall.SetWebUiRuleEnabledAsync(parsed.Config.Host.LanAccess).ConfigureAwait(false);
            }

            return Results.Json(desks, Ctx.ListDeskDto);
        });

        // ---- push
        api.Map("/ws", async (HttpContext context, EventHub hub) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                throw ApiException.BadRequest("expected a WebSocket upgrade");
            }

            // Cross-site WebSocket hijacking: another site's page may open the socket too.
            if (CrossOriginProblem(context.Request) is { } crossOrigin)
            {
                throw new ApiException(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, crossOrigin);
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await hub.HandleAsync(socket, context.RequestAborted).ConfigureAwait(false);
        });

        app.MapFallback("/api/{**rest}", (HttpContext context) =>
            Results.Json(ApiErrorResponse.Of(ApiErrorCodes.NotFound, $"no endpoint {context.Request.Method} {context.Request.Path}"),
                Ctx.ApiErrorResponse, statusCode: StatusCodes.Status404NotFound));
    }

    private static IResult NotImplemented(string message) =>
        Results.Json(ApiErrorResponse.Of(ApiErrorCodes.NotImplemented, message), Ctx.ApiErrorResponse, statusCode: StatusCodes.Status501NotImplemented);

    private static Task WriteErrorAsync(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(ApiErrorResponse.Of(code, message), Ctx.ApiErrorResponse);
    }

    private static async Task<T> ReadBodyAsync<T>(HttpRequest request, JsonTypeInfo<T> typeInfo)
    {
        if (request.ContentType is not null && !request.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(StatusCodes.Status415UnsupportedMediaType, ApiErrorCodes.BadRequest, "expected application/json");
        }

        try
        {
            return await JsonSerializer.DeserializeAsync(request.Body, typeInfo, request.HttpContext.RequestAborted).ConfigureAwait(false)
                   ?? throw ApiException.BadRequest("the request body must be a JSON object");
        }
        catch (JsonException ex)
        {
            throw ApiException.BadRequest($"invalid JSON: {ex.Message}");
        }
    }

    /// <summary>Maximum length of a desk user's Windows password in a request (CREDUI_MAX_PASSWORD_LENGTH).</summary>
    private const int MaxDeskPasswordLength = 256;

    /// <summary>
    /// A desk create/update body: the desk object plus an optional <c>password</c> (the Windows password of
    /// <c>user</c>). The password is taken out before the rest is read as <see cref="DeskConfig"/>, so it can never
    /// reach config.json; server-owned fields (<c>hasPassword</c>) are ignored.
    /// </summary>
    private static async Task<(DeskConfig Desk, string? Password)> ReadDeskBodyAsync(HttpRequest request)
    {
        if (request.ContentType is not null && !request.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(StatusCodes.Status415UnsupportedMediaType, ApiErrorCodes.BadRequest, "expected application/json");
        }

        JsonObject body;
        try
        {
            var node = await JsonNode.ParseAsync(request.Body, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }, cancellationToken: request.HttpContext.RequestAborted).ConfigureAwait(false);
            body = node as JsonObject ?? throw ApiException.BadRequest("the request body must be a JSON object");
        }
        catch (JsonException ex)
        {
            throw ApiException.BadRequest($"invalid JSON: {ex.Message}");
        }

        string? password = null;
        if (body.TryGetPropertyValue("password", out var passwordNode))
        {
            body.Remove("password");
            if (passwordNode is not null)
            {
                if (passwordNode.GetValueKind() != JsonValueKind.String)
                {
                    throw ApiException.Validation("password: must be a string");
                }

                password = passwordNode.GetValue<string>();
            }
        }

        body.Remove("hasPassword");
        if (password is { Length: 0 })
        {
            password = null;
        }

        if (password is { Length: > MaxDeskPasswordLength })
        {
            throw ApiException.Validation($"password: at most {MaxDeskPasswordLength} characters");
        }

        try
        {
            var desk = body.Deserialize(Ctx.DeskConfig) ?? throw ApiException.BadRequest("the request body must be a JSON object");
            return (desk, password);
        }
        catch (JsonException ex)
        {
            throw ApiException.BadRequest($"invalid JSON: {ex.Message}");
        }
    }

    private static async Task<byte[]> ReadUploadAsync(HttpRequest request)
    {
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(request.HttpContext.RequestAborted).ConfigureAwait(false);
            var file = form.Files.Count > 0 ? form.Files[0] : throw ApiException.BadRequest("the form has no file");
            if (file.Length > MaxImportBytes)
            {
                throw ApiException.BadRequest("the upload is too large");
            }

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, request.HttpContext.RequestAborted).ConfigureAwait(false);
            return ms.ToArray();
        }

        using var body = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, request.HttpContext.RequestAborted).ConfigureAwait(false)) > 0)
        {
            body.Write(chunk, 0, read);
            if (body.Length > MaxImportBytes)
            {
                throw ApiException.BadRequest("the upload is too large");
            }
        }

        return body.ToArray();
    }

    private static ResultMessage EnsureOk(ResultMessage result)
    {
        if (!result.Ok)
        {
            var code = result.Error?.Code ?? IpcProtocol.ErrorCodes.Internal;
            var status = code is IpcProtocol.ErrorCodes.Unsupported or IpcProtocol.ErrorCodes.UnknownType
                ? StatusCodes.Status501NotImplemented
                : StatusCodes.Status502BadGateway;
            throw new ApiException(status, ApiErrorCodes.ChildError, $"{code}: {result.Error?.Message}");
        }

        return result;
    }

    /// <summary>Accepts <c>{"clients":[{"id":"…","name":"…"}]}</c> or a bare array.</summary>
    private static List<ClientDto> ParseClients(JsonElement? data)
    {
        var list = new List<ClientDto>();
        if (data is not { } element)
        {
            return list;
        }

        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("clients", out var inner))
        {
            element = inner;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = item.TryGetProperty("id", out var idProp) ? idProp.ToString() : (item.TryGetProperty("uuid", out var uuid) ? uuid.ToString() : "");
            var name = item.TryGetProperty("name", out var nameProp) ? nameProp.ToString() : "";
            DateTimeOffset? pairedAt = (item.TryGetProperty("paired_at", out var paired) || item.TryGetProperty("pairedAt", out paired))
                                       && paired.ValueKind == JsonValueKind.String && paired.TryGetDateTimeOffset(out var at)
                ? at
                : null;
            list.Add(new ClientDto(id, name, pairedAt));
        }

        return list;
    }
}
