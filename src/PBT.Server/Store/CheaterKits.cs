using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;

namespace PBT.Server;

// The look that gives a cheater away: welding masks and face shields down, melee nobody carries, a bare SA-58 drum gun,
// or no gear at all with a stock gun and a raid backpack.
public sealed partial class PopulationStore
{
    private const string SilencerParent = "550aa4cd4bdc2dd8348b456c";
    private const string Sa58 = "5b0bbe4e5acfc40dc528a72d";
    private const string Sa58Drum = "5b7bef9c5acfc43d102852ec";

    private static readonly string[] HeadSlots = ["Headwear", "Earpiece", "FaceCover", "Eyewear"];
    private static readonly string[] NakedDrops = ["ArmorVest", "TacticalVest", "Headwear", "Earpiece", "FaceCover", "Eyewear", "Holster", "SecondPrimaryWeapon", "Backpack"];

    // Stock guns, each with the magazine it runs. Null keeps the preset's own.
    private static readonly (string Gun, string? Magazine)[] NakedGuns =
    [
        ("59d6088586f774275f37482f", null), // AKM
        ("57d14d2524597714373db789", null), // PP-91 Kedr
        ("64637076203536ad5600c990", null), // PKM, already a 100-round box
        ("5447a9cd4bdc2dbd208b4567", "5c6592372e221600133e47d7") // M4A1 with the MAG5-100
    ];

    private static readonly string[] RaidBackpacks = ["5df8a4d786f77412672a1e3b", "59e763f286f7742ee57895da", "67458730df3c1da90b0b052b"];

    // Closet cheaters keep ordinary stats, so only their kit and in-raid play give them away.
    private bool Blatant(Career c) => IsCheater(c) && Stable(c.Id + ":closet") >= policy.Cheaters.ClosetShare;

    private bool LooksLikeCheater(JsonObject kit)
    {
        var heads = policy.Cheaters.Helmets.Select(h => h.Tpl).ToHashSet();
        bool head = SlotTemplate(kit, "Headwear") is { } hw && heads.Contains(hw) || SlotTemplate(kit, "FaceCover") is { } fc && heads.Contains(fc);
        bool naked = SlotTemplate(kit, "ArmorVest") is null && SlotTemplate(kit, "TacticalVest") is null && RaidBackpacks.Contains(SlotTemplate(kit, "Backpack"));
        return head || naked;
    }

    private JsonObject CheaterKit(Career c, JsonObject equipment)
    {
        var s = policy.Cheaters;
        var kit = equipment.DeepClone().AsObject();
        bool naked = Random.Shared.NextDouble() < s.NakedShare;
        try
        {
            if (naked) NakedKit(kit);
            else
            {
                if (Random.Shared.NextDouble() < s.HelmetChance) ApplyCheaterHelmet(kit);
                if (s.Melee.Count > 0) ReplaceSlot(kit, "Scabbard", CreateShowcaseCollectible(s.Melee[Random.Shared.Next(s.Melee.Count)]));
                if (Random.Shared.NextDouble() < s.Sa58Chance && BareSa58() is { } gun)
                {
                    RemoveSlots(kit, "SecondPrimaryWeapon");
                    ReplaceSlot(kit, "FirstPrimaryWeapon", gun);
                }
            }
        }
        catch (Exception ex)
        {
            if (showcaseWarnings.Add(c.Id + ":cheater-kit")) logger.Warning($"[PBT] Cheater kit for {c.Name} kept as APBS built it. {ex.GetBaseException().Message}");
            return equipment;
        }

        return ValidEquipment(kit) && HasVisibleLoadout(kit) ? kit : equipment;
    }

    private void NakedKit(JsonObject kit)
    {
        RemoveSlots(kit, NakedDrops);
        var (gunTpl, magazine) = NakedGuns[Random.Shared.Next(NakedGuns.Length)];
        if (CreateShowcaseCollectible(gunTpl) is { } gun)
        {
            if (magazine is not null) SwapPart(gun, "mod_magazine", magazine);
            ReplaceSlot(kit, "FirstPrimaryWeapon", gun);
        }

        ReplaceSlot(kit, "Backpack", CreateShowcaseCollectible(RaidBackpacks[Random.Shared.Next(RaidBackpacks.Length)]));
    }

    // Stock SA-58 with every sight and scope taken off, a suppressor on the muzzle and the 50-round drum.
    private JsonArray? BareSa58()
    {
        if (CreateShowcaseCollectible(Sa58) is not { } gun) return null;
        foreach (var item in gun.OfType<JsonObject>().ToList())
        {
            string? slot = item["slotId"]?.ToString();
            if (slot is null || !(slot.StartsWith("mod_sight") || slot.StartsWith("mod_scope"))) continue;
            if (SlotRequired(gun, item)) continue;
            RemoveTree(gun, item["_id"]!.ToString());
        }

        SwapPart(gun, "mod_magazine", Sa58Drum);
        // The outermost muzzle slot that takes a suppressor gets one. A brake on that slot comes off first.
        foreach (var owner in gun.OfType<JsonObject>().ToList())
        {
            var slot = ShowcaseProps(owner["_tpl"]!.ToString())["Slots"]?.AsArray().OfType<JsonObject>().FirstOrDefault(x => x["_name"]?.ToString() == "mod_muzzle");
            string? silencer = slot?["_props"]?["filters"]?.AsArray().OfType<JsonObject>().SelectMany(f => f["Filter"]?.AsArray() ?? [])
                .Select(v => v?.ToString()).FirstOrDefault(t => t is not null && IsId(t) && templates.Items.ContainsKey(new MongoId(t)) && ShowcaseBasedOn(t, SilencerParent));
            if (silencer is null) continue;
            if (gun.OfType<JsonObject>().FirstOrDefault(i => i["parentId"]?.ToString() == owner["_id"]!.ToString() && i["slotId"]?.ToString() == "mod_muzzle") is { } old)
                RemoveTree(gun, old["_id"]!.ToString());
            if (CreateShowcaseCollectible(silencer) is not { } part) break;
            Attach(gun, owner["_id"]!.ToString(), "mod_muzzle", part);
            break;
        }

        return ValidShowcaseTree(gun) ? gun : null;
    }

    private bool SlotRequired(JsonArray tree, JsonObject item)
    {
        var parent = tree.OfType<JsonObject>().FirstOrDefault(i => i["_id"]?.ToString() == item["parentId"]?.ToString());
        return parent is not null && ShowcaseProps(parent["_tpl"]!.ToString())["Slots"]?.AsArray().OfType<JsonObject>()
            .Any(s => s["_name"]?.ToString() == item["slotId"]?.ToString() && s["_required"]?.GetValue<bool>() == true) == true;
    }

    // Replaces whatever sits in the named mod slot, anywhere in the tree, with a fresh part.
    private void SwapPart(JsonArray tree, string slot, string tpl)
    {
        if (tree.OfType<JsonObject>().FirstOrDefault(i => i["slotId"]?.ToString() == slot) is not { } old || CreateShowcaseCollectible(tpl) is not { } part) return;
        string parent = old["parentId"]!.ToString();
        RemoveTree(tree, old["_id"]!.ToString());
        Attach(tree, parent, slot, part);
    }

    private static void Attach(JsonArray tree, string parent, string slot, JsonArray part)
    {
        var root = (JsonObject)part[0]!;
        root["parentId"] = parent;
        root["slotId"] = slot;
        root.Remove("location");
        foreach (var node in part) tree.Add(node!.DeepClone());
    }

    private static void RemoveTree(JsonArray tree, string id)
    {
        var remove = ItemTree(tree, id).Select(n => n!["_id"]!.ToString()).ToHashSet();
        foreach (var node in tree.OfType<JsonObject>().Where(i => remove.Contains(i["_id"]!.ToString())).ToArray()) tree.Remove(node);
    }

    private static void RemoveSlots(JsonObject kit, params string[] slots)
    {
        string root = kit["Id"]!.ToString();
        var items = kit["Items"]!.AsArray();
        foreach (var top in items.OfType<JsonObject>().Where(i => i["parentId"]?.ToString() == root && slots.Contains(i["slotId"]?.ToString())).ToArray())
            RemoveTree(items, top["_id"]!.ToString());
    }

    private static void ReplaceSlot(JsonObject kit, string slot, JsonArray? tree)
    {
        if (tree is null) return;
        RemoveSlots(kit, slot);
        Attach(kit["Items"]!.AsArray(), kit["Id"]!.ToString(), slot, tree);
    }

    // Caller holds the gate. Account aliases of this raid's cheaters, so the client can run their in-raid behaviour.
    public (bool Behaviour, List<int> Cheaters) RaidCheaters(MongoId session)
    {
        lock (gate)
        {
            if (!Ready || !policy.Cheaters.Enabled) return (false, []);
            var ids = encounters.Values.Where(e => CanAccess(e, session) && population.Careers.TryGetValue(e.CareerId, out var c) && IsCheater(c))
                .Select(e => e.AccountId).ToList();
            return (policy.Cheaters.Behaviour, ids);
        }
    }

    // Debug: the first PMCs of a raid are cheaters, whatever APBS's level range. Caller holds the gate.
    private Career? DebugCheater(string session, string side, HashSet<string> used)
    {
        int wanted = policy.Cheaters.DebugCheatersPerRaid;
        if (!policy.Cheaters.Enabled || wanted <= 0 || used.Count(id => population.Careers.TryGetValue(id, out var u) && IsCheater(u)) >= wanted) return null;
        var pool = population.Careers.Values.Where(c => !c.Retired && c.Side.Equals(side, StringComparison.OrdinalIgnoreCase) && !used.Contains(c.Id)).ToList();
        var pick = pool.Where(IsCheater).OrderBy(_ => Random.Shared.Next()).FirstOrDefault();
        if (pick is null)
        {
            pick = pool.Where(c => !Notable(c) && c.BannedUtc == 0).OrderBy(_ => Random.Shared.Next()).FirstOrDefault();
            if (pick is null) return null;
            MakeCheater(pick);
            dirty = true;
        }

        logger.Info($"[PBT] Debug: {pick.Name} spawns as a cheater in this raid.");
        return pick;
    }
}
