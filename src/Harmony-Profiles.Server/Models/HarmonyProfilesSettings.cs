using System.Text.Json;

namespace HarmonyProfiles.Server;

public sealed class HarmonyProfilesSettings
{
    public bool PersistentFaceAndVoice { get; set; } = true;
    public bool PersistentClothing { get; set; }
    public bool EnableFriendRequests { get; set; } = true;
    public double FriendAcceptanceChance { get; set; } = 0.5;
    public double FriendActivityMultiplier { get; set; } = 1.4;
    public int ProgressionThresholdSeconds { get; set; } = 3600;
    public int MaximumOfflineDays { get; set; } = 14;

    internal static HarmonyProfilesSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new HarmonyProfilesSettings();
            AtomicFile.Write(path, JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true }));
            return defaults;
        }
        var settings = JsonSerializer.Deserialize<HarmonyProfilesSettings>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Harmony-Profiles config is empty.");
        settings.Validate();
        using var stored = JsonDocument.Parse(File.ReadAllText(path));
        if (stored.RootElement.TryGetProperty("UsePersistentNativeGeneration", out _))
            AtomicFile.Write(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        return settings;
    }
    public void Validate()
    {
        if (!double.IsFinite(FriendAcceptanceChance) || FriendAcceptanceChance is < 0 or > 1
            || !double.IsFinite(FriendActivityMultiplier) || FriendActivityMultiplier is < 0 or > 5
            || ProgressionThresholdSeconds is < 60 or > 86400
            || MaximumOfflineDays is < 1 or > 365)
            throw new InvalidDataException("Harmony-Profiles config values are outside the supported ranges.");
    }
}
