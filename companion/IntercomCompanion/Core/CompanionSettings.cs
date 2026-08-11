using System.Security.Cryptography;
using System.Text.Json;

namespace IntercomCompanion.Core;

internal sealed class CompanionSettings
{
    public uint NodeId { get; set; }
    public string Alias { get; set; } = Environment.MachineName[..Math.Min(32, Environment.MachineName.Length)];

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
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(RecordingsDirectory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string SanitizeAlias(string? alias) => string.IsNullOrWhiteSpace(alias)
        ? "Companion"
        : alias.Trim()[..Math.Min(32, alias.Trim().Length)];
}
