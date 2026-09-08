using System.Text.Json;

namespace IntercomCompanion.Core;

/// <summary>Shared remote configuration field mapping, testable without Windows IO.</summary>
internal static class DeviceConfigurationJson
{
    public static string Encode(DeviceConfiguration configuration)
    {
        return JsonSerializer.Serialize(new
        {
            cmd = "set",
            alias = configuration.Alias,
            speaker_volume = configuration.SpeakerVolume,
            led_brightness = configuration.LedBrightness,
            buttons_swapped = configuration.ButtonsSwapped,
            ring_orientation = configuration.RingOrientation,
            soft_mute = configuration.SoftMute,
            mesh_id = configuration.MeshId,
            device_id = configuration.DeviceId,
            assistant_enabled = configuration.AssistantEnabled,
            assistant_service_id = configuration.AssistantServiceId,
        });
    }

    public static DeviceConfiguration Decode(JsonElement root, uint senderId)
    {
        if (!root.TryGetProperty("type", out var type) || type.GetString() != "config")
            throw new JsonException("Expected device configuration.");
        return new DeviceConfiguration(
            root.TryGetProperty("alias", out var alias) ? alias.GetString() ?? "" : "",
            root.TryGetProperty("speaker_volume", out var volume) ? volume.GetInt32() : 512,
            root.TryGetProperty("led_brightness", out var brightness) ? brightness.GetInt32() : 48,
            root.TryGetProperty("buttons_swapped", out var swapped) && swapped.GetBoolean(),
            root.TryGetProperty("ring_orientation", out var orientation) ? orientation.GetInt32() : 0,
            root.TryGetProperty("soft_mute", out var softMute) && softMute.GetBoolean(),
            root.TryGetProperty("hw_muted", out var hardwareMute) && hardwareMute.GetBoolean(),
            ReadMeshId(root),
            root.TryGetProperty("device_id", out var deviceId) ? deviceId.GetUInt32() : senderId,
            root.TryGetProperty("assistant_enabled", out var assistantEnabled) && assistantEnabled.GetBoolean(),
            root.TryGetProperty("assistant_service_id", out var serviceId) ? serviceId.GetUInt32() : 0);
    }

    private static string ReadMeshId(JsonElement root)
    {
        if (!root.TryGetProperty("mesh_id", out var meshId)) return "MESH";
        if (meshId.ValueKind == JsonValueKind.String)
            return Protocol.IsValidMeshId(meshId.GetString()) ? meshId.GetString()! : "MESH";
        if (meshId.ValueKind == JsonValueKind.Number && meshId.TryGetUInt32(out var numeric))
            return Protocol.MeshIdToText(numeric);
        return "MESH";
    }
}
