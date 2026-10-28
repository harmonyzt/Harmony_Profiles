using System.Text.Json.Nodes;

namespace HarmonyProfiles.Server;

public sealed partial class PopulationStore
{
    private sealed record Milestone(string Id, string Side, string Counter, double Threshold);
    private readonly List<Milestone> milestones = [];
    private readonly HashSet<string> installedAchievements = [];

    private void LoadMilestones()
    {
        foreach (var achievement in (templates.Achievements ?? []).Concat(templates.CustomAchievements ?? []))
        {
            string id = achievement.Id.ToString();
            installedAchievements.Add(id);
            try
            {
                var node = JsonNode.Parse(json.Serialize(achievement)!)!.AsObject();
                if (!achievement.Hidden && achievement.Side is "All" or "Pmc" or "Usec" or "Bear"
                    && !node.ToJsonString().Contains("Prestige", StringComparison.OrdinalIgnoreCase))
                    achievementStories[id] = (achievement.Side, achievement.Rarity);
                if (node["hidden"]?.GetValue<bool>() == true || node["conditions"] is not JsonObject conditions
                    || !Keys(conditions, "started", "availableForStart", "availableForFinish", "success", "fail")
                    || conditions.Any(p => p.Key != "availableForFinish" && !Empty(p.Value))
                    || conditions["availableForFinish"] is not JsonArray { Count: 1 } finish
                    || finish[0] is not JsonObject condition || !PlainCounter(condition)) continue;
                string side = node["side"]?.GetValue<string>() ?? "";
                if (side is not ("All" or "Pmc" or "Usec" or "Bear")) continue;
                double threshold = condition["value"]?.GetValue<double>() ?? 0;
                if (!double.IsFinite(threshold) || threshold <= 0 || threshold != Math.Floor(threshold)) continue;
                string? counter = CounterName(condition);
                if (counter is not null) milestones.Add(new(id, side, counter, threshold));
            }
            catch (Exception ex)
            {
                logger.Warning("[Harmony-Profiles] Achievement rule skipped: " + id + ": " + ex.Message);
            }
        }
        logger.Info($"[Harmony-Profiles] {milestones.Count} supported lifetime achievement rules loaded.");
    }

    private static bool Keys(JsonObject node, params string[] allowed) => node.All(p => allowed.Contains(p.Key));
    private static bool Empty(JsonNode? node) => node is null || node is JsonArray { Count: 0 }
        || node is JsonValue value && (value.ToJsonString() is "false" or "0" or "\"\"");

    private static bool PlainCounter(JsonObject node) =>
        Keys(node, "id", "index", "dynamicLocale", "visibilityConditions", "globalQuestCounterId", "parentId",
            "value", "type", "oneSessionOnly", "completeInSeconds", "doNotResetIfCounterCompleted",
            "isResetOnConditionFailed", "isNecessary", "counter", "epicGamesId", "steamGamesId", "conditionType")
        && new[] { "visibilityConditions", "globalQuestCounterId", "parentId", "oneSessionOnly", "completeInSeconds",
            "isResetOnConditionFailed", "isNecessary" }.All(k => Empty(node[k]))
        && node["conditionType"]?.GetValue<string>() == "CounterCreator"
        && node["type"]?.GetValue<string>() == "Completion";

    private static string? CounterName(JsonObject condition)
    {
        if (condition["counter"] is not JsonObject counter || !Keys(counter, "id", "conditions")
            || counter["conditions"] is not JsonArray { Count: 1 } rules || rules[0] is not JsonObject rule) return null;
        string? type = rule["conditionType"]?.GetValue<string>();
        if (type == "ExitStatus")
            return Keys(rule, "id", "dynamicLocale", "status", "conditionType")
                && rule["status"] is JsonArray { Count: 1 } statuses && statuses[0]?.GetValue<string>() == "Survived"
                    ? "Survived" : null;
        if (type != "Kills" || !Keys(rule, "id", "dynamicLocale", "target", "compareMethod", "value", "weapon",
                "distance", "weaponModsInclusive", "weaponModsExclusive", "enemyEquipmentInclusive", "enemyEquipmentExclusive",
                "weaponCaliber", "savageRole", "bodyPart", "daytime", "enemyHealthEffects", "resetOnSessionEnd", "conditionType")
            || new[] { "weapon", "weaponModsInclusive", "weaponModsExclusive", "enemyEquipmentInclusive", "enemyEquipmentExclusive",
                "weaponCaliber", "savageRole", "bodyPart", "enemyHealthEffects", "resetOnSessionEnd" }.Any(k => !Empty(rule[k]))
            || rule["compareMethod"]?.GetValue<string>() != ">=" || rule["value"]?.GetValue<double>() != 1
            || rule["distance"] is not JsonObject distance || !Keys(distance, "value", "compareMethod")
            || distance["value"]?.GetValue<double>() != 0 || distance["compareMethod"]?.GetValue<string>() != ">="
            || rule["daytime"] is not JsonObject daytime || !Keys(daytime, "from", "to")
            || daytime["from"]?.GetValue<double>() != 0 || daytime["to"]?.GetValue<double>() != 0) return null;
        return rule["target"]?.GetValue<string>() switch
        {
            "Usec" => "UsecKills", "Bear" => "BearKills", "Savage" => "ScavKills", _ => null
        };
    }

    private Dictionary<string, long> EarnedMilestones(Career career, long now)
    {
        var result = new Dictionary<string, long>(career.Achievements);
        // Fictional history has a stable faction split. Unclassified observed PMC kills stay unclassified.
        long modeledUsec = (long)Math.Floor(career.PmcKills * (.35 + .3 * Stable(career.Id + ":kill-side")));
        foreach (var milestone in milestones)
        {
            if (result.ContainsKey(milestone.Id) || milestone.Side is not ("All" or "Pmc") && milestone.Side != career.Side) continue;
            long value = milestone.Counter switch
            {
                "Survived" => career.Survived,
                "UsecKills" => modeledUsec + career.Observed.UsecKills,
                "BearKills" => career.PmcKills - modeledUsec + career.Observed.BearKills,
                "ScavKills" => career.ScavKills + career.Observed.ScavKills,
                _ => 0
            };
            double hours = (career.RaidSeconds + career.Observed.ActiveSeconds) / 3600;
            string rarity = achievementStories.TryGetValue(milestone.Id, out var story) ? story.Rarity : "Rare";
            if (value >= milestone.Threshold && hours >= BadgeHours(milestone.Id, rarity))
                result.Add(milestone.Id, Math.Max(career.RegisteredUtc, now));
        }
        AddCommonStories(career, result, now);
        return result;
    }

    private JsonObject PublicAchievements(Career career)
    {
        var result = new JsonObject();
        foreach (var (id, earned) in career.Achievements)
            if (installedAchievements.Contains(id)) result[id] = earned;
        return result;
    }

    private void RestoreLatestEquipment()
    {
        foreach (var group in encounters.Values.GroupBy(e => e.CareerId))
        {
            var career = population.Careers[group.Key];
            var latest = group.OrderByDescending(e => e.GenerationOrder).ThenByDescending(e => e.GeneratedUtc)
                .ThenByDescending(e => e.AccountId == career.LastEquipmentAccount)
                .ThenByDescending(e => e.Equipment["Id"]?.ToString() == career.Equipment["Id"]?.ToString())
                .ThenByDescending(e => e.AccountId).First();
            // Expiration of the newest alias must not resurrect an older, longer-retained killer loadout.
            if ((!career.EquipmentIsSimulated && career.LastEquipmentAccount == 0) || latest.AccountId == career.LastEquipmentAccount
                || latest.GenerationOrder > career.LastEquipmentOrder
                || latest.GenerationOrder == career.LastEquipmentOrder && latest.GeneratedUtc > career.LastEquipmentGeneratedUtc)
                RememberEquipment(career, latest);
        }
    }

    private void RememberEquipment(Career career, Encounter encounter)
    {
        career.LastEquipmentAccount = encounter.AccountId;
        career.EquipmentIsSimulated = false;
        career.GearVersion = 1;
        career.GearPending = false;
        career.LastGearUpdateUtc = encounter.GeneratedUtc;
        career.LastEquipmentOrder = encounter.GenerationOrder;
        career.SimulatedLoadoutOrder = population.LastGenerationOrder;
        career.LastEquipmentGeneratedUtc = encounter.GeneratedUtc;
        career.Equipment = encounter.Equipment.DeepClone().AsObject();
        career.Customization = encounter.Customization.DeepClone().AsObject();
        dirty = true;
    }
}
