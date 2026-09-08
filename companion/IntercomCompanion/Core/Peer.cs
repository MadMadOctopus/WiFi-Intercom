using System.Net;

namespace IntercomCompanion.Core;

internal sealed record Peer(uint NodeId, IPEndPoint Endpoint, string Alias,
    byte? ProtocolVersion, string FirmwareVersion, byte Capabilities, byte HelloFlags, DateTimeOffset LastSeen,
    string GroupCode = "MESH")
{
    public bool IsProtocolCompatible => ProtocolVersion == Protocol.Version;
    public bool IsLegacy => ProtocolVersion is null or 1;
    // OTA requires an exact protocol version match; SupportsOta alone admits incompatible peers.
    public bool IsOtaEligible => ProtocolVersion == Protocol.Version && SupportsOta;
    public bool SupportsAssistantService => (Capabilities & Protocol.AssistantServiceCapability) != 0;
    public bool SupportsAssistantClient => (Capabilities & Protocol.AssistantClientCapability) != 0;
    public bool SupportsOta => (Capabilities & Protocol.OtaCapability) != 0;
    public bool SupportsMuteReporting => ProtocolVersion >= 2;
    public bool HardwareMuted => (HelloFlags & Protocol.HelloFlagHardwareMuted) != 0;
    public bool SoftMuted => (HelloFlags & Protocol.HelloFlagSoftMuted) != 0;
    public bool IsTalking => (HelloFlags & Protocol.HelloFlagTalking) != 0;
}

internal sealed record DeviceConfiguration(
    string Alias,
    int SpeakerVolume,
    int LedBrightness,
    bool ButtonsSwapped,
    int RingOrientation,
    bool SoftMute,
    bool HardwareMuted,
    string MeshId,
    uint DeviceId,
    bool AssistantEnabled = false,
    uint AssistantServiceId = 0);
