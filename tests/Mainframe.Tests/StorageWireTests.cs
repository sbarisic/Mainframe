using System.Text;
using Mainframe.Protocol;

namespace Mainframe.Tests;

public sealed class StorageWireTests
{
    [Fact]
    public async Task PublishedAppendAndRetirementTranscriptMatchesContracts()
    {
        Frame[] frames = [new(FrameType.Request, 17, 0, ProtocolJson.Serialize(new RpcRequest("fs.write", 2, 10000, ProtocolJson.ToElement(new FsWriteV2("fixture-handle", "0", "3", Append: true))))), new(FrameType.Response, 17, 0, ProtocolJson.Serialize(new RpcResponse(true, true, ProtocolJson.ToElement(new StorageStarted([new(1, "data", "requester")])), null))), new(FrameType.WindowUpdate, 17, 1, ProtocolJson.Serialize(new WindowCredit(65536))), new(FrameType.Data, 17, 1, "abc"u8.ToArray()), new(FrameType.EndStream, 17, 1, []), new(FrameType.Complete, 17, 0, ProtocolJson.Serialize(new RpcResponse(true, false, ProtocolJson.ToElement(new FsTransferred("3")), null))), new(FrameType.Retire, 17, 0, []), new(FrameType.RetireAck, 17, 0, [])];
        using var stream = new MemoryStream();
        foreach (Frame frame in frames) await FrameCodec.WriteAsync(stream, frame);
        string hex = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "storage-write-v2-retire.hex"));
        Assert.Equal(Convert.FromHexString(string.Concat(hex.Where(c => !char.IsWhiteSpace(c)))), stream.ToArray());
    }

    [Fact]
    public async Task PublishedWriteTranscriptMatchesExplicitContracts()
    {
        Frame[] frames = [new(FrameType.Request, 17, 0, ProtocolJson.Serialize(new RpcRequest("fs.write", 1, 10000, ProtocolJson.ToElement(new FsRange("fixture-handle", "0", "3"))))), new(FrameType.Response, 17, 0, ProtocolJson.Serialize(new RpcResponse(true, true, ProtocolJson.ToElement(new StorageStarted([new(1, "data", "requester")])), null))), new(FrameType.WindowUpdate, 17, 1, ProtocolJson.Serialize(new WindowCredit(65536))), new(FrameType.Data, 17, 1, "abc"u8.ToArray()), new(FrameType.EndStream, 17, 1, []), new(FrameType.Complete, 17, 0, ProtocolJson.Serialize(new RpcResponse(true, false, ProtocolJson.ToElement(new FsTransferred("3")), null)))];
        using var bytes = new MemoryStream();
        foreach (Frame frame in frames)
        {
            await FrameCodec.WriteAsync(bytes, frame);
        }

        string fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "storage-write-v1.hex"));
        Assert.Equal(Convert.FromHexString(string.Concat(fixture.Where(c => !char.IsWhiteSpace(c)))), bytes.ToArray());
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("9223372036854775808")]
    [InlineData("1e3")]
    [InlineData(" 1")]
    public void RejectsInvalidFileNumbers(string value) => Assert.Throws<ArgumentException>(() => FileNumbers.Parse(value));
    [Fact]
    public void StorageContractsRejectDuplicateAndMissingFieldsWithoutEchoingPassword()
    {
        Assert.Throws<ProtocolException>(() => ProtocolJson.Deserialize<VolumeCreateRequest>(Encoding.UTF8.GetBytes("{\"path\":\"x\"}")));
        Assert.Throws<ProtocolException>(() => ProtocolJson.Deserialize<VolumeCreateRequest>(Encoding.UTF8.GetBytes("{\"path\":\"x\",\"password\":\"secret\",\"password\":\"secret\"}")));
        Assert.DoesNotContain("secret", new VolumeCreateRequest("x", "secret").ToString());
        Assert.Equal("secret", ProtocolJson.Deserialize<VolumeCreateRequest>(Encoding.UTF8.GetBytes("{\"path\":\"x\",\"password\":\"secret\",\"futureField\":true}")).Password);
    }
}
