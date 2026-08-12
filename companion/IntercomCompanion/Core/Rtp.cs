using System.Buffers.Binary;

namespace IntercomCompanion.Core;

/// <summary>Minimal RFC 3550 RTP framing for static G.722 payload type 9.</summary>
internal sealed record RtpPacket(ushort Sequence, uint Timestamp, uint Ssrc, byte[] Payload);

internal static class Rtp
{
    public const int Port = 45679;
    public const byte G722PayloadType = 9;
    public const int HeaderLength = 12;
    public const int G722PayloadLength = 160; // 64 kb/s × 20 ms
    public const uint TimestampStep = 160;    // RTP G.722 clock is 8 kHz

    public static byte[] Pack(uint ssrc, ushort sequence, uint timestamp, ReadOnlySpan<byte> payload)
    {
        var datagram = new byte[HeaderLength + payload.Length];
        datagram[0] = 0x80;
        datagram[1] = G722PayloadType;
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(8), ssrc);
        payload.CopyTo(datagram.AsSpan(HeaderLength));
        return datagram;
    }

    public static bool TryParse(ReadOnlySpan<byte> datagram, out RtpPacket? packet)
    {
        packet = null;
        if (datagram.Length != HeaderLength + G722PayloadLength || datagram[0] != 0x80 ||
            (datagram[1] & 0x7f) != G722PayloadType) return false;
        packet = new RtpPacket(BinaryPrimitives.ReadUInt16BigEndian(datagram[2..]),
            BinaryPrimitives.ReadUInt32BigEndian(datagram[4..]),
            BinaryPrimitives.ReadUInt32BigEndian(datagram[8..]), datagram[HeaderLength..].ToArray());
        return true;
    }
}
