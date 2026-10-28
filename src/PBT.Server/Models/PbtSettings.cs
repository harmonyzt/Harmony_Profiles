using System.Text.Json;

namespace PBT.Server;

public sealed class PbtSettings
{
    public bool PersistentFaceAndVoice { get; set; } = true;
    public bool PersistentClothing { get; set; }
    public bool EnableFriendRequests { get; set; } = true;
    public double FriendAcceptanceChance { get; set; } = 0.5;
    public double FriendActivityMultiplier { get; set; } = 1.4;
    public double OnlineRateMultiplier { get; set; } = 1;
    public int ProgressionThresholdSeconds { get; set; } = 3600;
    public int MaximumOfflineDays { get; set; } = 14;

    internal static PbtSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new PbtSettings();
            AtomicFile.Write(path, JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true }));
            return defaults;
        }
        var settings = JsonSerializer.Deserialize<PbtSettings>(File.ReadAllText(path))
            ?? throw new InvalidDataException("PBT config is empty.");
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
            || !double.IsFinite(OnlineRateMultiplier) || OnlineRateMultiplier is < 0 or > 10
            || ProgressionThresholdSeconds is < 60 or > 86400
            || MaximumOfflineDays is < 1 or > 365)
            throw new InvalidDataException("PBT config values are outside the supported ranges.");
    }
}
