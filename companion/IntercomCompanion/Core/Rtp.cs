using System.Buffers.Binary;

namespace IntercomCompanion.Core;

/// <summary>Minimal RFC 3550 RTP framing for agreed IMA ADPCM payload type 96.</summary>
internal sealed record RtpPacket(ushort Sequence, uint Timestamp, uint Ssrc, byte[] Payload);

internal static class Rtp
{
    public const int Port = 45679;
    public const byte AdpcmPayloadType = 96;
    public const int HeaderLength = 12;
    public const int AdpcmPayloadLength = 164;
    public const uint TimestampStep = 320; // Native 16 kHz sample clock, 20 ms/frame

    public static byte[] Pack(uint ssrc, ushort sequence, uint timestamp, ReadOnlySpan<byte> payload)
    {
        var datagram = new byte[HeaderLength + payload.Length];
        datagram[0] = 0x80;
        datagram[1] = AdpcmPayloadType;
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(8), ssrc);
        payload.CopyTo(datagram.AsSpan(HeaderLength));
        return datagram;
    }

    public static bool TryParse(ReadOnlySpan<byte> datagram, out RtpPacket? packet)
    {
        packet = null;
        if (datagram.Length != HeaderLength + AdpcmPayloadLength || datagram[0] != 0x80 ||
            (datagram[1] & 0x7f) != AdpcmPayloadType) return false;
        packet = new RtpPacket(BinaryPrimitives.ReadUInt16BigEndian(datagram[2..]),
            BinaryPrimitives.ReadUInt32BigEndian(datagram[4..]),
            BinaryPrimitives.ReadUInt32BigEndian(datagram[8..]), datagram[HeaderLength..].ToArray());
        return true;
    }
}
