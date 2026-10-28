using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Hideout;

namespace HarmonyProfiles.Server;

public sealed partial class PopulationStore
{
    private readonly Dictionary<string, JsonObject> nativeCharacters = new();

    private JsonObject NativeCharacter(string side, string edition = "Standard")
    {
        string key = side + ":" + edition;
        if (nativeCharacters.TryGetValue(key, out var cached)) return cached;
        if (!templates.Profiles.TryGetValue(edition, out var profile))
            throw new InvalidDataException("Native edition template is missing: " + edition);
        var source = (side == "Bear" ? profile.Bear : profile.Usec)?.Character
            ?? throw new InvalidDataException("Native character template is missing for " + key);
        var character = JsonNode.Parse(json.Serialize(source)!)!.AsObject();
        nativeCharacters.Add(key, character);
        return character;
    }

    private void EnsureDevelopment(Career c)
    {
        if (c.DevelopmentVersion > 2) throw new InvalidDataException("Unknown career development version.");
        if (c.DevelopmentVersion == 2) return;
        var native = NativeCharacter(c.Side, c.Edition);
        if (c.DevelopmentVersion == 0)
        {
            c.Skills = native["Skills"]!.DeepClone().AsObject();
            c.Hideout = native["Hideout"]!.DeepClone().AsObject();
            c.Hideout["Seed"] = Guid.NewGuid().ToString("N");
            c.Hideout["HideoutCounters"] = new JsonObject
                { ["fuelCounter"] = 0, ["airFilterCounter"] = 0, ["waterFilterCounter"] = 0, ["craftingTimeCounter"] = 0 };
            c.CustomizationStashId = NewId();
            c.HideoutAreaStashes = new JsonObject();
            c.HideoutItems = new JsonArray(new JsonObject
                { ["_id"] = c.CustomizationStashId, ["_tpl"] = "673c7b00cbf4b984b5099181" });
            c.Stash = GenerateStash(c, native["Inventory"]!.AsObject());
            double hours = CycleHours(c);
            c.WealthRubles = Math.Clamp(100000 + hours * (c.Archetype == "Timmy" ? 5000 : 20000), 100000, 40_000_000);
        }
        // Edition migration raises native starting privileges without resetting existing construction or items.
        var states = c.Hideout["Areas"]!.AsArray();
        foreach (var baseline in native["Hideout"]!["Areas"]!.AsArray().OfType<JsonObject>())
        {
            int type = baseline["type"]!.GetValue<int>();
            var current = states.OfType<JsonObject>().FirstOrDefault(a => a["type"]!.GetValue<int>() == type);
            if (current is null) states.Add(baseline.DeepClone());
            else if (current["level"]!.GetValue<int>() < baseline["level"]!.GetValue<int>())
            {
                current["level"] = baseline["level"]!.DeepClone();
                current["constructing"] = false;
                current["completeTime"] = 0;
            }
        }
        SyncStashTemplate(c);
        c.DevelopmentVersion = 2;
        dirty = true;
    }

    private string StashTemplate(Career c)
    {
        var native = NativeCharacter(c.Side, c.Edition);
        int baseline = native["Hideout"]!["Areas"]!.AsArray().OfType<JsonObject>()
            .First(a => a["type"]!.GetValue<int>() == 3)["level"]!.GetValue<int>();
        if (AreaLevel(c, 3) > baseline)
        {
            var area = hideout.Areas.FirstOrDefault(a => (int?)a.Type == 3);
            if (area?.Stages?.TryGetValue(AreaLevel(c, 3).ToString(), out var stage) == true
                && stage.Container is MongoId container && !container.IsEmpty && templates.Items.ContainsKey(container))
                return container.ToString();
        }
        var inventory = native["Inventory"]!.AsObject();
        string root = inventory["stash"]!.GetValue<string>();
        return inventory["items"]!.AsArray().OfType<JsonObject>()
            .First(i => i["_id"]?.ToString() == root && i["parentId"] is null)["_tpl"]!.GetValue<string>();
    }

    private void SyncStashTemplate(Career c)
    {
        string template = StashTemplate(c);
        string? root = c.Stash["stash"]?.ToString();
        var item = (c.Stash["items"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(i => i["_id"]?.ToString() == root);
        if (item is not null) item["_tpl"] = template;
    }

    private JsonObject GenerateStash(Career c, JsonObject inventory)
    {
        string oldRoot = inventory["stash"]!.GetValue<string>();
        var all = inventory["items"]!.AsArray().OfType<JsonObject>().ToArray();
        int wanted = Math.Clamp(8 + (int)(CycleHours(c) / 30), 8, 60);
        var roots = all.Where(x => x["parentId"]?.GetValue<string>() == oldRoot)
            .OrderBy(x => Stable(c.Id + ":" + x["_id"])).Take(wanted).ToArray();
        bool changed;
        var allowedRoots = roots.Select(x => x["_id"]!.GetValue<string>()).ToHashSet();
        var keep = new HashSet<string>(allowedRoots);
        do
        {
            changed = false;
            foreach (var item in all)
                if (item["parentId"] is JsonValue parent && keep.Contains(parent.GetValue<string>()))
                    changed |= keep.Add(item["_id"]!.GetValue<string>());
        } while (changed);
        keep.Add(oldRoot);
        var ids = keep.ToDictionary(id => id, _ => NewId());
        var result = new JsonArray();
        foreach (var item in all.Where(x => keep.Contains(x["_id"]!.GetValue<string>())
            && (x["_id"]!.ToString() == oldRoot && x["parentId"] is null
                || x["parentId"] is not null && keep.Contains(x["parentId"]!.ToString())))
            .DistinctBy(x => x["_id"]!.ToString()))
        {
            var copy = item.DeepClone().AsObject();
            copy["_id"] = ids[item["_id"]!.GetValue<string>()];
            if (item["parentId"] is JsonValue parent && ids.TryGetValue(parent.GetValue<string>(), out var newParent))
                copy["parentId"] = newParent;
            result.Add(copy);
        }
        return new JsonObject { ["stash"] = ids[oldRoot], ["items"] = result };
    }

    private void Develop(Career c)
    {
        EnsureDevelopment(c);
        double hours = CycleHours(c);
        if (c.Skills["Common"] is JsonArray skills)
            foreach (var skill in skills.OfType<JsonObject>())
            {
                string id = skill["Id"]!.GetValue<string>();
                double rate = id is "Endurance" or "Strength" ? 2.5 : 1.3;
                double progress = Math.Min(5100, c.SkillCarry.GetValueOrDefault(id) + hours * rate * (.5 + Stable(c.Id + ":" + id)));
                skill["Progress"] = Math.Max(skill["Progress"]?.GetValue<double>() ?? 0, progress);
            }
        UpdateTraders(c, hours);
        AdvanceHideout(c, hours);
        EnsureShowcase(c);
        c.Achievements = EarnedMilestones(c, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        dirty = true;
    }

    private void UpdateTraders(Career c, double hours)
    {
        int playerLevel = Level(c.Experience);
        foreach (var (id, trader) in traders)
        {
            double sales = hours * (c.Archetype == "Timmy" ? 3000 : 12000);
            double standing = Math.Min(1.5, hours / 800);
            int loyalty = 0;
            if (trader.Base.LoyaltyLevels is not null)
                for (int i = 0; i < trader.Base.LoyaltyLevels.Count; i++)
                {
                    var stage = trader.Base.LoyaltyLevels[i];
                    if (playerLevel >= (stage.MinLevel ?? 1) && sales >= (stage.MinSalesSum ?? 0)
                        && standing >= (stage.MinStanding ?? 0)) loyalty = i + 1;
                    else break;
                }
            c.TradersInfo[id.ToString()] = new JsonObject { ["loyaltyLevel"] = loyalty,
                ["salesSum"] = sales, ["standing"] = standing, ["unlocked"] = loyalty > 0 };
        }
    }

    private void AdvanceHideout(Career c, double hours)
    {
        var allAreas = hideout.Areas.Concat(hideout.CustomAreas ?? []).Where(a => a.IsEnabled != false && a.Type is not null)
            .GroupBy(a => (int)a.Type!.Value).Select(g => g.First()).ToArray();
        var states = c.Hideout["Areas"]!.AsArray();
        var levels = states.OfType<JsonObject>().ToDictionary(a => a["type"]!.GetValue<int>(),
            a => a["level"]!.GetValue<int>());
        foreach (var area in allAreas)
        {
            int type = (int)area.Type!.Value;
            if (!levels.TryAdd(type, 0)) continue;
            states.Add(new JsonObject { ["type"] = type, ["level"] = 0, ["active"] = false,
                ["constructing"] = false, ["completeTime"] = 0, ["passiveBonusesEnabled"] = false, ["slots"] = new JsonArray() });
        }
        // Advance one stage at a time until all admitted dependencies settle. Unknown requirements block locally.
        bool upgraded;
        do
        {
            upgraded = false;
            foreach (var area in allAreas.OrderBy(a => Stable(c.Id + ":area:" + (int)a.Type!.Value)))
            {
                int type = (int)area.Type!.Value;
                int next = levels[type] + 1;
                if (area.Stages is null || !area.Stages.TryGetValue(next.ToString(), out var stage)) continue;
                double focus = .65 + Stable(c.Id + ":focus:" + type);
                int maximum = area.Stages.Keys.Select(k => int.TryParse(k, out int n) ? n : 0).Max();
                if (type is 21 or 22 || next > DesiredHideoutLevel(c, type, hours, maximum)) continue;
                double firstStageHours = type is 0 or 1 or 2 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 15 ? 4 : 25;
                double threshold = firstStageHours * next * next / (focus * policy.Hideout.ConstructionSpeed);
                if (hours < threshold || !AreaRequirements(area.Requirements, levels)) continue;
                double cost = StageCost(c, stage, levels) * policy.Hideout.MaterialCostMultiplier;
                if (!double.IsFinite(cost) || cost > c.WealthRubles) continue;
                c.WealthRubles -= cost;
                c.HideoutInvestment += cost;
                levels[type] = next;
                upgraded = true;
            }
        } while (upgraded);
        foreach (var area in allAreas)
        {
            int type = (int)area.Type!.Value;
            int level = levels[type];
            if (area.Stages is null || !area.Stages.TryGetValue(level.ToString(), out var stage)) continue;
            var state = states.OfType<JsonObject>().First(a => a["type"]!.GetValue<int>() == type);
            state["level"] = level;
            state["active"] = level > 0 && area.NeedsFuel != true;
            state["passiveBonusesEnabled"] = level > 0 && area.NeedsFuel != true;
            int count = stage.Slots ?? 0;
            if (count is < 0 or > 128) continue;
            var slots = new JsonArray();
            for (int i = 0; i < count; i++) slots.Add(new JsonObject { ["locationIndex"] = i });
            state["slots"] = slots;
            if (stage.Container is not MongoId container || container.IsEmpty || !templates.Items.ContainsKey(container)) continue;
            if (type == 3) container = new MongoId(StashTemplate(c));
            string key = type.ToString();
            string root = c.HideoutAreaStashes[key]?.GetValue<string>() ?? NewId();
            c.HideoutAreaStashes[key] = root;
            var rootItem = c.HideoutItems.OfType<JsonObject>().FirstOrDefault(i => i["_id"]?.GetValue<string>() == root);
            if (rootItem is null) c.HideoutItems.Add(new JsonObject { ["_id"] = root, ["_tpl"] = container.ToString() });
            else rootItem["_tpl"] = container.ToString();
        }
        SyncStashTemplate(c);
    }

    private static bool AreaRequirements(List<HideoutAreaRequirement>? requirements, Dictionary<int, int> levels) =>
        requirements is null || requirements.All(r => r.Type == "Area" && r.AreaType is int type
            && levels.GetValueOrDefault(type) >= (r.RequiredLevel ?? 0));

    private double StageCost(Career c, Stage stage, Dictionary<int, int> levels)
    {
        double cost = 0;
        foreach (var r in stage.Requirements ?? [])
        {
            switch (r.Type)
            {
                case "Area":
                    if (r.AreaType is not int type || levels.GetValueOrDefault(type) < (r.RequiredLevel ?? 0)) return double.PositiveInfinity;
                    break;
                case "Skill":
                    var skill = c.Skills["Common"]!.AsArray().OfType<JsonObject>()
                        .FirstOrDefault(s => s["Id"]?.GetValue<string>() == r.SkillName);
                    // A conservative progress threshold for the low native Hideout skill requirements.
                    if ((skill?["Progress"]?.GetValue<double>() ?? 0) < (r.SkillLevel ?? 0) * 100) return double.PositiveInfinity;
                    break;
                case "TraderLoyalty":
                    if ((c.TradersInfo[r.TraderId.ToString()]?["loyaltyLevel"]?.GetValue<int>() ?? 0) < (r.LoyaltyLevel ?? 0))
                        return double.PositiveInfinity;
                    break;
                case "Item":
                    if (!templates.Items.ContainsKey(r.TemplateId) || r.Count is not int count || count < 0) return double.PositiveInfinity;
                    double price = templates.Prices.GetValueOrDefault(r.TemplateId, 10000);
                    cost += Math.Max(1, price) * count;
                    if (r.IsSpawnedInSession == true && c.Raids < count) return double.PositiveInfinity;
                    break;
                default: return double.PositiveInfinity;
            }
        }
        return cost;
    }

    private void ApplyAppearance(Career c, BotBase bot)
    {
        if (bot.Customization is null) return;
        MongoId? Asset(string key)
        {
            string? id = c.Customization[key]?.GetValue<string>();
            return IsId(id) && templates.Customization.ContainsKey(new MongoId(id!)) ? new MongoId(id!) : (MongoId?)null;
        }
        if (settings.PersistentFaceAndVoice)
        {
            bot.Customization.Head = Asset("Head") ?? bot.Customization.Head;
            bot.Customization.Voice = Asset("Voice") ?? bot.Customization.Voice;
        }
        if (settings.PersistentClothing && Asset("Body") is MongoId body && Asset("Feet") is MongoId feet
            && Asset("Hands") is MongoId hands)
        {
            bot.Customization.Body = body;
            bot.Customization.Feet = feet;
            bot.Customization.Hands = hands;
        }
    }

    private static double Stable(string key) =>
        BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0) / (double)uint.MaxValue;
}
