using System.Net;

namespace IntercomCompanion.Core;

internal sealed record Peer(uint NodeId, IPEndPoint Endpoint, string Alias,
    byte? ProtocolVersion, string FirmwareVersion, byte Capabilities, DateTimeOffset LastSeen)
{
    public bool IsProtocolCompatible => ProtocolVersion is null || ProtocolVersion == Protocol.Version;
    public bool SupportsOta => (Capabilities & Protocol.OtaCapability) != 0;
}

internal sealed record DeviceConfiguration(
    string Alias,
    int SpeakerVolume,
    int LedBrightness,
    bool ButtonsSwapped,
    int RingOrientation);
