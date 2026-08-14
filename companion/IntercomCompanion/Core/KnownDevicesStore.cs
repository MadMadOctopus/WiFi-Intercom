using System.Text.Json;

namespace IntercomCompanion.Core;

/// <summary>
/// Local-only memory of devices. Discovery remains authoritative for active
/// state; this store deliberately contains no credential or peer-routing data.
/// </summary>
internal sealed class KnownDevicesStore
{
    private readonly Dictionary<uint, KnownDevice> devices = new();
    private static string FilePath => Path.Combine(CompanionSettings.AppDataDirectory, "devices.json");

    public IReadOnlyCollection<KnownDevice> Devices => devices.Values;

    public static KnownDevicesStore Load()
    {
        var store = new KnownDevicesStore();
        try
        {
            var saved = JsonSerializer.Deserialize<List<KnownDevice>>(File.ReadAllText(FilePath));
            foreach (var device in saved ?? [])
                if (device.NodeId != 0) store.devices[device.NodeId] = device;
        }
        catch (IOException) { }
        catch (JsonException) { }
        return store;
    }

    public void Remember(Peer peer, DeviceConfiguration? configuration = null)
    {
        devices[peer.NodeId] = new KnownDevice(peer.NodeId, peer.Alias, peer.Endpoint.Address.ToString(),
            peer.LastSeen, configuration?.SpeakerVolume, configuration?.LedBrightness,
            configuration?.SoftMute ?? peer.SoftMuted);
        Save();
    }

    public bool Remove(uint nodeId)
    {
        var removed = devices.Remove(nodeId);
        if (removed) Save();
        return removed;
    }

    public int RemoveAllOffline(ISet<uint> activeIds)
    {
        var removed = devices.Keys.Where(nodeId => !activeIds.Contains(nodeId)).ToArray();
        foreach (var nodeId in removed) devices.Remove(nodeId);
        if (removed.Length != 0) Save();
        return removed.Length;
    }

    private void Save()
    {
        Directory.CreateDirectory(CompanionSettings.AppDataDirectory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(devices.Values.OrderBy(device => device.Alias),
            new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal sealed record KnownDevice(uint NodeId, string Alias, string LastAddress,
    DateTimeOffset LastSeen, int? SpeakerVolume, int? LedBrightness, bool SoftMuted);
