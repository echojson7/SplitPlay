// SPDX-License-Identifier: MIT
using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SplitPlay.Core.Json;

namespace SplitPlay.Core.Ipc;

/// <summary>A line violates the JSON-lines protocol (not JSON, no type, too long, …).</summary>
public sealed class IpcProtocolException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Encodes and decodes control-pipe messages: one UTF-8 JSON object per line, <c>"type"</c> first, snake_case fields,
/// at most <see cref="IpcProtocol.MaxLineBytes"/> bytes per line.
/// </summary>
public static class IpcCodec
{
    private static readonly IpcJsonContext Ctx = IpcJsonContext.Default;

    private static readonly Dictionary<string, JsonTypeInfo> Decoders = new(StringComparer.Ordinal)
    {
        ["hello"] = Ctx.HelloMessage,
        ["ready"] = Ctx.ReadyMessage,
        ["heartbeat"] = Ctx.HeartbeatMessage,
        ["log"] = Ctx.LogMessage,
        ["event"] = Ctx.EventMessage,
        ["welcome"] = Ctx.WelcomeMessage,
        ["set_profile"] = Ctx.SetProfileMessage,
        ["pair"] = Ctx.PairMessage,
        ["list_clients"] = Ctx.ListClientsMessage,
        ["unpair"] = Ctx.UnpairMessage,
        ["send_sas"] = Ctx.SendSasMessage,
        ["shutdown"] = Ctx.ShutdownMessage,
        ["result"] = Ctx.ResultMessage,
    };

    /// <summary>All message types this build understands.</summary>
    public static IReadOnlyCollection<string> KnownTypes => Decoders.Keys;

    /// <summary>The wire type name of a message.</summary>
    public static string TypeNameOf(IpcMessage message) => message switch
    {
        HelloMessage => "hello",
        ReadyMessage => "ready",
        HeartbeatMessage => "heartbeat",
        LogMessage => "log",
        EventMessage => "event",
        WelcomeMessage => "welcome",
        SetProfileMessage => "set_profile",
        PairMessage => "pair",
        ListClientsMessage => "list_clients",
        UnpairMessage => "unpair",
        SendSasMessage => "send_sas",
        ShutdownMessage => "shutdown",
        ResultMessage => "result",
        UnknownIpcMessage u => u.TypeName,
        _ => throw new ArgumentException($"unsupported message {message.GetType().Name}", nameof(message)),
    };

    /// <summary>Encodes a message as UTF-8 JSON without the trailing newline.</summary>
    /// <exception cref="IpcProtocolException">The encoded line would exceed the line limit.</exception>
    public static byte[] Encode(IpcMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message is UnknownIpcMessage)
        {
            throw new ArgumentException("unknown messages cannot be encoded", nameof(message));
        }

        var body = JsonSerializer.SerializeToElement(message, TypeInfoOf(message));
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("type", message.Type);
            foreach (var property in body.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        if (buffer.WrittenCount > IpcProtocol.MaxLineBytes)
        {
            throw new IpcProtocolException($"encoded '{message.Type}' message is {buffer.WrittenCount} bytes, above the {IpcProtocol.MaxLineBytes} byte limit");
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Encodes a message including the terminating <c>\n</c>.</summary>
    public static byte[] EncodeLine(IpcMessage message)
    {
        var json = Encode(message);
        var line = new byte[json.Length + 1];
        json.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        return line;
    }

    /// <summary>Encodes a message as a string (diagnostics and tests).</summary>
    public static string EncodeToString(IpcMessage message) => Encoding.UTF8.GetString(Encode(message));

    /// <summary>
    /// Decodes one line (without its newline). Unknown types yield <see cref="UnknownIpcMessage"/>; unknown fields are ignored.
    /// </summary>
    /// <exception cref="IpcProtocolException">The line is not a JSON object with a string <c>type</c>.</exception>
    public static IpcMessage Decode(ReadOnlySpan<byte> line)
    {
        if (line.Length > IpcProtocol.MaxLineBytes)
        {
            throw new IpcProtocolException($"line is {line.Length} bytes, above the {IpcProtocol.MaxLineBytes} byte limit");
        }

        JsonDocument document;
        try
        {
            var reader = new Utf8JsonReader(line, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
            document = JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException ex)
        {
            throw new IpcProtocolException($"line is not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new IpcProtocolException("line is not a JSON object");
            }

            if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            {
                throw new IpcProtocolException("message has no string \"type\"");
            }

            var type = typeElement.GetString()!;
            if (!Decoders.TryGetValue(type, out var typeInfo))
            {
                long? id = root.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var value) ? value : null;
                return new UnknownIpcMessage { TypeName = type, Id = id };
            }

            try
            {
                return (IpcMessage)root.Deserialize(typeInfo)!;
            }
            catch (JsonException ex)
            {
                throw new IpcProtocolException($"'{type}' message has invalid fields: {ex.Message}", ex);
            }
            catch (InvalidOperationException ex)
            {
                throw new IpcProtocolException($"'{type}' message has invalid fields: {ex.Message}", ex);
            }
        }
    }

    /// <summary>Decodes a line given as a string.</summary>
    public static IpcMessage Decode(string line) => Decode(Encoding.UTF8.GetBytes(line));

    private static JsonTypeInfo TypeInfoOf(IpcMessage message) =>
        Decoders.TryGetValue(message.Type, out var info) ? info : throw new ArgumentException($"no encoder for {message.Type}");
}
