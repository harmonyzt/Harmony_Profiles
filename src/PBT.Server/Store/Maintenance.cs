using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;

namespace PBT.Server;

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

    // Chance grows with level towards the rarity's share; skill and a per-achievement difficulty scale it.
    // The roll per account and achievement is fixed, so levelling up only ever adds achievements.
    private double StoryChance(Career c, string id, string rarity)
    {
        var (share, scale) = rarity switch
        {
            "Common" => (policy.Achievements.Common, 12d),
            "Rare" => (policy.Achievements.Rare, 30d),
            "Legendary" => (policy.Achievements.Legendary, 50d),
            _ => (0d, 1d)
        };
        double reach = (1 - Math.Exp(-Level(c.Experience) / scale)) / (1 - Math.Exp(-79 / scale));
        return Math.Min(.98, share * reach * (.4 + 1.2 * Skill(c)) * (.5 + Stable(id + ":difficulty")));
    }

    private void AddStories(Career c, Dictionary<string, long> result, long now)
    {
        foreach (var (id, story) in achievementStories)
        {
            if (milestones.Any(m => m.Id == id) || result.ContainsKey(id)
                || story.Side is not ("All" or "Pmc") && story.Side != c.Side
                || Stable(c.Id + ":badge:" + id) >= StoryChance(c, id, story.Rarity)) continue;
            // Fictional account history only. Never invoke native reward, quest, or achievement-grant controllers.
            result.Add(id, now);
        }
    }

    // Some setups give the mannequin head a spawn weight. On a profile it looks broken.
    private bool IsMannequin(string? id) => IsId(id) && templates.Customization.TryGetValue(new MongoId(id!), out var template)
        && template.Name?.Contains("Mannequin", StringComparison.OrdinalIgnoreCase) == true;

    private Dictionary<MongoId, double> PlayableHeads(Dictionary<MongoId, double> heads)
    {
        var playable = heads.Where(h => !IsMannequin(h.Key.ToString())).ToDictionary();
        return playable.Any(h => double.IsFinite(h.Value) && h.Value > 0) ? playable : heads;
    }

    private void RepairAppearance(Career c)
    {
        var native = NativeCharacter(c.Side, c.Edition);
        if (IsMannequin(c.Customization["Head"]?.ToString())
            && bots.Types.TryGetValue(c.Side.ToLowerInvariant(), out var type) && type is not null)
        {
            var head = Pick(PlayableHeads(type.BotAppearance.Head));
            if (!IsMannequin(head.ToString()))
            {
                c.Customization["Head"] = head.ToString();
                dirty = true;
            }
        }
        foreach (string part in new[] { "Head", "Body", "Hands", "Feet", "Voice" })
        {
            string? asset = c.Customization[part]?.ToString();
            if (IsId(asset) && templates.Customization.ContainsKey(new MongoId(asset!)) && !(part == "Head" && IsMannequin(asset))) continue;
            if (native["Customization"]?[part] is { } fallback) c.Customization[part] = fallback.DeepClone();
        }
    }

    private void RepairCareerAssets(Career c)
    {
        var assets = Assets(c);
        var equipment = RepairEquipment(assets.Equipment);
        if (equipment.ToJsonString() != assets.Equipment.ToJsonString()) SetEquipment(c, equipment);
        if (assets.Stash?["stash"] is not JsonValue root || assets.Stash["items"] is not JsonArray items) return;
        var shape = new JsonObject { ["Id"] = root.DeepClone(), ["Items"] = items.DeepClone() };
        var repaired = RepairEquipment(shape);
        if (repaired["Items"]!.ToJsonString() == items.ToJsonString()) return;
        // A broken carried stash falls back to a freshly rolled one.
        if (repaired["Id"]?.ToString() != root.ToString()) assets.Stash = null;
        else assets.Stash["items"] = repaired["Items"]!.DeepClone();
        assets.Changed();
    }
}
