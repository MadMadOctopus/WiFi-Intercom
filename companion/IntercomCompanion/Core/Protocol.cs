using System.Buffers.Binary;

namespace IntercomCompanion.Core;

internal enum PacketType : byte
{
    Claim = 1,
    Busy = 2,
    Audio = 3,
    End = 4,
    Hello = 5,
    Heartbeat = 6,
    ConfigGet = 7,
    ConfigSet = 8,
    ConfigReply = 9,
}

internal sealed record IntercomPacket(
    PacketType Type,
    byte Flags,
    uint SenderId,
    uint SessionId,
    uint Sequence,
    uint TimestampMs,
    byte[] Payload);

/// <summary>Exact big-endian PTT1 datagram codec shared with firmware.</summary>
internal static class Protocol
{
    public const uint MeshId = 0x4D455348; // "MESH"
    public const int Port = 45678;
    public const int HeaderLength = 32;
    public const int AdpcmPayloadLength = 164;
    public const byte DirectedFlag = 0x01;

    private static ReadOnlySpan<byte> Magic => "PTT1"u8;

    public static byte[] Pack(uint senderId, PacketType type, uint sessionId,
                              uint sequence, uint timestampMs,
                              ReadOnlySpan<byte> payload, byte flags = 0)
    {
        var datagram = new byte[HeaderLength + payload.Length];
        Magic.CopyTo(datagram);
        datagram[4] = (byte)type;
        datagram[5] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(6), HeaderLength);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(8), MeshId);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(12), senderId);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(16), sessionId);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(20), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(24), timestampMs);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(28), checked((ushort)payload.Length));
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(30), 0);
        payload.CopyTo(datagram.AsSpan(HeaderLength));
        return datagram;
    }

    public static bool TryParse(ReadOnlySpan<byte> datagram, uint ownNodeId,
                                out IntercomPacket? packet)
    {
        packet = null;
        if (datagram.Length < HeaderLength || !datagram[..4].SequenceEqual(Magic) ||
            BinaryPrimitives.ReadUInt16BigEndian(datagram[6..]) != HeaderLength ||
            BinaryPrimitives.ReadUInt32BigEndian(datagram[8..]) != MeshId)
            return false;

        var sender = BinaryPrimitives.ReadUInt32BigEndian(datagram[12..]);
        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(datagram[28..]);
        if (sender == ownNodeId || datagram.Length != HeaderLength + payloadLength)
            return false;

        packet = new IntercomPacket(
            (PacketType)datagram[4], datagram[5], sender,
            BinaryPrimitives.ReadUInt32BigEndian(datagram[16..]),
            BinaryPrimitives.ReadUInt32BigEndian(datagram[20..]),
            BinaryPrimitives.ReadUInt32BigEndian(datagram[24..]),
            datagram[HeaderLength..].ToArray());
        return true;
    }
}
