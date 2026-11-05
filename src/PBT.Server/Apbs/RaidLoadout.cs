using System.Text.Json.Nodes;
using HarmonyLib;
using SPTarkov.Server.Core.Generators.Bot;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Bots;

namespace PBT.Server;

public sealed partial class PopulationStore
{
    private static readonly string[] LootContainers = ["TacticalVest", "Pockets", "Backpack", "SecuredContainer"];

    private void InstallRaidLoadout(Harmony owner)
    {
        var method = AccessTools.Method(typeof(BotInventoryGenerator), nameof(BotInventoryGenerator.GenerateInventory),
            [typeof(MongoId), typeof(MongoId), typeof(BotType), typeof(BotGenerationDetails)]);
        if (method?.ReturnType != typeof(BotBaseInventory))
        {
            throw new MissingMethodException("SPT inventory generation contract changed.");
        }

        // Last, so APBS's prefix has already built its own inventory that this replaces.
        owner.Patch(method, postfix: new HarmonyMethod(typeof(PopulationStore), nameof(RaidInventory))
        {
            priority = Priority.Last
        });
    }

    private static void RaidInventory(ref BotBaseInventory __result, MongoId botId, MongoId sessionId, BotType botJsonTemplate, BotGenerationDetails botGenerationDetails)
    {
        var store = Active;
        if (store?.Ready != true || __result?.Items is null || __result.Equipment is null || ApbsIntegration.Current is not { LevelConfirmed: true, Career: { } career })
        {
            return;
        }

        try
        {
            store.WearProfileLoadout(career, __result, botId, sessionId, botJsonTemplate, botGenerationDetails);
        }
        catch (Exception ex)
        {
            store.Report(ex);
        }

        try
        {
            store.StockSupplies(career, __result, botId);
        }
        catch (Exception ex)
        {
            store.Report(ex);
        }
    }

    // Profile loadouts are only weapons and gear, so simulated ones get APBS loot added on top.
    private (JsonObject Equipment, bool NeedsLoot)? ProfileLoadout(Career c, string side)
    {
        lock (gate)
        {
            if (!policy.RetainProfileGearInRaid || c.Retired || c.NeedsRegear || !c.VisibleLoadout || !c.Side.Equals(side, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var equipment = CareerEquipment(c);
            if (!ValidEquipment(equipment) || !EquippedWeapons(equipment).Any())
            {
                return null;
            }

            return (equipment.DeepClone().AsObject(), c.EquipmentIsSimulated);
        }
    }

    private void WearProfileLoadout(Career c, BotBaseInventory inventory, MongoId botId, MongoId session, BotType template, BotGenerationDetails details)
    {
        if (gearFactory is null || ProfileLoadout(c, details.Side ?? "") is not { } loadout)
        {
            return;
        }

        var (stored, needsLoot) = loadout;

        // A captured killer kit is the fight's leftovers. Spawn it restocked, never with the empty mags it ended on.
        Restock(stored);
        string root = inventory.Equipment!.Value.ToString();
        string storedRoot = stored["Id"]!.ToString();
        var ids = new Dictionary<string, string>
        {
            [storedRoot] = root
        };

        var replacement = new JsonArray();

        foreach (var item in stored["Items"]!.AsArray().OfType<JsonObject>())
        {
            string id = item["_id"]!.ToString();
            if (id == storedRoot)
            {
                continue;
            }

            ids[id] = new MongoId().ToString();
        }

        // The old kit's dogtag names the previous spawn; SPT adds a fresh one after inventory generation.
        var dogtags = new HashSet<string>(stored["Items"]!.AsArray().OfType<JsonObject>().Where(i => i["parentId"]?.ToString() == storedRoot && i["slotId"]?.ToString() == "Dogtag")
            .Select(i => i["_id"]!.ToString()));

        foreach (var item in stored["Items"]!.AsArray().OfType<JsonObject>())
        {
            string id = item["_id"]!.ToString();
            if (id == storedRoot || dogtags.Contains(id) || item["parentId"]?.ToString() is not { } parent || dogtags.Contains(parent) || !ids.ContainsKey(parent))
            {
                continue;
            }

            var copy = item.DeepClone().AsObject();
            copy["_id"] = ids[id];
            copy["parentId"] = ids[parent];
            replacement.Add(copy);
        }

        var items = json.Deserialize<List<Item>>(replacement.ToJsonString()) ?? throw new InvalidDataException("Cannot read the stored profile loadout.");
        var original = inventory.Items!.ToList();
        var equipped = new HashSet<string>
        {
            root
        };
        bool changed;
        do
        {
            changed = false;
            foreach (var item in original)
            {
                if (item.ParentId is { } parent && equipped.Contains(parent))
                {
                    changed |= equipped.Add(item.Id.ToString());
                }
            }
        } while (changed);

        equipped.Remove(root);
        try
        {
            inventory.Items = original.Where(i => !equipped.Contains(i.Id.ToString())).Concat(items).ToList();
            inventoryContainers.ClearCache(botId);

            foreach (string slot in LootContainers)
            {
                TrackContainer(botId, inventory, root, slot);
            }

            AddSpareMagazines(botId, inventory, root);

            if (needsLoot)
            {
                gearFactory.AddLoot(botId, session, template, details, inventory);
            }

            inventoryContainers.ClearCache(botId);

            // Duplicate ids make the client hang children off the wrong item (inserts inside inserts).
            var seen = new HashSet<MongoId>();
            if (inventory.Items.Any(i => !seen.Add(i.Id)))
            {
                throw new InvalidDataException($"Profile loadout for {c.Name} produced duplicate item ids; kept the generated kit.");
            }
        }
        catch
        {
            inventoryContainers.ClearCache(botId);
            inventory.Items = original;

            throw;
        }
    }

    // SPT's container cache starts empty, so mark cells the stored magazines and ammo already use.
    private void TrackContainer(MongoId botId, BotBaseInventory inventory, string root, string slot)
    {
        var container = inventory.Items!.FirstOrDefault(i => i.ParentId == root && i.SlotId == slot);
        if (container is null)
        {
            return;
        }

        inventoryContainers.AddEmptyContainerToBot(botId, Enum.Parse<EquipmentSlots>(slot), container);
        if (inventoryContainers.GetBotContainer(botId)?.TryGetValue(Enum.Parse<EquipmentSlots>(slot), out var details) != true)
        {
            return;
        }

        var grids = details!.ContainerDbItem.Properties?.Grids?.ToList() ?? [];
        foreach (var child in inventory.Items!.Where(i => i.ParentId == container.Id.ToString()))
        {
            int index = grids.FindIndex(g => g.Name == child.SlotId);
            if (index < 0 || index >= details.ContainerGridDetails.Count)
            {
                continue;
            }

            var node = JsonNode.Parse(json.Serialize(child.Location) ?? "null") as JsonObject;

            if (node?["x"] is null || node["y"] is null)
            {
                continue;
            }

            int x = node["x"]!.GetValue<int>(), y = node["y"]!.GetValue<int>();
            bool rotated = node["r"]?.ToString() is "Vertical" or "1";
            var size = itemHelper.GetItemSize(inventory.Items!, child.Id);
            if (size is null)
            {
                continue;
            }

            int width = rotated ? size.Height : size.Width, height = rotated ? size.Width : size.Height;
            var map = details.ContainerGridDetails[index].GridMap;
            for (int dy = 0; dy < height; dy++)
            for (int dx = 0; dx < width; dx++)
                if (y + dy < map.GetLength(0) && x + dx < map.GetLength(1))
                {
                    map[y + dy, x + dx] = 1;
                }
        }
    }

    private static readonly MongoId MagazineClass = new("5448bc234bdc2d3c308b4569");
    private static readonly MongoId CylinderClass = new("627a137bf21bc425b06ab944");
    private const int SpareMagazines = 3;

    private TemplateItem? BoxMagazine(string? tpl) =>
        tpl is not null && IsId(tpl) && templates.Items.TryGetValue(new MongoId(tpl), out var t) && itemHelper.IsOfBaseclass(t.Id, MagazineClass)
        && !itemHelper.IsOfBaseclass(t.Id, CylinderClass) && t.Properties?.Cartridges?.FirstOrDefault()?.MaxCount > 0
            ? t
            : null;

    private void Restock(JsonObject equipment)
    {
        var items = equipment["Items"]!.AsArray();
        var all = items.OfType<JsonObject>().ToList();
        foreach (var mag in all)
        {
            if (BoxMagazine(mag["_tpl"]?.ToString()) is not { } template)
            {
                continue;
            }
            
            string magId = mag["_id"]!.ToString();
            int capacity = (int)template.Properties!.Cartridges!.First().MaxCount!.Value;
            var rounds = all.Where(i => i["parentId"]?.ToString() == magId && i["slotId"]?.ToString() == "cartridges").ToList();
            string? ammo = rounds.Select(r => r["_tpl"]?.ToString()).FirstOrDefault(t => t is not null) ?? AmmoFor(mag, all, template);

            if (ammo is null || !templates.Items.TryGetValue(new MongoId(ammo), out var ammoTemplate))
            {
                continue;
            }

            foreach (var r in rounds)
            {
                items.Remove(r);
            }
            
            int stack = Math.Max(1, ammoTemplate.Properties?.StackMaxSize ?? capacity);
            int stacks = (capacity + stack - 1) / stack;
            for (int index = 0, left = capacity; index < stacks; index++)
            {
                int count = Math.Min(stack, left);
                left -= count;
                var node = new JsonObject
                {
                    ["_id"] = new MongoId().ToString(),
                    ["_tpl"] = ammo,
                    ["parentId"] = magId,
                    ["slotId"] = "cartridges",
                    ["upd"] = new JsonObject
                    {
                        ["StackObjectsCount"] = count
                    }
                };
                // SPT leaves a single stack without a location.
                if (stacks > 1)
                {
                    node["location"] = index;
                }
                
                items.Add(node);
            }
        }
    }

    // An emptied mag has no rounds to copy, so take what the gun or the kit already shoots.
    private string? AmmoFor(JsonObject mag, List<JsonObject> all, TemplateItem template)
    {
        var filter = template.Properties?.Cartridges?.FirstOrDefault()?.Properties?.Filters?.FirstOrDefault()?.Filter;
        if (filter is null || filter.Count == 0)
        {
            return null;
        }
        
        bool Fits(string? tpl) => tpl is not null && IsId(tpl) && filter.Contains(new MongoId(tpl)) && templates.Items.ContainsKey(new MongoId(tpl));
        
        var owned = all.Select(i => i["_tpl"]?.ToString()).FirstOrDefault(Fits);
        if (owned is not null)
        {
            return owned;
        }
        
        var weapon = all.FirstOrDefault(i => i["_id"]?.ToString() == mag["parentId"]?.ToString());
        if (weapon?["_tpl"]?.ToString() is { } wtpl && IsId(wtpl) && templates.Items.TryGetValue(new MongoId(wtpl), out var w) && w.Properties?.DefAmmo is { } def
            && Fits(def.ToString()))
        {
            return def.ToString();
        }
        
        return filter.Select(f => f.ToString()).FirstOrDefault(Fits);
    }

    private void AddSpareMagazines(MongoId botId, BotBaseInventory inventory, string root)
    {
        var items = inventory.Items!;
        foreach (string slot in new[] { "FirstPrimaryWeapon", "SecondPrimaryWeapon", "Holster" })
        {
            var weapon = items.FirstOrDefault(i => i.ParentId == root && i.SlotId == slot);
            var mag = weapon is null ? null : items.FirstOrDefault(i => i.ParentId == weapon.Id.ToString() && i.SlotId == "mod_magazine");

            if (mag is null || BoxMagazine(mag.Template.ToString()) is null)
            {
                continue;
            }
            
            // Tube and fixed magazines cannot be swapped, the restock already filled them.
            if (!templates.Items.TryGetValue(weapon!.Template, out var gun) || gun.Properties?.ReloadMode != ReloadMode.ExternalMagazine)
            {
                break;
            }
            
            var holders = items.Where(i => i.ParentId == root && i.SlotId is "TacticalVest" or "Pockets" or "Backpack").Select(i => i.Id.ToString()).ToHashSet();
            int spares = items.Count(i => i.Template == mag.Template && i.ParentId is { } p && holders.Contains(p));
            var rounds = items.Where(i => i.ParentId == mag.Id.ToString()).ToList();
            
            for (; spares < SpareMagazines; spares++)
            {
                var copy = cloner.Clone(mag)!;
                copy.Id = new MongoId();
                copy.ParentId = null;
                copy.SlotId = null;
                copy.Location = null;
                var set = new List<Item>
                {
                    copy
                };
                foreach (var r in rounds)
                {
                    var round = cloner.Clone(r)!;
                    round.Id = new MongoId();
                    round.ParentId = copy.Id.ToString();
                    set.Add(round);
                }

                if (botGeneratorHelper.AddItemWithChildrenToEquipmentSlot(botId, [EquipmentSlots.TacticalVest, EquipmentSlots.Pockets], copy.Id, copy.Template, set, inventory)
                    != ItemAddedResult.SUCCESS) break;
            }

            // Spares for the main gun only.
            break;
        }
    }
}
