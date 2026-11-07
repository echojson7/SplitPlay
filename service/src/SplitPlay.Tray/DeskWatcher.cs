// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SplitPlay.Core.Api;
using SplitPlay.Core.Ipc;

namespace SplitPlay.Tray;

/// <summary>
/// Follows the desks for the tray: polls <c>GET /api/v1/desks</c> every 2 s with a bearer token from the admin pipe, and
/// starts or stops a desk. Only the poll loop reads the list, so answers never arrive out of order; a start or stop
/// wakes it at once. <see cref="Changed"/> and <see cref="PinRequested"/> are raised through the <c>post</c> callback
/// (the tray hands in its UI thread). A user the service refuses a token (not the host's administrator) is asked again
/// only every 5 minutes, so that the refusals do not fill the service log; the tray then has no desk items and no
/// notifications, as before.
/// </summary>
internal sealed class DeskWatcher : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ServiceDownInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RefusedInterval = TimeSpan.FromMinutes(5);

    /// <summary>Failed polls in a row before the list is dropped: one timeout or a 503 at start-up keeps the menu.</summary>
    private const int FailuresBeforeEmpty = 3;

    /// <summary>A token this close to its expiry is replaced before the next request.</summary>
    private static readonly TimeSpan TokenMargin = TimeSpan.FromMinutes(1);

    private readonly Uri _api;
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
    private readonly Action<Action> _post;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private AdminToken? _token;
    private IReadOnlyList<TrayDesk>? _last;
    private int _failures;
    private volatile bool _disposed;

    /// <param name="baseUrl">The web UI's address (the API is under <c>api/v1/</c>).</param>
    /// <param name="post">Runs an action on the UI thread.</param>
    public DeskWatcher(Uri baseUrl, Action<Action> post)
    {
        _api = new Uri(baseUrl, "api/v1/");
        _post = post;
    }

    /// <summary>The latest desk list; empty until the first answer and once the service stays unreachable.</summary>
    public IReadOnlyList<TrayDesk> Desks { get; private set; } = [];

    /// <summary>The desk list changed (any field).</summary>
    public event Action? Changed;

    /// <summary>A Moonlight client started waiting for its PIN on these desks.</summary>
    public event Action<IReadOnlyList<TrayDesk>>? PinRequested;

    public void Start()
    {
        var ct = _stop.Token;
        _ = Task.Run(() => RunAsync(ct), CancellationToken.None);
    }

    /// <summary>Starts or stops a desk; returns the service's error message, or null when it accepted (or the tray quits).</summary>
    public async Task<string?> ActAsync(string deskId, bool start)
    {
        if (_disposed)
        {
            return null;
        }

        var ct = _stop.Token;
        try
        {
            using var response = await SendAsync(HttpMethod.Post, $"desks/{Uri.EscapeDataString(deskId)}/{(start ? "start" : "stop")}", ct);
            if (response.IsSuccessStatusCode)
            {
                WakeUp();
                return null;
            }

            return await ErrorMessageAsync(response);
        }
        catch (Exception ex) when (_disposed && ex is ObjectDisposedException or OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AdminPipeException or IOException)
        {
            return ex.Message;
        }
    }

    private void WakeUp()
    {
        try
        {
            _wake.Release();
        }
        catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException)
        {
            // Already signalled, or quitting.
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan next;
            try
            {
                next = await PollOnceAsync(ct) ? Interval : ServiceDownInterval;
            }
            catch (AdminPipeException ex) when (ex.Message != AdminPipe.ClosedMessage)
            {
                // The service answered and refused: this user gets no token.
                Debug.WriteLine($"SplitPlay tray: no token from the admin pipe: {ex.Message}");
                Failed(force: true);
                next = RefusedInterval;
            }
            catch (AdminPipeException)
            {
                // The pipe closed without an answer (restart, rate limit): like a service that is down.
                Failed(force: false);
                next = ServiceDownInterval;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never let the poller die: a bug or an odd answer only costs one round.
                Debug.WriteLine($"SplitPlay tray: desk poll failed: {ex}");
                Failed(force: false);
                next = ServiceDownInterval;
            }

            try
            {
                await _wake.WaitAsync(next, ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    /// <summary>One <c>GET /desks</c>. False when the service did not answer usefully (stopped, starting, no token).</summary>
    private async Task<bool> PollOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<TrayDesk> desks;
        try
        {
            using var response = await SendAsync(HttpMethod.Get, "desks", ct);
            if (!response.IsSuccessStatusCode)
            {
                Failed(force: false);
                return false;
            }

            desks = TrayDesks.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            Failed(force: false);
            return false;
        }

        _failures = 0;
        Publish(desks);
        return true;
    }

    /// <summary>A poll failed: the list is kept for a few rounds, then dropped (at once when <paramref name="force"/>).</summary>
    private void Failed(bool force)
    {
        if (force || ++_failures >= FailuresBeforeEmpty)
        {
            Publish([]);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await TokenAsync(renew: attempt > 0, ct);
            if (token is null)
            {
                // The pipe is not there: the service is stopped or still starting.
                throw new HttpRequestException("the SplitPlay service is not running");
            }

            using var request = new HttpRequestMessage(method, new Uri(_api, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                // The service restarted (tokens live in memory): get a new one and try once more.
                response.Dispose();
                continue;
            }

            return response;
        }
    }

    /// <summary>The cached token, or a new one; one request at a time (the poll loop and a menu action share it).</summary>
    private async Task<AdminToken?> TokenAsync(bool renew, CancellationToken ct)
    {
        await _tokenGate.WaitAsync(ct);
        try
        {
            if (!renew && _token is { } t && t.ExpiresAt - DateTimeOffset.UtcNow > TokenMargin)
            {
                return t;
            }

            _token = await AdminPipe.RequestAsync(AdminPipe.Kinds.Bearer, "tray", TimeSpan.FromSeconds(3), cancellationToken: ct);
            return _token;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private static async Task<string> ErrorMessageAsync(HttpResponseMessage response)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? $"HTTP {(int)response.StatusCode}";
            }
        }
        catch (JsonException)
        {
        }

        return $"HTTP {(int)response.StatusCode}";
    }

    /// <summary>Hands a new list to the UI thread; raises the events there.</summary>
    private void Publish(IReadOnlyList<TrayDesk> desks)
    {
        _post(() =>
        {
            if (_disposed)
            {
                return;
            }

            var before = _last;
            // While the service is unreachable the previous requests are kept for the comparison, so that a short
            // outage does not notify again about the same request.
            if (desks.Count > 0 || before is null)
            {
                _last = desks;
            }

            if (Desks.SequenceEqual(desks))
            {
                return;
            }

            Desks = desks;
            Changed?.Invoke();
            var fresh = TrayDesks.NewPinRequests(before, desks);
            if (fresh.Count > 0)
            {
                PinRequested?.Invoke(fresh);
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        // The loop and a pending action may still hold the semaphores and the client: they end on the cancelled token,
        // so only the token source is released here.
        _stop.Dispose();
    }
}
