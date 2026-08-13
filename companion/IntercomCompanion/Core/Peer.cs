using System.Net;

namespace IntercomCompanion.Core;

internal sealed record Peer(uint NodeId, IPEndPoint Endpoint, string Alias,
    byte? ProtocolVersion, string FirmwareVersion, DateTimeOffset LastSeen)
{
    public bool IsProtocolCompatible => ProtocolVersion is null || ProtocolVersion == Protocol.Version;
}

internal sealed record DeviceConfiguration(
    string Alias,
    int SpeakerVolume,
    int LedBrightness,
    bool ButtonsSwapped,
    int RingOrientation);
