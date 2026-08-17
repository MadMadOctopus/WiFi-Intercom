using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntercomCompanion.Core;

/// <summary>A group this companion has joined. The code is the 4-character wire
/// identity every node shares; the name is a label stored only on this PC.</summary>
internal sealed record GroupMembership(string Code, string? Name)
{
    [JsonIgnore]
    public string Label => string.IsNullOrWhiteSpace(Name) ? Code : $"{Name} ({Code})";
}

internal sealed class CompanionSettings
{
    public uint NodeId { get; set; }

    /// <summary>Groups this companion has joined, in display order. A companion
    /// always belongs to at least one group.</summary>
    public List<GroupMembership> JoinedGroups { get; set; } = [];

    /// <summary>The group <c>Hold to broadcast</c> sends to: empty means every
    /// joined group (the default), otherwise one of <see cref="JoinedGroups"/>.</summary>
    public string BroadcastTargetCode { get; set; } = "";

    [JsonIgnore]
    public bool BroadcastsToAll => string.IsNullOrEmpty(BroadcastTargetCode);

    /// <summary>Display label for the current broadcast target.</summary>
    [JsonIgnore]
    public string BroadcastTargetLabel => BroadcastsToAll ? "all joined groups" : GroupLabel(BroadcastTargetCode);

    /// <summary>Legacy single-group field, read once to migrate an old
    /// settings.json into <see cref="JoinedGroups"/>; not written afterwards.</summary>
    [JsonPropertyName("MeshId")]
    public string? LegacyMeshId { get; set; }

    /// <summary>Back-compat alias: the broadcast target group code. Existing call
    /// sites treat "the current group" as the broadcast target.</summary>
    /// <summary>Back-compat: always a valid single group code (never empty) for
    /// legacy packing fallbacks. Reads the broadcast target, or the first joined
    /// group when broadcasting to all.</summary>
    [JsonIgnore]
    public string MeshId
    {
        get => BroadcastsToAll ? (JoinedGroups.Count > 0 ? JoinedGroups[0].Code : "MESH") : BroadcastTargetCode;
        set => BroadcastTargetCode = value;
    }

    public string Alias { get; set; } = Environment.MachineName[..Math.Min(32, Environment.MachineName.Length)];
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
                saved.NormalizeGroups();
                return saved;
            }
        }
        catch (IOException) { }
        catch (JsonException) { }

        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        var nodeId = BitConverter.ToUInt32(bytes);
        var fresh = new CompanionSettings { NodeId = nodeId == 0 ? 1u : nodeId };
        fresh.NormalizeGroups();
        return fresh;
    }

    public void Save()
    {
        Alias = SanitizeAlias(Alias);
        NormalizeGroups();
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(RecordingsDirectory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ---- Group membership --------------------------------------------------

    public bool IsJoined(string code) => JoinedGroups.Any(group => group.Code == code);

    public string? GroupName(string code) => JoinedGroups.FirstOrDefault(group => group.Code == code)?.Name;

    /// <summary>A device's group as "Name (CODE)" when this PC has a name for it,
    /// otherwise the bare code.</summary>
    public string GroupLabel(string code)
    {
        var name = GroupName(code);
        return string.IsNullOrWhiteSpace(name) ? code : $"{name} ({code})";
    }

    public bool JoinGroup(string code, string? name = null)
    {
        if (!Protocol.IsValidMeshId(code) || IsJoined(code)) return false;
        JoinedGroups.Add(new GroupMembership(code, string.IsNullOrWhiteSpace(name) ? null : name.Trim()));
        return true;
    }

    public bool LeaveGroup(string code)
    {
        if (JoinedGroups.Count <= 1 || !IsJoined(code)) return false;
        JoinedGroups.RemoveAll(group => group.Code == code);
        if (BroadcastTargetCode == code) BroadcastTargetCode = "";  // back to all groups
        return true;
    }

    public void RenameGroup(string code, string? name)
    {
        var index = JoinedGroups.FindIndex(group => group.Code == code);
        if (index >= 0) JoinedGroups[index] = JoinedGroups[index] with { Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim() };
    }

    public bool SetBroadcastTarget(string code)
    {
        // Empty selects every joined group; otherwise it must be one we are in.
        if (!string.IsNullOrEmpty(code) && !IsJoined(code)) return false;
        BroadcastTargetCode = code ?? "";
        return true;
    }

    private void NormalizeGroups()
    {
        JoinedGroups ??= [];
        JoinedGroups.RemoveAll(group => group is null || !Protocol.IsValidMeshId(group.Code));
        // De-duplicate by code, keeping the first name.
        JoinedGroups = JoinedGroups
            .GroupBy(group => group.Code)
            .Select(g => g.First())
            .ToList();
        if (JoinedGroups.Count == 0)
        {
            var seed = Protocol.IsValidMeshId(LegacyMeshId) ? LegacyMeshId! : "MESH";
            JoinedGroups.Add(new GroupMembership(seed, null));
        }
        LegacyMeshId = null;
        // A non-empty target must be a group we are in; otherwise fall back to
        // broadcasting to every joined group.
        if (!string.IsNullOrEmpty(BroadcastTargetCode) && !IsJoined(BroadcastTargetCode)) BroadcastTargetCode = "";
        // Leaving the targeted group returns to all groups rather than silently
        // picking one.
        if (BroadcastTargetCode == null) BroadcastTargetCode = "";
    }

    public static string SanitizeAlias(string? alias) => string.IsNullOrWhiteSpace(alias)
        ? "Companion"
        : alias.Trim()[..Math.Min(32, alias.Trim().Length)];
}
