using System.Buffers.Binary;

namespace Mainframe.Protocol;
/// <summary>Encodes v1 frames. Exchange lifecycle and role authorization belong to the connection.</summary>
public static class FrameCodec
{
    public const int HeaderSize = 20;
    public const int MaxPayloadBytes = 1_048_576;
    public static FrameHeader ValidateHeader(ReadOnlySpan<byte> header) => ValidateHeader(header, MaxPayloadBytes);
    public static FrameHeader ValidateHeader(ReadOnlySpan<byte> header, int maxPayloadBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPayloadBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPayloadBytes, MaxPayloadBytes);
        if (header.Length != HeaderSize)
            throw new ProtocolException("A frame header must contain exactly 20 bytes.");
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > maxPayloadBytes)
            throw new ProtocolException("The frame payload exceeds the negotiated limit.");
        var type = (FrameType)BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        if (BinaryPrimitives.ReadUInt16BigEndian(header[6..]) != 0)
            throw new ProtocolException("Reserved frame flags must be zero.");
        ulong exchangeId = BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
        uint channelId = BinaryPrimitives.ReadUInt32BigEndian(header[16..]);
        switch (type)
        {
            case FrameType.Hello:
            case FrameType.Welcome:
            case FrameType.Auth:
            case FrameType.AuthResult:
            case FrameType.Ping:
            case FrameType.Pong:
            case FrameType.GoAway:
                if (exchangeId != 0 || channelId != 0)
                    throw new ProtocolException("Connection frames require zero exchange and channel IDs.");
                break;
            case FrameType.Request:
            case FrameType.Response:
            case FrameType.Complete:
            case FrameType.Cancel:
            case FrameType.Retire:
            case FrameType.RetireAck:
                if (exchangeId == 0 || channelId != 0)
                    throw new ProtocolException("Exchange control requires a nonzero exchange and zero channel ID.");
                break;
            case FrameType.Data:
            case FrameType.EndStream:
            case FrameType.WindowUpdate:
                if (exchangeId == 0 || channelId == 0)
                    throw new ProtocolException("Stream frames require nonzero exchange and channel IDs.");
                break;
            default:
                throw new ProtocolException("Unknown frame type.");
        }

        if ((type is FrameType.Ping or FrameType.Pong) && length != 8)
            throw new ProtocolException("Heartbeat payloads must contain exactly eight bytes.");
        if (type is FrameType.EndStream or FrameType.Retire or FrameType.RetireAck && length != 0)
            throw new ProtocolException("END_STREAM must have an empty payload.");
        if (type == FrameType.Data && length is 0 or > 65536)
            throw new ProtocolException("DATA payload must contain 1..65536 bytes.");
        if (type is not (FrameType.Ping or FrameType.Pong or FrameType.EndStream or FrameType.Retire or FrameType.RetireAck or FrameType.Data) && length == 0)
            throw new ProtocolException("Control frames require a JSON payload.");
        return new FrameHeader((int)length, type, exchangeId, channelId);
    }

    public static ValueTask<Frame?> ReadAsync(Stream stream, CancellationToken cancellationToken = default) => ReadAsync(stream, MaxPayloadBytes, cancellationToken);
    public static async ValueTask<Frame?> ReadAsync(Stream stream, int maxPayloadBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] header = new byte[HeaderSize];
        int read = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (read == 0)
            return null;
        await ReadRemainingAsync(stream, header.AsMemory(1), cancellationToken).ConfigureAwait(false);
        FrameHeader fields = ValidateHeader(header, maxPayloadBytes);
        byte[] payload = new byte[fields.PayloadLength];
        await ReadRemainingAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return new Frame(fields.Type, fields.ExchangeId, fields.ChannelId, payload);
    }

    public static async ValueTask WriteAsync(Stream stream, Frame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(frame.Payload);
        byte[] header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)frame.Payload.Length));
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)frame.Type);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8), frame.ExchangeId);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), frame.ChannelId);
        _ = ValidateHeader(header);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ReadRemainingAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (!buffer.IsEmpty)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new ProtocolException("The connection ended inside a frame.");
            buffer = buffer[read..];
        }
    }
}
