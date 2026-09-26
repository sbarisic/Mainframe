namespace Mainframe.Protocol;

public enum FrameType : ushort
{
    Hello = 0x0001,
    Welcome = 0x0002,
    Auth = 0x0003,
    AuthResult = 0x0004,
    Request = 0x0010,
    Response = 0x0011,
    Complete = 0x0012,
    Cancel = 0x0013,
    Data = 0x0020,
    EndStream = 0x0021,
    WindowUpdate = 0x0022,
    Ping = 0x0030,
    Pong = 0x0031,
    GoAway = 0x0032
}

public sealed record Frame(FrameType Type, ulong ExchangeId, uint ChannelId, byte[] Payload);
public readonly record struct FrameHeader(int PayloadLength, FrameType Type, ulong ExchangeId, uint ChannelId);
public sealed class ProtocolException : IOException
{
    public ProtocolException(string message) : base(message)
    {
    }

    public ProtocolException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
