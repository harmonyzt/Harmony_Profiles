using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;

namespace PBT.Server;

public sealed partial class PopulationStore
{
    private readonly Dictionary<string, (string Side, string Rarity)> achievementStories = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> wearable = new();

    public Dictionary<string, int> AchievementPercentages()
    {
        lock (gate)
        {
            var careers = population.Careers.Values.Where(c => !c.Retired).ToArray();
            if (!Ready || careers.Length == 0)
                return [];
            return installedAchievements.ToDictionary(
                id => id,
                id => (int)Math.Round(careers.Count(c => c.Achievements.ContainsKey(id)) * 100d / careers.Length)
            );
        }
    }

    private double BadgeHours(string id, string rarity) =>
        rarity switch
        {
            "Common" => 15 + Stable(id + ":hours") * 85,
            "Rare" => 150 + Stable(id + ":hours") * 600,
            "Legendary" => 1000 + Stable(id + ":hours") * 2000,
            _ => double.PositiveInfinity,
        };

    // Live PvP share of all players per achievement, keyed by achievement id once matched to English names.
    private readonly Dictionary<string, double> liveAchievementRates = new();
    private readonly Dictionary<double, double> achievementWeightMeans = new();
    private long achievementWeightsUtc;

    private void LoadAchievementRates()
    {
        using var stream = System
            .Reflection.Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("PBT.Server.Calibration.achievement-rates.json");
        if (stream is null)
            return;
        var rates = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(stream) ?? [];
        var byName = rates.ToDictionary(p => RateKey(p.Key), p => p.Value / 100);
        var names = locales.GetLocaleDb("en");
        foreach (var id in achievementStories.Keys)
            if (names.TryGetValue(id + " name", out var name) && byName.TryGetValue(RateKey(name), out double rate))
                liveAchievementRates[id] = Math.Clamp(rate, 0, 1);
        logger.Debug(
            $"[PBT] Live rates matched for {liveAchievementRates.Count} of {achievementStories.Count} story achievements."
        );
    }

    private static string RateKey(string name) => new(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    // Rarer achievements lean harder on level, so only veterans own them.
    private static double RateScale(double rate) =>
        rate >= .3 ? 8
        : rate >= .05 ? 20
        : rate >= .01 ? 35
        : 50;

    private double AchievementWeight(Career c, double scale) =>
        (1 - Math.Exp(-Level(c.Experience) / scale)) / (1 - Math.Exp(-79 / scale)) * (.4 + 1.2 * Skill(c));

    private double AchievementWeightMean(double scale)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now - achievementWeightsUtc > 3600)
        {
            achievementWeightMeans.Clear();
            achievementWeightsUtc = now;
        }

        if (achievementWeightMeans.TryGetValue(scale, out double mean))
            return mean;
        var careers = population.Careers.Values.Where(c => !c.Retired).ToArray();
        mean = careers.Length == 0 ? 1 : Math.Max(.01, careers.Average(c => AchievementWeight(c, scale)));
        achievementWeightMeans[scale] = mean;
        return mean;
    }

    // The roll per account and achievement is fixed, so levelling up only ever adds achievements.
    private double StoryChance(Career c, string id, string rarity)
    {
        // Matches the live share across the whole population; each account's odds follow its level and skill.
        if (liveAchievementRates.TryGetValue(id, out double live))
        {
            if (live >= 1)
                return 1;
            double liveScale = RateScale(live);
            return 1 - Math.Pow(1 - live, AchievementWeight(c, liveScale) / AchievementWeightMean(liveScale));
        }

        // Custom achievements from other mods may use their own rarity names; those count as Rare.
        var (share, scale) = (rarity ?? "").ToLowerInvariant() switch
        {
            "common" => (policy.Achievements.Common, 12d),
            "legendary" => (policy.Achievements.Legendary, 50d),
            _ => (policy.Achievements.Rare, 30d),
        };
        double reach = (1 - Math.Exp(-Level(c.Experience) / scale)) / (1 - Math.Exp(-79 / scale));
        return Math.Min(.98, share * reach * (.4 + 1.2 * Skill(c)) * (.5 + Stable(id + ":difficulty")));
    }

    private void AddStories(Career c, Dictionary<string, long> result, long now)
    {
        foreach (var (id, story) in achievementStories)
        {
            if (
                milestones.Any(m => m.Id == id)
                || result.ContainsKey(id)
                || story.Side is not ("All" or "Pmc") && story.Side != c.Side
                || Stable(c.Id + ":badge:" + id) >= StoryChance(c, id, story.Rarity)
            )
                continue;
            result.Add(id, now);
        }
    }

    // Some setups give the mannequin head a spawn weight. On a profile it looks broken.
    private bool IsMannequin(string? id) =>
        IsId(id)
        && templates.Customization.TryGetValue(new MongoId(id!), out var template)
        && template.Name?.Contains("Mannequin", StringComparison.OrdinalIgnoreCase) == true;

    // The client resolves Head/Body/Feet to a bundle while spawning; an id it can't resolve crashes bot loading.
    private bool Wearable(string part, string? id)
    {
        if (!IsId(id))
            return false;
        if (wearable.TryGetValue(id!, out bool known))
            return known;
        bool ok =
            templates.Customization.TryGetValue(new MongoId(id!), out var template)
            && template.Type == "Item"
            && (part is "Voice" || template.Properties?.BodyPart == part && PrefabPath(template.Properties.Prefab));
        wearable[id!] = ok;
        return ok;
    }

    private bool PrefabPath(object? prefab) =>
        prefab switch
        {
            null => false,
            string path => path.Length > 0,
            _ => JsonNode.Parse(json.Serialize(prefab) ?? "null") is JsonObject node
                && !string.IsNullOrEmpty(node["path"]?.ToString()),
        };

    private Dictionary<MongoId, double> PlayableHeads(Dictionary<MongoId, double> heads)
    {
        var playable = heads.Where(h => !IsMannequin(h.Key.ToString())).ToDictionary();
        return playable.Any(h => double.IsFinite(h.Value) && h.Value > 0) ? playable : heads;
    }

    private void RepairAppearance(Career c)
    {
        var native = NativeCharacter(c.Side, c.Edition);
        if (
            IsMannequin(c.Customization["Head"]?.ToString())
            && bots.Types.TryGetValue(c.Side.ToLowerInvariant(), out var type)
            && type is not null
        )
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
            if (Wearable(part, asset) && !(part == "Head" && IsMannequin(asset)))
                continue;
            if (native["Customization"]?[part] is { } fallback)
                c.Customization[part] = fallback.DeepClone();
        }
    }

    private void RepairCareerAssets(Career c)
    {
        var assets = Assets(c);
        var equipment = RepairEquipment(assets.Equipment);
        if (equipment.ToJsonString() != assets.Equipment.ToJsonString())
            SetEquipment(c, equipment);
        if (assets.Stash?["stash"] is not JsonValue root || assets.Stash["items"] is not JsonArray items)
            return;
        var shape = new JsonObject { ["Id"] = root.DeepClone(), ["Items"] = items.DeepClone() };
        var repaired = RepairEquipment(shape);
        if (repaired["Items"]!.ToJsonString() == items.ToJsonString())
            return;
        if (repaired["Id"]?.ToString() != root.ToString())
            assets.Stash = null;
        else
            assets.Stash["items"] = repaired["Items"]!.DeepClone();
        assets.Changed();
    }
}
