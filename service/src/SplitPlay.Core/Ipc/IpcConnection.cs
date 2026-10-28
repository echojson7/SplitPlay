// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Text.Json;

namespace SplitPlay.Core.Ipc;

/// <summary>
/// JSON-lines framing over a duplex stream (a named pipe in production). Reads are single-consumer; writes are
/// serialized internally and may come from any thread. Also correlates requests with their results.
/// </summary>
public sealed class IpcConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _buffer = new byte[IpcProtocol.MaxLineBytes + 1];
    private readonly ConcurrentDictionary<long, TaskCompletionSource<ResultMessage>> _pending = new();
    private int _start;
    private int _end;
    private long _nextId;
    private int _disposed;

    public IpcConnection(Stream stream, bool ownsStream = true)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _ownsStream = ownsStream;
    }

    /// <summary>Allocates a request id unique for this sender.</summary>
    public long NextId() => Interlocked.Increment(ref _nextId);

    /// <summary>Writes one message followed by <c>\n</c> and flushes.</summary>
    public async Task SendAsync(IpcMessage message, CancellationToken cancellationToken = default)
    {
        var line = IpcCodec.EncodeLine(message);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Sends a request built by <paramref name="build"/> (which receives a fresh id) and waits for its result.
    /// The result arrives through <see cref="ReceiveAsync"/>, which someone must be running concurrently.
    /// </summary>
    public async Task<ResultMessage> RequestAsync(Func<long, IpcMessage> build, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var id = NextId();
        var message = build(id);
        if (message is not IIpcRequest request || request.Id != id)
        {
            throw new ArgumentException("the builder must return a request carrying the given id", nameof(build));
        }

        var tcs = new TaskCompletionSource<ResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(message, cancellationToken).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Reads the next message. Results for pending requests are completed here and also returned.
    /// Returns null when the peer closed the stream.
    /// </summary>
    /// <exception cref="IpcProtocolException">A line is malformed or too long.</exception>
    public async Task<IpcMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                FailPending(new EndOfStreamException("the pipe was closed"));
                return null;
            }

            if (line.Value.Length == 0)
            {
                continue;
            }

            var message = IpcCodec.Decode(line.Value.Span);
            if (message is ResultMessage result && _pending.TryRemove(result.Id, out var tcs))
            {
                tcs.TrySetResult(result);
            }

            return message;
        }
    }

    /// <summary>Answers a request from the peer.</summary>
    public Task ReplyAsync(long id, bool ok, JsonElement? data = null, string? errorCode = null, string? errorMessage = null,
        CancellationToken cancellationToken = default) =>
        SendAsync(ok ? ResultMessage.Success(id, data) : ResultMessage.Failure(id, errorCode ?? IpcProtocol.ErrorCodes.Internal, errorMessage ?? ""),
            cancellationToken);

    private async Task<ReadOnlyMemory<byte>?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newline >= 0)
            {
                var length = newline - _start;
                if (length > IpcProtocol.MaxLineBytes)
                {
                    throw new IpcProtocolException($"line exceeds {IpcProtocol.MaxLineBytes} bytes");
                }

                var lineEnd = length > 0 && _buffer[newline - 1] == (byte)'\r' ? newline - 1 : newline;
                var line = _buffer.AsSpan(_start, lineEnd - _start).ToArray();
                _start = newline + 1;
                return line;
            }

            // Compact, then read more.
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            if (_end == _buffer.Length)
            {
                throw new IpcProtocolException($"line exceeds {IpcProtocol.MaxLineBytes} bytes");
            }

            var read = await _stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                // A final unterminated line is discarded: every message must end with \n.
                return null;
            }

            _end += read;
        }
    }

    private void FailPending(Exception error)
    {
        foreach (var (id, tcs) in _pending)
        {
            if (_pending.TryRemove(id, out _))
            {
                tcs.TrySetException(error);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        FailPending(new ObjectDisposedException(nameof(IpcConnection)));
        if (_ownsStream)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        _writeLock.Dispose();
    }
}
