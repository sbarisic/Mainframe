using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mainframe.Protocol;
using Xunit;

namespace Mainframe.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public async Task HelloMatchesPublishedWireFixture()
    {
        string fixture = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hello-v1.hex"));
        var hello = new HelloRequest([1], [], ["unary-rpc"], "terminal", "mframe", FrameCodec.MaxPayloadBytes);
        using var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, new Frame(FrameType.Hello, 0, 0, ProtocolJson.Serialize(hello)));
        Assert.Equal(fixture.Trim(), Convert.ToHexString(stream.ToArray()));
    }

    [Fact]
    public async Task HeaderMatchesBigEndianWireFixture()
    {
        using var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, new Frame(FrameType.Request, 0x0102030405060709, 0, "{}"u8.ToArray()));
        Assert.Equal("00000002001000000102030405060709000000007B7D", Convert.ToHexString(stream.ToArray()));
    }

    [Fact]
    public async Task FragmentedAndCoalescedFramesRoundTrip()
    {
        var first = new Frame(FrameType.Request, 1, 0, "{\"method\":\"kernel.describe\"}"u8.ToArray());
        var second = new Frame(FrameType.Ping, 0, 0, [1, 2, 3, 4, 5, 6, 7, 8]);
        using var written = new MemoryStream();
        await FrameCodec.WriteAsync(written, first);
        await FrameCodec.WriteAsync(written, second);
        using var fragmented = new FragmentedStream(written.ToArray(), 3);
        Frame result = (await FrameCodec.ReadAsync(fragmented))!;
        Assert.Equal(first.Type, result.Type);
        Assert.Equal(first.ExchangeId, result.ExchangeId);
        Assert.Equal(first.Payload, result.Payload);
        result = (await FrameCodec.ReadAsync(fragmented))!;
        Assert.Equal(second.Payload, result.Payload);
        Assert.Null(await FrameCodec.ReadAsync(fragmented));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(19)]
    [InlineData(21)]
    public async Task PartialHeaderOrPayloadIsNeverCleanEof(int length)
    {
        using var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, new Frame(FrameType.Request, 1, 0, "{}"u8.ToArray()));
        using var truncated = new MemoryStream(stream.ToArray()[..length]);
        await Assert.ThrowsAsync<ProtocolException>(async () => await FrameCodec.ReadAsync(truncated));
    }

    [Fact]
    public async Task OversizedPayloadRejectedBeforeReadingItsBody()
    {
        byte[] header = Header(FrameType.Request, 1, 0, FrameCodec.MaxPayloadBytes + 1u);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<ProtocolException>(async () => await FrameCodec.ReadAsync(stream));
        Assert.Equal(20, stream.Position);
    }

    [Fact]
    public async Task NegotiatedPayloadLimitIsAppliedBeforeReadingBody()
    {
        using var stream = new MemoryStream(Header(FrameType.Request, 1, 0, 1025));
        await Assert.ThrowsAsync<ProtocolException>(async () => await FrameCodec.ReadAsync(stream, 1024));
    }

    [Theory]
    [InlineData((ushort)FrameType.Request, 0ul, 0u)]
    [InlineData((ushort)FrameType.Response, 1ul, 1u)]
    [InlineData((ushort)FrameType.Data, 1ul, 0u)]
    [InlineData((ushort)FrameType.Hello, 1ul, 0u)]
    [InlineData(65535, 0ul, 0u)]
    public void InvalidMessageTypesAndIdsAreRejected(ushort type, ulong exchange, uint channel)
    {
        byte[] header = Header((FrameType)type, exchange, channel, 2);
        Assert.Throws<ProtocolException>(() => FrameCodec.ValidateHeader(header));
    }

    [Fact]
    public void ReservedFlagsAndHeartbeatSizeAreRejected()
    {
        byte[] header = Header(FrameType.Request, 1, 0, 2);
        header[7] = 1;
        Assert.Throws<ProtocolException>(() => FrameCodec.ValidateHeader(header));
        Assert.Throws<ProtocolException>(() => FrameCodec.ValidateHeader(Header(FrameType.Ping, 0, 0, 7)));
        Assert.Throws<ProtocolException>(() => FrameCodec.ValidateHeader(Header(FrameType.EndStream, 1, 1, 1)));
    }

    [Fact]
    public void JsonUsesCamelCaseAndExplicitContracts()
    {
        var hello = new HelloRequest([1], [], ["unary-rpc"], "terminal", "mframe", FrameCodec.MaxPayloadBytes);
        byte[] bytes = ProtocolJson.Serialize(hello);
        Assert.Contains("\"maxFrameBytes\":1048576", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        HelloRequest decoded = ProtocolJson.Deserialize<HelloRequest>(bytes);
        Assert.Equal("terminal", decoded.Role);
        Assert.Equal([1], decoded.Versions);
        Assert.Throws<NotSupportedException>(() => ProtocolJson.Serialize(new { Unexpected = true }));
    }

    [Theory]
    [InlineData("{\"method\":\"kernel.health\",\"method\":\"kernel.describe\",\"version\":1,\"arguments\":{}}")]
    [InlineData("{\"method\":\"kernel.health\",\"version\":1,\"arguments\":{\"x\":1,\"\\u0078\":2}}")]
    [InlineData("{\"method\":\"kernel.health\",\"arguments\":{}}")]
    [InlineData("{\"method\":null,\"version\":1,\"arguments\":{}}")]
    [InlineData("{\"method\":\"kernel.health\",\"version\":1,\"arguments\":null}")]
    [InlineData("{\"method\":\"kernel.health\",\"version\":1,\"timeoutMs\":0,\"arguments\":{}}")]
    [InlineData("{\"method\":\"kernel.health\",\"version\":1,\"arguments\":{}")]
    public void MalformedOrIncompleteRequestsAreRejected(string json) => Assert.Throws<ProtocolException>(() => ProtocolJson.Deserialize<RpcRequest>(Encoding.UTF8.GetBytes(json)));
    [Fact]
    public void AdditiveFieldsAreAllowedWithoutTypeActivation()
    {
        RpcRequest request = ProtocolJson.Deserialize<RpcRequest>("{\"method\":\"kernel.health\",\"version\":1,\"arguments\":{},\"futureField\":{\"$type\":\"anything\"}}"u8.ToArray());
        Assert.Equal("kernel.health", request.Method);
    }

    [Fact]
    public void DepthIsBounded()
    {
        string nested = new string('[', 33) + "0" + new string(']', 33);
        Assert.Throws<ProtocolException>(() => ProtocolJson.ValidateJson(Encoding.UTF8.GetBytes(nested)));
    }

    [Fact]
    public void JsonObjectArgumentsAndResultsRoundTrip()
    {
        JsonElement element = ProtocolJson.ToElement(new JsonObject { ["path"] = "/vol/home/file.txt" });
        var request = new RpcRequest("fs.stat", 1, 5000, element);
        Assert.Equal("/vol/home/file.txt", ProtocolJson.Deserialize<RpcRequest>(ProtocolJson.Serialize(request)).Arguments.GetProperty("path").GetString());
        var response = new RpcResponse(true, false, ProtocolJson.ToElement(new JsonObject { ["healthy"] = true }), null);
        Assert.True(ProtocolJson.Deserialize<RpcResponse>(ProtocolJson.Serialize(response)).Ok);
    }

    [Theory]
    [InlineData("{\"ok\":false,\"result\":{}}")]
    [InlineData("{\"ok\":true,\"result\":{},\"error\":{\"code\":\"X\",\"message\":\"X\",\"outcome\":\"unknown\"}}")]
    [InlineData("{\"ok\":false,\"error\":{\"code\":\"X\",\"message\":\"X\",\"outcome\":\"invented\"}}")]
    public void InconsistentResponsesAreRejected(string json) => Assert.Throws<ProtocolException>(() => ProtocolJson.Deserialize<RpcResponse>(Encoding.UTF8.GetBytes(json)));
    private static byte[] Header(FrameType type, ulong exchange, uint channel, uint length)
    {
        byte[] header = new byte[20];
        BinaryPrimitives.WriteUInt32BigEndian(header, length);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)type);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8), exchange);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), channel);
        return header;
    }

    [Theory]
    [InlineData("window-update-v1.hex", FrameType.WindowUpdate)]
    [InlineData("process-start-v1.hex", FrameType.Request)]
    [InlineData("complete-v1.hex", FrameType.Complete)]
    public async Task ExecutionWireFixturesMatchContracts(string fixture, FrameType type)
    {
        object payload = type switch
        {
            FrameType.WindowUpdate => new WindowCredit(65536),
            FrameType.Request => new RpcRequest("process.start", 1, 10000, ProtocolJson.ToElement(new ProcessStartRequest("hello", ["Alice"]))),
            _ => new RpcResponse(true, false, ProtocolJson.ToElement(new ProcessExited(7)), null)
        };
        var frame = new Frame(type, 17, type == FrameType.WindowUpdate ? 1u : 0u, ProtocolJson.Serialize(payload));
        using var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, frame);
        string hex = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        Assert.Equal(Convert.FromHexString(string.Concat(hex.Where(c => !char.IsWhiteSpace(c)))), stream.ToArray());
    }

    private sealed class FragmentedStream(byte[] bytes, int chunkSize) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
    }
}
