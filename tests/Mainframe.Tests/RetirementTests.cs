using System.Net;
using System.Net.Sockets;
using Mainframe.Protocol;

namespace Mainframe.Tests;

public sealed class RetirementTests
{
    [Fact]
    public async Task CancellationRacesRetireWithoutLosingBufferedOutput()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient { NoDelay = true };
        Task connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using TcpClient remote = await listener.AcceptTcpClientAsync();
        remote.NoDelay = true;
        await connecting;
        listener.Stop();
        await using var server = new WireConnection(remote.GetStream(), false, retirement: true);
        await using var caller = new WireConnection(client.GetStream(), true, retirement: true);
        server.RequestHandler = async (exchange, _) =>
        {
            await exchange.ReplyAsync(new(true, true, ProtocolJson.ToElement(new StorageStarted([new(1, "input", "requester"), new(2, "output", "responder")])), null));
            await exchange.Channel(2).SendAsync(new byte[] { 7, 8, 9 });
            try { await Task.Delay(Timeout.Infinite, exchange.Cancelled); }
            catch (OperationCanceledException) { }
            await exchange.Channel(2).EndAsync();
            await exchange.CompleteAsync(new(true, false, ProtocolJson.ToElement(new FsTransferred("3")), null));
        };
        server.Start();
        caller.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        for (int index = 0; index < 100; index++)
        {
            WireExchange exchange = await caller.RequestAsync(new("test", 1, null, ProtocolJson.ToElement(new EmptyArguments())), timeout.Token);
            await exchange.Response.Task.WaitAsync(timeout.Token);
            Task writing = exchange.Channel(1).SendAsync(new byte[65536], timeout.Token);
            await exchange.CancelAsync();
            await exchange.Completion.Task.WaitAsync(timeout.Token);
            try { await writing; }
            catch (Exception error) when (error is IOException or OperationCanceledException) { }
            await exchange.CancelAsync(); // Must not emit late traffic after retirement.
            byte[] output = new byte[3];
            await exchange.Channel(2).Input.ReadExactlyAsync(output, timeout.Token);
            Assert.Equal(new byte[] { 7, 8, 9 }, output);
            Assert.InRange(caller.BufferedPayloadBytes, 0, 16 * 1024 * 1024);
        }
        while (caller.RetainedExchangeCount != 0 || server.RetainedExchangeCount != 0) await Task.Delay(1, timeout.Token);
        Assert.False(caller.Closed.IsCancellationRequested);
        Assert.Equal(0, caller.BufferedPayloadBytes);
    }

    [Fact]
    public async Task RetiresTenThousandExchangesWithBoundedRecords()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient { NoDelay = true };
        Task connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using TcpClient remote = await listener.AcceptTcpClientAsync();
        remote.NoDelay = true;
        await connecting;
        listener.Stop();
        await using var server = new WireConnection(remote.GetStream(), false, retirement: true);
        await using var caller = new WireConnection(client.GetStream(), true, retirement: true);
        server.RequestHandler = (exchange, _) => exchange.ReplyAsync(new(true, false, ProtocolJson.ToElement(new EmptyArguments()), null));
        server.Start();
        caller.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        for (int i = 0; i < 10050; i++)
        {
            WireExchange exchange = await caller.RequestAsync(new("test", 1, null, ProtocolJson.ToElement(new EmptyArguments())), deadline.Token);
            Assert.True((await exchange.Response.Task.WaitAsync(deadline.Token)).Ok);
            Assert.InRange(caller.RetainedExchangeCount, 0, 128);
            Assert.InRange(server.RetainedExchangeCount, 0, 128);
            Assert.InRange(caller.BufferedPayloadBytes, 0, 16 * 1024 * 1024);
        }
        while (caller.RetainedExchangeCount != 0 || server.RetainedExchangeCount != 0) await Task.Delay(1, deadline.Token);
        Assert.False(caller.Closed.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiredFramesAndUnnegotiatedRetirementAreRejected(bool negotiated)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient { NoDelay = true };
        Task connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using TcpClient remote = await listener.AcceptTcpClientAsync();
        await connecting;
        listener.Stop();
        await using var server = new WireConnection(remote.GetStream(), false, retirement: negotiated);
        server.RequestHandler = (exchange, _) => exchange.ReplyAsync(new(true, false, ProtocolJson.ToElement(new EmptyArguments()), null));
        server.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Stream stream = client.GetStream();
        await FrameCodec.WriteAsync(stream, new(FrameType.Request, 1, 0, ProtocolJson.Serialize(new RpcRequest("test", 1, null, ProtocolJson.ToElement(new EmptyArguments())))), deadline.Token);
        Assert.Equal(FrameType.Response, (await FrameCodec.ReadAsync(stream, deadline.Token))!.Type);
        await FrameCodec.WriteAsync(stream, new(FrameType.Retire, 1, 0, []), deadline.Token);
        if (negotiated)
        {
            Assert.Equal(FrameType.RetireAck, (await FrameCodec.ReadAsync(stream, deadline.Token))!.Type);
            await FrameCodec.WriteAsync(stream, new(FrameType.Cancel, 1, 0, "{}"u8.ToArray()), deadline.Token);
        }
        await server.Completion.WaitAsync(deadline.Token);
        Assert.True(server.Closed.IsCancellationRequested);
    }
}
