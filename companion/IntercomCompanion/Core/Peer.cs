using System.Net;

namespace IntercomCompanion.Core;

internal sealed record Peer(uint NodeId, IPEndPoint Endpoint, string Alias, DateTimeOffset LastSeen);

internal sealed record DeviceConfiguration(
    string Alias,
    int SpeakerVolume,
    int LedBrightness,
    bool ButtonsSwapped,
    int RingOrientation);
