using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;

namespace HarmonyProfiles.Server;

public sealed partial class PopulationStore
{
    private readonly Dictionary<string, (string Side, string Rarity)> achievementStories = new();

    public Dictionary<string, int> AchievementPercentages()
    {
        lock (gate)
        {
            var careers = population.Careers.Values.Where(c => !c.Retired).ToArray();
            if (!Ready || careers.Length == 0) return [];
            return installedAchievements.ToDictionary(id => id,
                id => (int)Math.Round(careers.Count(c => c.Achievements.ContainsKey(id)) * 100d / careers.Length));
        }
    }

    private double BadgeHours(string id, string rarity) => rarity switch
    {
        "Common" => 15 + Stable(id + ":hours") * 85,
        "Rare" => 150 + Stable(id + ":hours") * 600,
        "Legendary" => 1000 + Stable(id + ":hours") * 2000,
        _ => double.PositiveInfinity
    };

    private void AddCommonStories(Career c, Dictionary<string, long> result, long now)
    {
        double hours = (c.RaidSeconds + c.Observed.ActiveSeconds) / 3600;
        foreach (var (id, story) in achievementStories)
        {
            if (story.Rarity != "Common" || milestones.Any(m => m.Id == id) || result.ContainsKey(id)
                || story.Side is not ("All" or "Pmc") && story.Side != c.Side
                || hours < BadgeHours(id, story.Rarity) || Stable(c.Id + ":badge:" + id) < .1) continue;
            // Fictional account history only. Never invoke native reward, quest, or achievement-grant controllers.
            result.Add(id, now);
        }
    }

    private void RepairCareerAssets(Career c)
    {
        c.Equipment = RepairEquipment(c.Equipment);
        var native = NativeCharacter(c.Side, c.Edition);
        foreach (string part in new[] { "Head", "Body", "Hands", "Feet", "Voice" })
        {
            string? asset = c.Customization[part]?.ToString();
            if (IsId(asset) && templates.Customization.ContainsKey(new MongoId(asset!))) continue;
            if (native["Customization"]?[part] is { } fallback) c.Customization[part] = fallback.DeepClone();
        }
        if (c.Stash["stash"] is not JsonValue root || c.Stash["items"] is not JsonArray items) return;
        var shape = new JsonObject { ["Id"] = root.DeepClone(), ["Items"] = items.DeepClone() };
        var repaired = RepairEquipment(shape);
        if (repaired["Id"]?.ToString() != root.ToString())
            c.Stash = GenerateStash(c, native["Inventory"]!.AsObject());
        else c.Stash["items"] = repaired["Items"]!.DeepClone();
    }
}
