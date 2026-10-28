// SPDX-License-Identifier: MIT
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SplitPlay.Core;
using SplitPlay.Core.Api;
using SplitPlay.Core.Configuration;
using SplitPlay.Core.Json;

namespace SplitPlay.Cli;

/// <summary>The Web API answered with an error.</summary>
internal sealed class ApiCallException(HttpStatusCode status, string code, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;

    public string Code { get; } = code;
}

/// <summary>The service could not be reached.</summary>
internal sealed class ServiceUnreachableException(string message, Exception inner) : Exception(message, inner);

/// <summary>Thin client for the loopback Web API (docs/contracts/web-api.md).</summary>
internal sealed class ApiClient : IDisposable
{
    private readonly HttpClient _http;

    public ApiClient(Uri baseUri, TimeSpan? timeout = null)
    {
        // Loopback only: never use a system proxy for 127.0.0.1.
        var handler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(5) };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUri.ToString().TrimEnd('/') + BuildInfo.ApiPrefix + "/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("splitplay-cli", BuildInfo.Version));
    }

    public Uri BaseAddress => _http.BaseAddress!;

    /// <summary>
    /// Resolves the service URL: <c>--url</c>, then <c>SPLITPLAY_URL</c>, then <c>host.webPort</c> from the local
    /// config.json, then the default port.
    /// </summary>
    public static Uri ResolveBaseUri(string? url, string? dataRoot)
    {
        url ??= Environment.GetEnvironmentVariable("SPLITPLAY_URL");
        if (!string.IsNullOrWhiteSpace(url))
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var u) ? u : throw new UsageException($"invalid --url '{url}'");
        }

        var port = BuildInfo.DefaultWebPort;
        try
        {
            var root = dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), BuildInfo.ServiceName);
            var file = Path.Combine(root, ConfigStore.FileName);
            if (File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("host", out var host) && host.TryGetProperty("webPort", out var p) && p.TryGetInt32(out var configured))
                {
                    port = configured;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fall back to the default port.
        }

        return new Uri($"http://127.0.0.1:{port}/");
    }

    public Task<HealthDto> HealthAsync() => GetAsync("health", SplitPlayJsonContext.Default.HealthDto);

    public Task<List<DeskDto>> ListDesksAsync() => GetAsync("desks", SplitPlayJsonContext.Default.ListDeskDto);

    public Task<DeskDto> GetDeskAsync(string id) => GetAsync($"desks/{Uri.EscapeDataString(id)}", SplitPlayJsonContext.Default.DeskDto);

    public Task<DeskDto> CreateDeskAsync(DeskConfig desk) =>
        SendAsync(HttpMethod.Post, "desks", Json(desk, SplitPlayJsonContext.Default.DeskConfig), SplitPlayJsonContext.Default.DeskDto);

    public Task<DeskDto> StartDeskAsync(string id) =>
        SendAsync(HttpMethod.Post, $"desks/{Uri.EscapeDataString(id)}/start", null, SplitPlayJsonContext.Default.DeskDto);

    public Task<DeskDto> StopDeskAsync(string id) =>
        SendAsync(HttpMethod.Post, $"desks/{Uri.EscapeDataString(id)}/stop", null, SplitPlayJsonContext.Default.DeskDto);

    public async Task DeleteDeskAsync(string id)
    {
        using var response = await SendRawAsync(HttpMethod.Delete, $"desks/{Uri.EscapeDataString(id)}", null).ConfigureAwait(false);
    }

    public async Task<byte[]> ExportConfigAsync()
    {
        using var response = await SendRawAsync(HttpMethod.Get, "config/export", null).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }

    public Task<List<DeskDto>> ImportConfigAsync(byte[] zip)
    {
        var content = new ByteArrayContent(zip);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return SendAsync(HttpMethod.Post, "config/import", content, SplitPlayJsonContext.Default.ListDeskDto);
    }

    private static StringContent Json<T>(T value, JsonTypeInfo<T> info) =>
        new(JsonSerializer.Serialize(value, info), Encoding.UTF8, "application/json");

    private Task<T> GetAsync<T>(string path, JsonTypeInfo<T> info) => SendAsync(HttpMethod.Get, path, null, info);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, HttpContent? content, JsonTypeInfo<T> info)
    {
        using var response = await SendRawAsync(method, path, content).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, info).ConfigureAwait(false)
               ?? throw new ApiCallException(response.StatusCode, "bad_response", "empty response");
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, HttpContent? content)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ServiceUnreachableException($"cannot reach the SplitPlay service at {_http.BaseAddress}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new ServiceUnreachableException($"the SplitPlay service at {_http.BaseAddress} did not answer in time", ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            try
            {
                var error = JsonSerializer.Deserialize(body, SplitPlayJsonContext.Default.ApiErrorResponse);
                if (error?.Error is { } e)
                {
                    throw new ApiCallException(response.StatusCode, e.Code, e.Message);
                }
            }
            catch (JsonException)
            {
            }

            throw new ApiCallException(response.StatusCode, "http_" + (int)response.StatusCode, string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase ?? "" : body);
        }
    }

    public void Dispose() => _http.Dispose();
}
