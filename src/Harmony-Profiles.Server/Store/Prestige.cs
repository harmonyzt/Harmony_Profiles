using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;

namespace HarmonyProfiles.Server;

public sealed partial class PopulationStore
{
    private SimulationPolicy policy = new();
    private JsonArray prestigeDefinitions = new();
    private static double TotalSeconds(Career c) => c.RaidSeconds + c.Observed.ActiveSeconds;
    private static double CycleHours(Career c) => Math.Max(0, TotalSeconds(c) - c.PrestigeCycleStartSeconds) / 3600;

    private void LoadFeatures()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(directory)!, "simulation.json");
        bool write = !File.Exists(path);
        if (!write) policy = JsonSerializer.Deserialize<SimulationPolicy>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Simulation policy is empty.");
        // Files from before the stats rework have single raid lengths and inflated kill rates.
        if (policy.ProgressionPriors?.Any(p => p is not null && p.SurvivedMinutes == 0 && p.DeathMinutes == 0) == true)
        {
            policy.ProgressionPriors = new SimulationPolicy().ProgressionPriors;
            write = true;
            logger.Info("[Harmony-Profiles] Replaced pre-rework raid history priors in simulation.json with the new defaults.");
        }
        policy.Validate();
        if (write) AtomicFile.Write(path, JsonSerializer.Serialize(policy, new JsonSerializerOptions { WriteIndented = true }));
        prestigeDefinitions = JsonNode.Parse(json.Serialize(templates.Prestige)!)?["elements"]?.DeepClone().AsArray() ?? new();
        foreach (var c in population.Careers.Values)
        {
            if (c.FeatureVersion != 0) continue;
            c.PrestigeLevel = Math.Max(c.PrestigeLevel, c.LastGeneratedPrestige);
            if (c.PrestigeLevel > 0) c.PrestigeCycleStartSeconds = TotalSeconds(c);
            c.FeatureVersion = 1;
        }
    }

    private bool PrestigeConditions(Career c, JsonArray conditions)
    {
        foreach (var condition in conditions.OfType<JsonObject>())
        {
            string[] allowed = ["id", "index", "dynamicLocale", "visibilityConditions", "globalQuestCounterId", "parentId", "value",
                "compareMethod", "target", "status", "availableAfter", "dispersion", "areaType", "conditionType",
                "minDurability", "maxDurability", "dogtagLevel", "onlyFoundInRaid", "isEncoded"];
            if (condition.Any(p => p.Value is not null && !allowed.Contains(p.Key))
                || condition["visibilityConditions"] is JsonArray { Count: > 0 }
                || !string.IsNullOrEmpty(condition["globalQuestCounterId"]?.ToString())
                || !string.IsNullOrEmpty(condition["parentId"]?.ToString())
                || (condition["availableAfter"]?.GetValue<double>() ?? 0) > 0
                || (condition["dispersion"]?.GetValue<double>() ?? 0) > 0) return false;
            double value = condition["value"]?.GetValue<double>() ?? 0;
            if (condition["compareMethod"] is { } compare && compare.ToString() != ">=") return false;
            switch (condition["conditionType"]?.ToString())
            {
                case "Level": if (Level(c.Experience) < value) return false; break;
                case "Skill":
                    if (SkillProgress(c, condition["target"]?.ToString()) / 100 < value) return false;
                    break;
                case "HideoutArea":
                    int area = condition["areaType"]?.GetValue<int>() ?? -1;
                    if (AreaLevel(c, area) < value) return false;
                    break;
                case "Quest":
                    string id = condition["target"]?.ToString() ?? "";
                    if (!IsId(id) || !templates.Quests.ContainsKey(new MongoId(id))
                        || condition["status"] is not JsonArray statuses || !statuses.Any(s => s?.GetValue<int>() == 4)
                        || CycleHours(c) < policy.MinimumPrestigeCycleHours * (1 + c.PrestigeLevel * .25)) return false;
                    // These accounts have fictional quest histories. No player quest controller is invoked.
                    break;
                case "HasItem":
                    if (condition["target"] is not JsonArray { Count: 1 } targets || targets[0]?.ToString() != "5449016a4bdc2d6f028b456f"
                        || c.WealthRubles < value || condition["onlyFoundInRaid"]?.GetValue<bool>() == true
                        || condition["isEncoded"]?.GetValue<bool>() == true || (condition["dogtagLevel"]?.GetValue<double>() ?? 0) > 0
                        || (condition["minDurability"]?.GetValue<double>() ?? 0) > 0
                        || (condition["maxDurability"]?.GetValue<double>() ?? 100) < 100)
                        return false;
                    break;
                default: return false;
            }
        }
        return conditions.Count > 0 && conditions.All(c => c is JsonObject);
    }

    private void TryPrestige(Career c, long now)
    {
        if (!policy.EnablePrestige || c.PrestigeLevel >= prestigeDefinitions.Count || IsObservedActive(c, now) || now < c.StoryHoldUntilUtc
            || reservedCareers.Values.Any(ids => ids.Contains(c.Id)) || CycleHours(c) < policy.MinimumPrestigeCycleHours
            || prestigeDefinitions[c.PrestigeLevel] is not JsonObject definition
            || definition["conditions"] is not JsonArray conditions || !PrestigeConditions(c, conditions)) return;
        var next = JsonSerializer.Deserialize<Career>(JsonSerializer.Serialize(c))!;
        string definitionId = definition["id"]!.ToString();
        var transfer = definition["transferConfigs"]?.AsObject();
        if (transfer?["skillConfig"]?["transferMultiplier"] is not JsonValue skillRate
            || transfer["masteringConfig"]?["transferMultiplier"] is not JsonValue masteryRate
            || skillRate.GetValue<double>() is < 0 or > 1 || masteryRate.GetValue<double>() is < 0 or > 1) return;
        var skills = CareerSkills(c);
        foreach (string group in new[] { "Common", "Mastering" })
            if (skills[group] is JsonArray entries)
                foreach (var entry in entries.OfType<JsonObject>())
                    entry["Progress"] = (entry["Progress"]?.GetValue<double>() ?? 0)
                        * (group == "Common" ? skillRate.GetValue<double>() : masteryRate.GetValue<double>());
        var stash = CareerStash(c);
        var carried = PrestigeCarry(stash, transfer["stashConfig"] as JsonObject);
        int oldXp = next.Experience;
        next.PrestigeLevel++;
        next.LastPrestigeUtc = now;
        next.PrestigeCycleStartSeconds = TotalSeconds(next);
        next.Experience = profileHelper.GetExperience(1) ?? 0;
        next.PendingRaidSeconds = 0;
        next.CurrentSurvivalStreak = 0;
        next.DevelopmentVersion = 0;
        EnsureDevelopment(next);
        next.SkillCarry = skills["Common"]!.AsArray().OfType<JsonObject>()
            .ToDictionary(s => s["Id"]!.ToString(), s => s["Progress"]?.GetValue<double>() ?? 0);
        next.WealthRubles = 100000;
        next.HideoutInvestment = 0;
        next.NeedsRegear = true;
        next.EquipmentIsSimulated = true;
        next.LastEquipmentAccount = 0;
        string stashRoot = stash["stash"]!.ToString();
        var root = stash["items"]!.AsArray().OfType<JsonObject>().First(i => i["_id"]!.ToString() == stashRoot).DeepClone();
        var items = new JsonArray(root);
        foreach (var tree in carried)
        {
            tree[0]!["parentId"] = stashRoot;
            foreach (var item in tree) items.Add(item!.DeepClone());
        }
        stash["items"] = items;
        SyncStashTemplate(next, stash);
        if (IsId(definitionId) && PrestigeHelper.PrestigeAchievements.TryGetValue(new MongoId(definitionId), out var achievement))
            next.Achievements[achievement.ToString()] = now;
        next.PrestigeHistory.Add(new(next.PrestigeLevel, definitionId, now, TotalSeconds(next) / 3600, oldXp, carried.Sum(t => t.Count)));
        // All eligibility/reset work is staged before replacing the live fictional career fields.
        foreach (var property in typeof(Career).GetProperties()) property.SetValue(c, property.GetValue(next));
        SetEquipment(c, EmptyEquipment());
        var assets = Assets(c);
        assets.Stash = stash;
        assets.Changed();
        dirty = true;
        logger.Info($"[Harmony-Profiles] Synthetic prestige completed: account={c.AccountId}, prestige={c.PrestigeLevel}.");
    }

    private List<JsonArray> PrestigeCarry(JsonObject stash, JsonObject? config)
    {
        var result = new List<JsonArray>();
        if (config is null || stash["items"] is not JsonArray source) return result;
        int columns = Math.Clamp(config["xCellCount"]?.GetValue<int>() ?? 0, 0, 20);
        int rows = Math.Clamp(config["yCellCount"]?.GetValue<int>() ?? 0, 0, 30);
        var included = config["filters"]?["includedItems"]?.AsArray().Select(v => v!.ToString()).ToHashSet() ?? [];
        var excluded = config["filters"]?["excludedItems"]?.AsArray().Select(v => v!.ToString()).ToHashSet() ?? [];
        int x = 0, y = 0, rowHeight = 0;
        foreach (var item in source.OfType<JsonObject>().Where(i => i["parentId"]?.ToString() == stash["stash"]?.ToString()))
        {
            string tpl = item["_tpl"]!.ToString();
            if (tpl is "5449016a4bdc2d6f028b456f" or "5696686a4bdc2da3298b456a" or "569668774bdc2da2298b4568") continue;
            if (excluded.Any(f => ItemBasedOn(tpl, f)) || !included.Any(f => ItemBasedOn(tpl, f))) continue;
            var props = JsonNode.Parse(json.Serialize(templates.Items[new MongoId(tpl)])!)?["_props"];
            int width = Math.Max(1, props?["Width"]?.GetValue<int>() ?? 1), height = Math.Max(1, props?["Height"]?.GetValue<int>() ?? 1);
            if (width > columns || height > rows) continue;
            if (x + width > columns) { x = 0; y += rowHeight; rowHeight = 0; }
            if (y + height > rows) continue;
            var tree = ItemTree(source, item["_id"]!.ToString());
            tree[0]!["location"] = new JsonObject { ["x"] = x, ["y"] = y, ["r"] = 0, ["isSearched"] = true };
            x += width; rowHeight = Math.Max(rowHeight, height);
            result.Add(tree);
        }
        return result;
    }

    private bool ItemBasedOn(string tpl, string ancestor)
    {
        var seen = new HashSet<string>();
        while (IsId(tpl) && seen.Add(tpl) && templates.Items.TryGetValue(new MongoId(tpl), out var item))
        {
            if (tpl == ancestor) return true;
            tpl = JsonNode.Parse(json.Serialize(item)!)?["_parent"]?.ToString() ?? "";
        }
        return false;
    }

    private static int AreaLevel(Career c, int area) => c.AreaLevels.GetValueOrDefault(area);

    private static JsonArray ItemTree(JsonArray items, string root)
    {
        var keep = new HashSet<string> { root };
        bool changed;
        do
        {
            changed = false;
            foreach (var item in items.OfType<JsonObject>())
                if (item["parentId"] is { } parent && keep.Contains(parent.ToString())) changed |= keep.Add(item["_id"]!.ToString());
        } while (changed);
        return new JsonArray(items.OfType<JsonObject>().Where(i => keep.Contains(i["_id"]!.ToString()))
            .OrderByDescending(i => i["_id"]!.ToString() == root).Select(i => i.DeepClone()).ToArray());
    }
}
