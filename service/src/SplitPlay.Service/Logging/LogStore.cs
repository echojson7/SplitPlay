// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json;
using SplitPlay.Core.Api;

namespace SplitPlay.Service.Logging;

/// <summary>
/// Keeps the most recent log lines in memory for <c>GET /logs</c> and appends them as JSON lines to
/// <c>logs\service.log</c> (rotated to <c>service.1.log</c> … at 10 MB).
/// </summary>
public sealed class LogStore : IDisposable
{
    private const int Capacity = 5000;
    private const long MaxFileBytes = 10 * 1024 * 1024;
    private const int KeepFiles = 5;

    private readonly Queue<LogLineDto> _lines = new();
    private readonly object _gate = new();
    private readonly string? _filePath;
    private FileStream? _file;

    public LogStore(string? logsDirectory)
    {
        if (logsDirectory is not null)
        {
            Directory.CreateDirectory(logsDirectory);
            _filePath = Path.Combine(logsDirectory, "service.log");
        }
    }

    public void Add(LogLineDto line)
    {
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > Capacity)
            {
                _lines.Dequeue();
            }

            WriteToFile(line);
        }
    }

    /// <summary>Returns up to <paramref name="tail"/> most recent lines, optionally filtered.</summary>
    public List<LogLineDto> Tail(int tail, string? component, string? desk)
    {
        lock (_gate)
        {
            IEnumerable<LogLineDto> query = _lines;
            if (!string.IsNullOrEmpty(component))
            {
                query = query.Where(l => string.Equals(l.Component, component, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrEmpty(desk))
            {
                query = query.Where(l => l.Desk == desk);
            }

            var list = query.ToList();
            return list.Skip(Math.Max(0, list.Count - tail)).ToList();
        }
    }

    private void WriteToFile(LogLineDto line)
    {
        if (_filePath is null)
        {
            return;
        }

        try
        {
            _file ??= new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var json = JsonSerializer.Serialize(line, Core.Json.SplitPlayJsonContext.Default.LogLineDto);
            _file.Write(Encoding.UTF8.GetBytes(json + "\n"));
            _file.Flush();
            if (_file.Length > MaxFileBytes)
            {
                Rotate();
            }
        }
        catch (IOException)
        {
            // Logging must never take the service down.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Rotate()
    {
        _file?.Dispose();
        _file = null;
        for (var i = KeepFiles - 1; i >= 1; i--)
        {
            var from = i == 1 ? _filePath! : RotatedName(i - 1);
            var to = RotatedName(i);
            if (File.Exists(from))
            {
                File.Move(from, to, overwrite: true);
            }
        }
    }

    private string RotatedName(int index) =>
        Path.Combine(Path.GetDirectoryName(_filePath)!, $"service.{index}.log");

    public void Dispose()
    {
        lock (_gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }
}

/// <summary>Logger provider feeding <see cref="LogStore"/>.</summary>
public sealed class LogStoreLoggerProvider(LogStore store) : ILoggerProvider
{
    /// <summary>Logger category used for lines forwarded from children over the pipe.</summary>
    public const string ChildCategory = "SplitPlay.Child";

    public ILogger CreateLogger(string categoryName) => new StoreLogger(store, categoryName);

    public void Dispose()
    {
    }

    private sealed class StoreLogger(LogStore store, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string? desk = null;
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                foreach (var (key, value) in values)
                {
                    if (key == "Desk")
                    {
                        desk = value?.ToString();
                    }
                }
            }

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message += Environment.NewLine + exception;
            }

            var component = category == ChildCategory ? "worker" : "service";
            store.Add(new LogLineDto(DateTimeOffset.UtcNow, LevelName(logLevel), component, desk, message));
        }

        private static string LevelName(LogLevel level) => level switch
        {
            LogLevel.Trace => "trace",
            LogLevel.Debug => "debug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warning",
            LogLevel.Error => "error",
            _ => "critical",
        };
    }
}
