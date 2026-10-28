// SPDX-License-Identifier: MIT
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SplitPlay.Core.Ipc;

namespace SplitPlay.Core.Tests;

public sealed class IpcCodecTests
{
    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static TheoryData<IpcMessage> AllMessages() => new()
    {
        new HelloMessage { Protocol = 1, Role = "worker", Desk = "desk-1", Pid = 1234, Version = "0.1.0", Session = 3 },
        new ReadyMessage { Ports = new ReadyPorts { Base = 48089 } },
        new HeartbeatMessage
        {
            Seq = 17, State = "idle",
            Metrics = new Dictionary<string, JsonElement> { ["fps"] = Json("0"), ["bitrate_kbps"] = Json("0"), ["future_key"] = Json("\"x\"") },
        },
        new LogMessage { Level = "info", Message = "hello \"world\"\n" },
        new EventMessage { Name = "client_connected", Data = Json("""{"client":"Living room PC"}""") },
        new WelcomeMessage { Protocol = 1, Profile = new IpcProfile { Fps = 120, Width = 1920, Height = 1080, BitrateKbps = 60000, Codec = "hevc" } },
        new SetProfileMessage
        {
            Id = 5,
            Profile = new IpcProfile
            {
                FecPercent = 20, MinFps = 15, DpbFrames = 16, PacingMbps = 800, GpuPriority = "high",
                GpuLuid = "0x0000000000012345", CpuSet = [4, 5, 6, 7],
            },
        },
        new PairMessage { Id = 6, Pin = "1234", Name = "Laptop" },
        new ListClientsMessage { Id = 7 },
        new UnpairMessage { Id = 8, Client = "2f0c9d3e-uuid" },
        new SendSasMessage { Id = 9 },
        new ShutdownMessage { Id = 10 },
        ResultMessage.Success(10),
        ResultMessage.Success(7, Json("""{"clients":[]}""")),
        ResultMessage.Failure(11, "unsupported", "not here"),
    };

    [Theory]
    [MemberData(nameof(AllMessages))]
    public void Every_message_round_trips(IpcMessage message)
    {
        var encoded = IpcCodec.EncodeToString(message);
        var decoded = IpcCodec.Decode(encoded);
        Assert.Equal(message.GetType(), decoded.GetType());
        Assert.Equal(encoded, IpcCodec.EncodeToString(decoded));
        Assert.StartsWith($"{{\"type\":\"{message.Type}\"", encoded, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', encoded);
    }

    [Fact]
    public void Contract_examples_decode()
    {
        var hello = Assert.IsType<HelloMessage>(IpcCodec.Decode(
            """{"type":"hello","protocol":1,"role":"worker","desk":"desk-1","pid":1234,"version":"0.1.0","session":3}"""));
        Assert.Equal("worker", hello.Role);
        Assert.Equal("desk-1", hello.Desk);
        Assert.Equal(1234, hello.Pid);
        Assert.Equal(3, hello.Session);

        var ready = Assert.IsType<ReadyMessage>(IpcCodec.Decode("""{"type":"ready","ports":{"base":48089}}"""));
        Assert.Equal(48089, ready.Ports!.Base);

        var heartbeat = Assert.IsType<HeartbeatMessage>(IpcCodec.Decode(
            """{"type":"heartbeat","seq":17,"state":"idle","metrics":{"fps":0,"bitrate_kbps":0,"encode_ms_p50":0,"encode_ms_p99":0,"loss_pct":0,"clients":0}}"""));
        Assert.Equal(17, heartbeat.Seq);
        Assert.Equal(6, heartbeat.Metrics!.Count);

        var result = Assert.IsType<ResultMessage>(IpcCodec.Decode("""{"type":"result","id":10,"ok":false,"error":{"code":"unsupported","message":"no"}}"""));
        Assert.False(result.Ok);
        Assert.Equal("unsupported", result.Error!.Code);
    }

    [Fact]
    public void Fields_are_snake_case_and_nulls_are_omitted()
    {
        var json = IpcCodec.EncodeToString(new SetProfileMessage { Id = 1, Profile = new IpcProfile { BitrateKbps = 1000, CpuSet = [1] } });
        Assert.Equal("""{"type":"set_profile","id":1,"profile":{"bitrate_kbps":1000,"cpu_set":[1]}}""", json);
    }

    [Fact]
    public void Unknown_fields_are_ignored()
    {
        var message = Assert.IsType<ShutdownMessage>(IpcCodec.Decode("""{"type":"shutdown","id":3,"future":{"a":1}}"""));
        Assert.Equal(3, message.Id);
    }

    [Fact]
    public void Unknown_types_are_surfaced_with_their_id()
    {
        var withId = Assert.IsType<UnknownIpcMessage>(IpcCodec.Decode("""{"type":"teleport","id":42}"""));
        Assert.Equal(("teleport", 42L), (withId.TypeName, withId.Id));
        var withoutId = Assert.IsType<UnknownIpcMessage>(IpcCodec.Decode("""{"type":"teleport"}"""));
        Assert.Null(withoutId.Id);
        Assert.Throws<ArgumentException>(() => IpcCodec.Encode(withId));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"id":1}""")]
    [InlineData("""{"type":5}""")]
    [InlineData("""{"type":"hello","pid":"abc"}""")]
    public void Malformed_lines_throw(string line)
    {
        Assert.Throws<IpcProtocolException>(() => IpcCodec.Decode(line));
    }

    [Fact]
    public void Oversized_messages_are_rejected()
    {
        var big = new LogMessage { Message = new string('x', IpcProtocol.MaxLineBytes) };
        Assert.Throws<IpcProtocolException>(() => IpcCodec.Encode(big));
        var line = Encoding.UTF8.GetBytes("{\"type\":\"log\",\"message\":\"" + new string('x', IpcProtocol.MaxLineBytes) + "\"}");
        Assert.Throws<IpcProtocolException>(() => IpcCodec.Decode(line));
    }

    [Fact]
    public async Task Connection_reads_multiple_lines_and_skips_blank_ones()
    {
        var text = "{\"type\":\"ready\"}\n\r\n{\"type\":\"heartbeat\",\"seq\":1}\r\n{\"type\":\"log\",\"message\":\"partial";
        await using var connection = new IpcConnection(new MemoryStream(Encoding.UTF8.GetBytes(text)));
        Assert.IsType<ReadyMessage>(await connection.ReceiveAsync());
        Assert.Equal(1, Assert.IsType<HeartbeatMessage>(await connection.ReceiveAsync()).Seq);
        // The last line has no terminating newline and is discarded at end of stream.
        Assert.Null(await connection.ReceiveAsync());
    }

    [Fact]
    public async Task Connection_rejects_lines_longer_than_64_KiB()
    {
        var text = new string(' ', IpcProtocol.MaxLineBytes + 10) + "\n";
        await using var connection = new IpcConnection(new MemoryStream(Encoding.UTF8.GetBytes(text)));
        await Assert.ThrowsAsync<IpcProtocolException>(() => connection.ReceiveAsync());
    }

    [Fact]
    public async Task Connection_writes_newline_terminated_lines()
    {
        var stream = new MemoryStream();
        await using var connection = new IpcConnection(stream, ownsStream: false);
        await connection.SendAsync(new ShutdownMessage { Id = 1 });
        await connection.SendAsync(new ListClientsMessage { Id = 2 });
        Assert.Equal("{\"type\":\"shutdown\",\"id\":1}\n{\"type\":\"list_clients\",\"id\":2}\n", Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public async Task Requests_are_correlated_with_results_over_a_named_pipe()
    {
        var name = $"sp-test-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync());

        await using var service = new IpcConnection(server, ownsStream: false);
        await using var child = new IpcConnection(client, ownsStream: false);

        var serviceReader = Task.Run(async () =>
        {
            while (await service.ReceiveAsync() is not null)
            {
            }
        });

        var childLoop = Task.Run(async () =>
        {
            while (await child.ReceiveAsync() is { } message)
            {
                if (message is ListClientsMessage list)
                {
                    await child.ReplyAsync(list.Id, ok: true, Json("""{"clients":[{"id":"c1","name":"PC"}]}"""));
                }
                else if (message is ShutdownMessage shutdown)
                {
                    await child.ReplyAsync(shutdown.Id, ok: false, errorCode: "unsupported", errorMessage: "nope");
                    return;
                }
            }
        });

        var first = await service.RequestAsync(id => new ListClientsMessage { Id = id }, TimeSpan.FromSeconds(5));
        Assert.True(first.Ok);
        Assert.Equal("c1", first.Data!.Value.GetProperty("clients")[0].GetProperty("id").GetString());

        var second = await service.RequestAsync(id => new ShutdownMessage { Id = id }, TimeSpan.FromSeconds(5));
        Assert.False(second.Ok);
        Assert.Equal("unsupported", second.Error!.Code);
        Assert.True(second.Id > first.Id);

        await childLoop;
        client.Close();
        await serviceReader.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Pipe_names_follow_the_contract()
    {
        Assert.Equal(@"\\.\pipe\splitplay\worker-desk-1", PipeNames.Full("worker", "desk-1"));
        Assert.Equal(@"splitplay\seat-desk-1", PipeNames.Local("seat", "desk-1"));
        Assert.Equal(@"splitplay\worker-desk-1", PipeNames.ToLocal(@"\\.\pipe\splitplay\worker-desk-1"));
        Assert.Equal("plain", PipeNames.ToLocal("plain"));
    }
}
