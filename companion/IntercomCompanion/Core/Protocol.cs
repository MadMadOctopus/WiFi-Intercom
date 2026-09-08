using System.Buffers.Binary;
using System.Text;

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
    OtaOffer = 10,
    OtaStatus = 11,
    OtaCancel = 12,
    Accept = 13,
}

internal sealed record IntercomPacket(
    PacketType Type,
    byte Flags,
    uint SenderId,
    uint SessionId,
    uint Sequence,
    uint TimestampMs,
    byte[] Payload,
    uint MeshId);

/// <summary>Exact big-endian PTT1 datagram codec shared with firmware.</summary>
internal static class Protocol
{
    public const byte Version = 3;
    public const uint DefaultMeshId = 0x4D455348; // "MESH"
    public const int Port = 45678;
    public const int HeaderLength = 32;
    public const int AdpcmPayloadLength = 164;
    public const byte DirectedFlag = 0x01;
    public const byte OtaCapability = 0x01;
    public const byte AssistantServiceCapability = 0x02;
    public const byte AssistantClientCapability = 0x04;
    public const byte HelloFlagHardwareMuted = 0x01;
    public const byte HelloFlagSoftMuted = 0x02;
    public const byte HelloFlagTalking = 0x04;

    private static ReadOnlySpan<byte> Magic => "PTT1"u8;
    private static ReadOnlySpan<byte> HelloMagic => "IH1"u8;
    private static ReadOnlySpan<byte> HelloMagic2 => "IH2"u8;
    private static ReadOnlySpan<byte> HelloMagic3 => "IH3"u8;

    /// <summary>Discovery is self-describing while retaining a compact payload.
    /// IH3 | protocol revision | capabilities | flags | firmware byte count | firmware | alias.</summary>
    public static byte[] BuildHelloPayload(string alias, string firmwareVersion, byte flags = 0, byte capabilities = 0)
    {
        var firmware = Encoding.UTF8.GetBytes(firmwareVersion[..Math.Min(31, firmwareVersion.Length)]);
        var aliasBytes = Encoding.UTF8.GetBytes(CompanionSettings.SanitizeAlias(alias));
        var payload = new byte[7 + firmware.Length + aliasBytes.Length];
        HelloMagic3.CopyTo(payload);
        payload[3] = Version;
        payload[4] = capabilities; // production companion advertises no service/client roles
        payload[5] = (byte)(flags & (HelloFlagHardwareMuted | HelloFlagSoftMuted | HelloFlagTalking));
        payload[6] = checked((byte)firmware.Length);
        firmware.CopyTo(payload, 7);
        aliasBytes.CopyTo(payload, 7 + firmware.Length);
        return payload;
    }

    public static HelloAnnouncement ParseHello(ReadOnlySpan<byte> payload)
    {
        if (payload.Length >= 7 && payload[..3].SequenceEqual(HelloMagic3))
        {
            var firmwareLength = payload[6];
            if (payload.Length >= 7 + firmwareLength)
            {
                var firmware = Encoding.UTF8.GetString(payload.Slice(7, firmwareLength));
                var alias = Encoding.UTF8.GetString(payload[(7 + firmwareLength)..]);
                return new HelloAnnouncement(CompanionSettings.SanitizeAlias(alias), payload[3], firmware, payload[4], payload[5]);
            }
        }
        if (payload.Length >= 6 && payload[..3].SequenceEqual(HelloMagic2))
        {
            var firmwareLength = payload[5];
            if (payload.Length >= 6 + firmwareLength)
            {
                var firmware = Encoding.UTF8.GetString(payload.Slice(6, firmwareLength));
                var alias = Encoding.UTF8.GetString(payload[(6 + firmwareLength)..]);
                return new HelloAnnouncement(CompanionSettings.SanitizeAlias(alias), payload[3], firmware, payload[4], 0);
            }
        }
        if (payload.Length >= 5 && payload[..3].SequenceEqual(HelloMagic))
        {
            var firmwareLength = payload[4];
            if (payload.Length >= 5 + firmwareLength)
            {
                var firmware = Encoding.UTF8.GetString(payload.Slice(5, firmwareLength));
                var alias = Encoding.UTF8.GetString(payload[(5 + firmwareLength)..]);
                return new HelloAnnouncement(CompanionSettings.SanitizeAlias(alias), payload[3], firmware, 0, 0);
            }
        }
        // A pre-0.6.2 node advertised only its UTF-8 alias.
        return new HelloAnnouncement(CompanionSettings.SanitizeAlias(Encoding.UTF8.GetString(payload)), null, "legacy", 0, 0);
    }

    public static byte[] Pack(uint meshId, uint senderId, PacketType type, uint sessionId,
                              uint sequence, uint timestampMs,
                              ReadOnlySpan<byte> payload, byte flags = 0)
    {
        var datagram = new byte[HeaderLength + payload.Length];
        Magic.CopyTo(datagram);
        datagram[4] = (byte)type;
        datagram[5] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(6), HeaderLength);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(8), meshId);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(12), senderId);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(16), sessionId);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(20), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(24), timestampMs);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(28), checked((ushort)payload.Length));
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(30), Version);
        payload.CopyTo(datagram.AsSpan(HeaderLength));
        return datagram;
    }

    /// <summary>Parses any well-formed datagram regardless of group; the caller
    /// decides membership from <see cref="IntercomPacket.MeshId"/>. Multi-group
    /// discovery depends on not filtering by group here.</summary>
    public static bool TryParse(ReadOnlySpan<byte> datagram, uint ownNodeId, out IntercomPacket? packet)
    {
        packet = null;
        if (datagram.Length < HeaderLength || !datagram[..4].SequenceEqual(Magic) ||
            BinaryPrimitives.ReadUInt16BigEndian(datagram[6..]) != HeaderLength)
            return false;

        if (datagram[4] != (byte)PacketType.Hello &&
            BinaryPrimitives.ReadUInt16BigEndian(datagram[30..]) != Version) return false;

        var meshId = BinaryPrimitives.ReadUInt32BigEndian(datagram[8..]);
        var sender = BinaryPrimitives.ReadUInt32BigEndian(datagram[12..]);
        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(datagram[28..]);
        if (sender == ownNodeId || datagram.Length != HeaderLength + payloadLength)
            return false;

        packet = new IntercomPacket(
            (PacketType)datagram[4], datagram[5], sender,
            BinaryPrimitives.ReadUInt32BigEndian(datagram[16..]),
            BinaryPrimitives.ReadUInt32BigEndian(datagram[20..]),
            BinaryPrimitives.ReadUInt32BigEndian(datagram[24..]),
            datagram[HeaderLength..].ToArray(),
            meshId);
        return true;
    }

    public static bool IsValidMeshId(string? meshId) => meshId is { Length: 4 } &&
        meshId.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9');

    public static uint MeshIdFromText(string meshId)
    {
        if (!IsValidMeshId(meshId)) throw new ArgumentException("Group ID must be exactly four A–Z or 0–9 characters.", nameof(meshId));
        return BinaryPrimitives.ReadUInt32BigEndian(Encoding.ASCII.GetBytes(meshId));
    }

    public static string MeshIdToText(uint meshId) => Encoding.ASCII.GetString([
        (byte)(meshId >> 24), (byte)(meshId >> 16), (byte)(meshId >> 8), (byte)meshId]);
}

internal sealed record HelloAnnouncement(string Alias, byte? ProtocolVersion, string FirmwareVersion,
    byte Capabilities, byte Flags);
