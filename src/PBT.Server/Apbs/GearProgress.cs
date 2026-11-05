using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;

namespace PBT.Server;

// Kits change the way a real player's do: survivors buy one better piece at a time, the dead rebuy and often keep their usual gun.
public sealed partial class PopulationStore
{
    private sealed record GearGroup(string Name, string[] Slots, int Weight);

    // Head slots block each other and the rig carries the gun's magazines, so those move together.
    private static readonly GearGroup[] GearGroups =
    [
        new("weapon", ["FirstPrimaryWeapon", "SecondPrimaryWeapon", "TacticalVest", "Pockets"], 4),
        new("armor", ["ArmorVest"], 3),
        new("helmet", ["Headwear", "Earpiece", "FaceCover", "Eyewear"], 2),
        new("backpack", ["Backpack"], 1),
        new("sidearm", ["Holster"], 1)
    ];

    private const int GearHistorySize = 12;

    private bool Upgrading(Career c) => policy.GearProgression && !c.NeedsRegear && c.GearVersion > 0 && c.VisibleLoadout;

    private static IEnumerable<JsonObject> GearItems(JsonObject equipment) => equipment["Items"]!.AsArray().OfType<JsonObject>();

    private static string? SlotTemplate(JsonObject equipment, string slot)
    {
        string root = equipment["Id"]!.ToString();
        return GearItems(equipment).FirstOrDefault(i => i["parentId"]?.ToString() == root && i["slotId"]?.ToString() == slot)?["_tpl"]?.ToString();
    }

    private static List<JsonObject> Subtree(JsonObject equipment, IEnumerable<string> slots)
    {
        string root = equipment["Id"]!.ToString();
        var wanted = slots.ToHashSet();
        var all = GearItems(equipment).ToArray();
        var ids = all.Where(i => i["parentId"]?.ToString() == root && wanted.Contains(i["slotId"]?.ToString() ?? ""))
            .Select(i => i["_id"]!.ToString()).ToHashSet();
        bool changed;
        do
        {
            changed = false;
            foreach (var i in all)
                if (i["parentId"]?.ToString() is { } parent && ids.Contains(parent)) changed |= ids.Add(i["_id"]!.ToString());
        } while (changed);
        return all.Where(i => ids.Contains(i["_id"]!.ToString())).ToList();
    }

    private static double StackCount(JsonObject item) => item["upd"]?["StackObjectsCount"] is JsonValue v
        ? v.TryGetValue<double>(out double d) ? Math.Max(1, d) : v.TryGetValue<int>(out int n) ? Math.Max(1, n) : 1 : 1;

    private double GearValue(IEnumerable<JsonObject> items)
    {
        var handbook = HandbookPrices();
        return items.Sum(i =>
        {
            string tpl = i["_tpl"]?.ToString() ?? "";
            if (!IsId(tpl)) return 0;
            double price = templates.Prices.TryGetValue(new MongoId(tpl), out double flea) ? flea : handbook.GetValueOrDefault(tpl);
            return price * StackCount(i);
        });
    }

    // The secure container and dogtag are not for sale.
    private double KitValue(JsonObject equipment) => ValidEquipment(equipment)
        ? GearValue(Subtree(equipment, ["FirstPrimaryWeapon", "SecondPrimaryWeapon", "Holster", "Scabbard", "TacticalVest", "ArmorVest",
            "Pockets", "Backpack", "Headwear", "Earpiece", "FaceCover", "Eyewear", "ArmBand"])) : 0;

    private bool BlocksArmor(JsonObject equipment) => SlotTemplate(equipment, "TacticalVest") is { } tpl && IsId(tpl)
        && templates.Items.TryGetValue(new MongoId(tpl), out var template) && template.Properties?.BlocksArmorVest == true;

    // An armored rig takes the armor slot with it.
    private string[] GroupSlots(GearGroup group, JsonObject a, JsonObject b) =>
        group.Name == "weapon" && (BlocksArmor(a) || BlocksArmor(b)) ? [.. group.Slots, "ArmorVest"] : group.Slots;

    private static JsonObject SwapGroup(JsonObject target, JsonObject source, string[] slots)
    {
        string root = target["Id"]!.ToString();
        var drop = Subtree(target, slots).Select(i => i["_id"]!.ToString()).ToHashSet();
        var take = Subtree(source, slots);
        var ids = take.ToDictionary(i => i["_id"]!.ToString(), _ => new MongoId().ToString());
        ids[source["Id"]!.ToString()] = root;
        var items = new JsonArray(GearItems(target).Where(i => !drop.Contains(i["_id"]!.ToString())).Select(i => (JsonNode)i.DeepClone()).ToArray());
        foreach (var item in take)
        {
            var copy = item.DeepClone().AsObject();
            copy["_id"] = ids[item["_id"]!.ToString()];
            copy["parentId"] = ids[item["parentId"]!.ToString()];
            items.Add(copy);
        }
        return new JsonObject { ["Id"] = root, ["Items"] = items };
    }

    private string GearName(JsonObject equipment, string slot)
    {
        if (SlotTemplate(equipment, slot) is not { } tpl) return "";
        try
        {
            var names = locales.GetLocaleDb();
            return names.GetValueOrDefault(tpl + " ShortName") ?? names.GetValueOrDefault(tpl + " Name") ?? "";
        }
        catch { return ""; }
    }

    private static string A(string name) => ("aeiouAEIOU".Contains(name[0]) || name.StartsWith("M4") || name.StartsWith("MP") || name.StartsWith("SR")
        || name.StartsWith("SV") || name.StartsWith("RSh") || name.StartsWith("RPK") || name.StartsWith("HK") ? "an " : "a ") + name;

    private string GroupLabel(GearGroup group, JsonObject kit)
    {
        string name = GearName(kit, group.Slots[0]);
        return group.Name switch
        {
            "weapon" => name.Length > 0 ? A(name) : "a new primary",
            "armor" => name.Length > 0 ? $"{name} armor" : "better armor",
            "helmet" => name.Length > 0 ? A(name) + " helmet" : "better headgear",
            "backpack" => name.Length > 0 ? A(name) + " backpack" : "a bigger backpack",
            _ => name.Length > 0 ? A(name) + " sidearm" : "a better sidearm"
        };
    }

    private static void RecordGear(Career c, string kind, string text, double rubles, long now)
    {
        c.GearHistory.Insert(0, new GearEvent(now, kind, text, Math.Round(rubles)));
        if (c.GearHistory.Count > GearHistorySize) c.GearHistory.RemoveRange(GearHistorySize, c.GearHistory.Count - GearHistorySize);
    }

    private void KeepGear(Career c, long now)
    {
        c.GearPending = false;
        c.LastGearUpdateUtc = now;
        c.NextGearAttemptUtc = now + Random.Shared.Next(600, 1801);
        dirty = true;
    }

    // Caller holds the gate.
    private JsonObject? UpgradeGear(Career c, JsonObject candidate, long now)
    {
        var current = CareerEquipment(c);
        if (!ValidEquipment(current) || !HasVisibleLoadout(current)) return candidate;
        // Players keep money for a couple of deaths instead of spending their last ruble on gear.
        double spare = c.WealthRubles - KitCost(c) * 2;
        var options = new List<(GearGroup Group, string[] Slots, double Cost)>();
        foreach (var group in GearGroups)
        {
            if (group.Name == "armor" && BlocksArmor(current)) continue;
            var slots = GroupSlots(group, current, candidate);
            double owned = GearValue(Subtree(current, slots)), bought = GearValue(Subtree(candidate, slots));
            // The old piece is sold, which pays for part of the new one.
            double cost = Math.Max(0, bought - owned * .5);
            if (bought <= 0 || bought < owned * 1.15 || cost > spare) continue;
            options.Add((group, slots, cost));
        }
        if (options.Count == 0) return null;
        int roll = Random.Shared.Next(options.Sum(o => o.Group.Weight));
        var pick = options.First(o => (roll -= o.Group.Weight) < 0);
        var result = SwapGroup(current, candidate, pick.Slots);
        if (!ValidEquipment(result) || !HasVisibleLoadout(result)) return null;
        c.WealthRubles = Math.Max(0, c.WealthRubles - pick.Cost);
        string label = GroupLabel(pick.Group, candidate);
        Log("gear", $"{c.Name} upgraded to {label}");
        RecordGear(c, "upgrade", $"Upgraded to {label}", -pick.Cost, now);
        return result;
    }

    // The death already took a kit's worth of wealth in the simulation, so the rebuy records that cost.
    private JsonObject Rebuy(Career c, JsonObject fresh, long now)
    {
        if (c.DiedUtc == 0)
        {
            RecordGear(c, "rebuy", c.PrestigeLevel > 0 && c.GearHistory.Count > 0 ? "Started over with a fresh kit" : "Bought a first kit", 0, now);
            return fresh;
        }
        double cost = -KitCost(c);
        var previous = CareerEquipment(c);
        string gun = ValidEquipment(previous) ? GearName(previous, "FirstPrimaryWeapon") : "";
        double roll = Random.Shared.NextDouble();
        bool insured = roll < policy.InsuranceReturnChance;
        bool usual = !insured && roll < policy.InsuranceReturnChance + .25 && FortuneTiers(c, false) >= 0;
        if (gun.Length > 0 && (insured || usual))
        {
            var kept = SwapGroup(fresh, previous, GroupSlots(GearGroups[0], fresh, previous));
            if (ValidEquipment(kept) && HasVisibleLoadout(kept))
            {
                RecordGear(c, insured ? "insurance" : "rebuy",
                    insured ? $"Got the {gun} back from insurance" : $"Rebought the usual {gun} loadout", cost, now);
                return kept;
            }
        }
        string next = GearName(fresh, "FirstPrimaryWeapon");
        RecordGear(c, "rebuy", next.Length > 0 ? $"Bought a new kit with {A(next)}" : "Bought a new kit", cost, now);
        return fresh;
    }
}
