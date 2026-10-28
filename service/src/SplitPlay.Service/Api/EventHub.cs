// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using SplitPlay.Core.Api;
using SplitPlay.Core.Json;
using SplitPlay.Service.Supervision;

namespace SplitPlay.Service.Api;

/// <summary>
/// Fans desk changes out to WebSocket clients (<c>GET /ws</c>) and pushes metrics every two seconds.
/// Each client has a small bounded queue; a slow client loses the oldest events instead of slowing the service.
/// </summary>
public sealed class EventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<byte[]>> _clients = new();
    private readonly DeskSupervisor _supervisor;

    public EventHub(DeskSupervisor supervisor)
    {
        _supervisor = supervisor;
        supervisor.DeskChanged += desk => Broadcast(JsonSerializer.SerializeToUtf8Bytes(new WsDeskEvent(desk), SplitPlayJsonContext.Default.WsDeskEvent));
    }

    public int ClientCount => _clients.Count;

    public async Task HandleAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _clients[id] = channel;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            // Initial snapshot so that a fresh client does not need a separate GET.
            foreach (var desk in _supervisor.List())
            {
                channel.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(new WsDeskEvent(desk), SplitPlayJsonContext.Default.WsDeskEvent));
            }

            var send = SendLoopAsync(socket, channel.Reader, cts.Token);
            var metrics = MetricsLoopAsync(channel.Writer, cts.Token);
            var receive = ReceiveLoopAsync(socket, cts.Token);
            await Task.WhenAny(send, receive).ConfigureAwait(false);
            await cts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(Quiet(send), Quiet(metrics), Quiet(receive)).ConfigureAwait(false);

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
        finally
        {
            _clients.TryRemove(id, out _);
            channel.Writer.TryComplete();
        }
    }

    private void Broadcast(byte[] payload)
    {
        foreach (var channel in _clients.Values)
        {
            channel.Writer.TryWrite(payload);
        }
    }

    private static async Task SendLoopAsync(WebSocket socket, ChannelReader<byte[]> reader, CancellationToken ct)
    {
        await foreach (var payload in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
    }

    private async Task MetricsLoopAsync(ChannelWriter<byte[]> writer, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var m = _supervisor.Metrics();
            writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(new WsMetricsEvent(m.Host, m.Desks), SplitPlayJsonContext.Default.WsMetricsEvent));
        }
    }

    private static async Task ReceiveLoopAsync(WebSocket socket, CancellationToken ct)
    {
        // The protocol is push-only; incoming frames are read and discarded until the client closes.
        var buffer = new byte[4096];
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }
        }
    }

    private static async Task Quiet(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ChannelClosedException)
        {
        }
    }
}
