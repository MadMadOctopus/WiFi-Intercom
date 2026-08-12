using System.Buffers.Binary;

namespace IntercomCompanion.Core;

/// <summary>Minimal RFC 3550 RTP framing for agreed Opus payload type 111.</summary>
internal sealed record RtpPacket(ushort Sequence, uint Timestamp, uint Ssrc, byte[] Payload);

internal static class Rtp
{
    public const int Port = 45679;
    public const byte OpusPayloadType = 111;
    public const int HeaderLength = 12;
    // Espressif's 16 kHz / 20 ms encoder requests 220 bytes; 256 keeps all
    // device and PC RTP buffers compatible. Normal CBR packets are 120 bytes.
    public const int OpusMaxPayloadLength = 256;
    public const uint TimestampStep = 960; // Opus RTP always uses a 48 kHz clock

    public static byte[] Pack(uint ssrc, ushort sequence, uint timestamp, ReadOnlySpan<byte> payload)
    {
        var datagram = new byte[HeaderLength + payload.Length];
        datagram[0] = 0x80;
        datagram[1] = OpusPayloadType;
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(8), ssrc);
        payload.CopyTo(datagram.AsSpan(HeaderLength));
        return datagram;
    }

    public static bool TryParse(ReadOnlySpan<byte> datagram, out RtpPacket? packet)
    {
        packet = null;
        if (datagram.Length <= HeaderLength || datagram.Length > HeaderLength + OpusMaxPayloadLength ||
            datagram[0] != 0x80 || (datagram[1] & 0x7f) != OpusPayloadType) return false;
        packet = new RtpPacket(BinaryPrimitives.ReadUInt16BigEndian(datagram[2..]),
            BinaryPrimitives.ReadUInt32BigEndian(datagram[4..]),
            BinaryPrimitives.ReadUInt32BigEndian(datagram[8..]), datagram[HeaderLength..].ToArray());
        return true;
    }
}
