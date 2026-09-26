using System.Net;
using System.Net.Sockets;
using Mainframe.Protocol;

namespace Mainframe.Tests;

public sealed class StreamingProtocolTests : IAsyncLifetime
{
    private TcpClient client = null!, accepted = null!;
    private WireConnection server = null!;
    private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
    private Stream Stream => client.GetStream();

    public async Task InitializeAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        client = new TcpClient();
        Task connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        accepted = await listener.AcceptTcpClientAsync();
        await connecting;
        listener.Stop();
        server = new(accepted.GetStream(), false);
    }

    public async Task DisposeAsync()
    {
        await server.DisposeAsync();
        client.Dispose();
        accepted.Dispose();
        deadline.Dispose();
    }

    private Task Send(FrameType type, ulong id, uint channel, byte[] payload) => FrameCodec.WriteAsync(Stream, new(type, id, channel, payload), deadline.Token).AsTask();
    private Task Request(ulong id = 1) => Send(FrameType.Request, id, 0, ProtocolJson.Serialize(new RpcRequest("test", 1, null, ProtocolJson.ToElement(new EmptyArguments()))));
    private async Task<Frame> Read() => await FrameCodec.ReadAsync(Stream, deadline.Token) ?? throw new IOException("EOF");
    private void Streaming(Func<WireExchange, Task>? afterReply = null)
    {
        server.RequestHandler = async (exchange, request) =>
        {
            await exchange.ReplyAsync(new(true, true, ProtocolJson.ToElement(new ProcessStarted("test", [new(1, "stdin", "requester"), new(2, "stdout", "responder")])), null));
            if (afterReply is not null)
                await afterReply(exchange);
        };
        server.Start();
    }

    [Fact]
    public async Task CreditOverrunClosesTheConnection()
    {
        Streaming();
        await Request();
        Assert.Equal(FrameType.Response, (await Read()).Type);
        Frame window = await Read();
        Assert.Equal(FrameType.WindowUpdate, window.Type);
        int credit = ProtocolJson.Deserialize<WindowCredit>(window.Payload).Credit;
        await Send(FrameType.Data, 1, 1, new byte[credit]);
        await Send(FrameType.Data, 1, 1, [1]);
        await server.Completion.WaitAsync(deadline.Token);
        Assert.True(server.Closed.IsCancellationRequested);
    }

    [Fact]
    public async Task UndeclaredChannelAndDuplicateResponseAreProtocolErrors()
    {
        Streaming();
        await Request();
        await Read();
        await Read();
        await Send(FrameType.EndStream, 1, 4, []);
        await server.Completion.WaitAsync(deadline.Token);
        Assert.True(server.Closed.IsCancellationRequested);
    }

    [Fact]
    public async Task LateAuthorizedInputIsDrainedAfterCompletion()
    {
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Streaming(async exchange =>
        {
            await finish.Task;
            await exchange.Channel(2).EndAsync();
            await exchange.CompleteAsync(new(true, false, ProtocolJson.ToElement(new ProcessExited(0)), null));
        });
        await Request();
        await Read();
        var credit = ProtocolJson.Deserialize<WindowCredit>((await Read()).Payload).Credit;
        finish.SetResult();
        Assert.Equal(FrameType.EndStream, (await Read()).Type);
        Assert.Equal(FrameType.Complete, (await Read()).Type);
        await Send(FrameType.Data, 1, 1, new byte[credit]);
        await Send(FrameType.EndStream, 1, 1, []);
        await Send(FrameType.Cancel, 1, 0, "{}"u8.ToArray());
        await Send(FrameType.Ping, 0, 0, new byte[8]);
        Assert.Equal(FrameType.Pong, (await Read()).Type);
        await Send(FrameType.Data, 1, 1, [1]);
        await server.Completion.WaitAsync(deadline.Token);
        Assert.True(server.Closed.IsCancellationRequested);
    }

    [Fact]
    public async Task DataWaitsForCreditAndCompletionFollowsDataAndEof()
    {
        Streaming(async exchange =>
        {
            await exchange.Channel(2).SendAsync("output"u8.ToArray());
            await exchange.Channel(2).EndAsync();
            await exchange.CompleteAsync(new(true, false, ProtocolJson.ToElement(new ProcessExited(8)), null));
        });
        await Request();
        Assert.Equal(FrameType.Response, (await Read()).Type);
        Assert.Equal(FrameType.WindowUpdate, (await Read()).Type);
        await Send(FrameType.Ping, 0, 0, new byte[8]);
        Assert.Equal(FrameType.Pong, (await Read()).Type);
        await Send(FrameType.WindowUpdate, 1, 2, ProtocolJson.Serialize(new WindowCredit(6)));
        Assert.Equal("output"u8.ToArray(), (await Read()).Payload);
        Assert.Equal(FrameType.EndStream, (await Read()).Type);
        Assert.Equal(FrameType.Complete, (await Read()).Type);
    }

    [Fact]
    public async Task ExcessOutstandingCreditIsRejected()
    {
        Streaming();
        await Request();
        await Read();
        await Read();
        await Send(FrameType.WindowUpdate, 1, 2, ProtocolJson.Serialize(new WindowCredit(262144)));
        await Send(FrameType.WindowUpdate, 1, 2, ProtocolJson.Serialize(new WindowCredit(1)));
        await server.Completion.WaitAsync(deadline.Token);
        Assert.True(server.Closed.IsCancellationRequested);
    }

    [Fact]
    public async Task ActiveExchangeLimitIsBounded()
    {
        server.RequestHandler = (_, _) => Task.CompletedTask;
        server.Start();
        for (ulong id = 1; id <= 257; id += 2)
            await Request(id);
        await server.Completion.WaitAsync(deadline.Token);
        Assert.True(server.Closed.IsCancellationRequested);
    }

    [Fact]
    public async Task ReceiveCreditReservesCapacityAcrossManyChannels()
    {
        server.RequestHandler = (exchange, _) => exchange.ReplyAsync(new(true, true, ProtocolJson.ToElement(new ProcessStarted("test", Enumerable.Range(1, 8).Select(i => new StreamDescriptor((uint)i, "in" + i, "requester")).ToArray())), null));
        server.Start();
        for (ulong id = 1; id <= 63; id += 2)
            await Request(id);
        int replies = 0, grants = 0;
        while (replies < 32)
        {
            Frame frame = await Read();
            if (frame.Type == FrameType.Response)
                replies++;
            else if (frame.Type == FrameType.WindowUpdate)
                grants++;
        }

        await Send(FrameType.Ping, 0, 0, new byte[8]);
        while (true)
        {
            Frame frame = await Read();
            if (frame.Type == FrameType.Pong)
                break;
            if (frame.Type == FrameType.WindowUpdate)
                grants++;
        }

        Assert.InRange(grants, 1, 96);
        Assert.InRange(server.BufferedPayloadBytes, 65536, 16 * 1024 * 1024);
        Assert.False(server.Closed.IsCancellationRequested);
    }

    [Fact]
    public async Task CompletedExchangeBookkeepingClosesAtPublishedLimit()
    {
        server.RequestHandler = (exchange, _) => exchange.ReplyAsync(new(true, false, ProtocolJson.ToElement(new EmptyArguments()), null));
        server.Start();
        for (ulong id = 1; id < 8193; id += 2)
        {
            await Request(id);
            Assert.Equal(FrameType.Response, (await Read()).Type);
        }

        await Request(8193);
        Assert.Equal(FrameType.GoAway, (await Read()).Type);
        await server.Completion.WaitAsync(deadline.Token);
        Assert.True(server.Closed.IsCancellationRequested);
    }
}
