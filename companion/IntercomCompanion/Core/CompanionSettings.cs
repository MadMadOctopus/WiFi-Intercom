using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IntercomCompanion.Core;

internal sealed class CompanionSettings
{
    // Matches DEVICE_ALIAS_MAX in the firmware's device_config.h.
    private const int AliasMaxBytes = 32;

    public uint NodeId { get; set; }
    public string MeshId { get; set; } = "MESH";
    public string Alias { get; set; } = SanitizeAlias(Environment.MachineName);
    public string? RecordingDeviceName { get; set; }
    public string? PlaybackDeviceId { get; set; }
    public bool RunInNotificationArea { get; set; } = true;
    public bool StartWithWindows { get; set; }

    public static string AppDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WiFi-Intercom");

    public static string RecordingsDirectory => Path.Combine(AppDataDirectory, "recordings");

    private static string FilePath => Path.Combine(AppDataDirectory, "settings.json");

    public static CompanionSettings Load()
    {
        try
        {
            var saved = JsonSerializer.Deserialize<CompanionSettings>(File.ReadAllText(FilePath));
            if (saved is { NodeId: not 0 })
            {
                saved.Alias = SanitizeAlias(saved.Alias);
                saved.MeshId = Protocol.IsValidMeshId(saved.MeshId) ? saved.MeshId : "MESH";
                return saved;
            }
        }
        catch (IOException) { }
        catch (JsonException) { }

        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        var nodeId = BitConverter.ToUInt32(bytes);
        return new CompanionSettings { NodeId = nodeId == 0 ? 1u : nodeId };
    }

    public void Save()
    {
        Alias = SanitizeAlias(Alias);
        MeshId = Protocol.IsValidMeshId(MeshId) ? MeshId : "MESH";
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(RecordingsDirectory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    // Firmware stores the alias in a 32-byte UTF-8 field (DEVICE_ALIAS_MAX) and
    // truncates with strncpy, so the cap is measured in encoded bytes and whole
    // characters are dropped rather than splitting a code point.
    public static string SanitizeAlias(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return "Companion";
        var trimmed = alias.Trim();
        var length = trimmed.Length;
        while (length > 0 && (char.IsHighSurrogate(trimmed[length - 1]) ||
                              Encoding.UTF8.GetByteCount(trimmed.AsSpan(0, length)) > AliasMaxBytes))
            length--;
        return length == 0 ? "Companion" : trimmed[..length];
    }
}
