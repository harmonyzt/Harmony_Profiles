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
        if (method?.ReturnType != typeof(BotBaseInventory)) throw new MissingMethodException("SPT inventory generation contract changed.");
        // Last, so APBS's prefix has already built its own inventory that this replaces.
        owner.Patch(method, postfix: new HarmonyMethod(typeof(PopulationStore), nameof(RaidInventory)) { priority = Priority.Last });
    }

    private static void RaidInventory(ref BotBaseInventory __result, MongoId botId, MongoId sessionId, BotType botJsonTemplate,
        BotGenerationDetails botGenerationDetails)
    {
        var store = Active;
        if (store?.Ready != true || __result?.Items is null || __result.Equipment is null
            || ApbsIntegration.Current is not { LevelConfirmed: true, Career: { } career }) return;
        try { store.WearProfileLoadout(career, __result, botId, sessionId, botJsonTemplate, botGenerationDetails); }
        catch (Exception ex) { store.Report(ex); }
    }

    // Profile loadouts are only weapons and gear, so simulated ones get APBS loot added on top.
    private (JsonObject Equipment, bool NeedsLoot)? ProfileLoadout(Career c, string side)
    {
        lock (gate)
        {
            if (!policy.RetainProfileGearInRaid || c.Retired || c.NeedsRegear || !c.VisibleLoadout
                || !c.Side.Equals(side, StringComparison.OrdinalIgnoreCase)) return null;
            var equipment = CareerEquipment(c);
            if (!ValidEquipment(equipment) || !EquippedWeapons(equipment).Any()) return null;
            return (equipment.DeepClone().AsObject(), c.EquipmentIsSimulated);
        }
    }

    private void WearProfileLoadout(Career c, BotBaseInventory inventory, MongoId botId, MongoId session, BotType template, BotGenerationDetails details)
    {
        if (gearFactory is null || ProfileLoadout(c, details.Side ?? "") is not { } loadout) return;
        var (stored, needsLoot) = loadout;
        string root = inventory.Equipment!.Value.ToString();
        string storedRoot = stored["Id"]!.ToString();
        var ids = new Dictionary<string, string> { [storedRoot] = root };
        var replacement = new JsonArray();
        foreach (var item in stored["Items"]!.AsArray().OfType<JsonObject>())
        {
            string id = item["_id"]!.ToString();
            if (id == storedRoot) continue;
            ids[id] = new MongoId().ToString();
        }
        // The old kit's dogtag names the previous spawn; SPT adds a fresh one after inventory generation.
        var dogtags = new HashSet<string>(stored["Items"]!.AsArray().OfType<JsonObject>()
            .Where(i => i["parentId"]?.ToString() == storedRoot && i["slotId"]?.ToString() == "Dogtag").Select(i => i["_id"]!.ToString()));
        foreach (var item in stored["Items"]!.AsArray().OfType<JsonObject>())
        {
            string id = item["_id"]!.ToString();
            if (id == storedRoot || dogtags.Contains(id) || item["parentId"]?.ToString() is not { } parent
                || dogtags.Contains(parent) || !ids.ContainsKey(parent)) continue;
            var copy = item.DeepClone().AsObject();
            copy["_id"] = ids[id];
            copy["parentId"] = ids[parent];
            replacement.Add(copy);
        }
        var items = json.Deserialize<List<Item>>(replacement.ToJsonString())
            ?? throw new InvalidDataException("Cannot read the stored profile loadout.");
        var original = inventory.Items!.ToList();
        var equipped = new HashSet<string> { root };
        bool changed;
        do
        {
            changed = false;
            foreach (var item in original)
                if (item.ParentId is { } parent && equipped.Contains(parent)) changed |= equipped.Add(item.Id.ToString());
        } while (changed);
        equipped.Remove(root);
        try
        {
            inventory.Items = original.Where(i => !equipped.Contains(i.Id.ToString())).Concat(items).ToList();
            if (needsLoot)
            {
                inventoryContainers.ClearCache(botId);
                foreach (string slot in LootContainers) TrackContainer(botId, inventory, root, slot);
                gearFactory.AddLoot(botId, session, template, details, inventory);
                inventoryContainers.ClearCache(botId);
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
        if (container is null) return;
        inventoryContainers.AddEmptyContainerToBot(botId, Enum.Parse<EquipmentSlots>(slot), container);
        if (inventoryContainers.GetBotContainer(botId)?.TryGetValue(Enum.Parse<EquipmentSlots>(slot), out var details) != true) return;
        var grids = details!.ContainerDbItem.Properties?.Grids?.ToList() ?? [];
        foreach (var child in inventory.Items!.Where(i => i.ParentId == container.Id.ToString()))
        {
            int index = grids.FindIndex(g => g.Name == child.SlotId);
            if (index < 0 || index >= details.ContainerGridDetails.Count) continue;
            var node = JsonNode.Parse(json.Serialize(child.Location) ?? "null") as JsonObject;
            if (node?["x"] is null || node["y"] is null) continue;
            int x = node["x"]!.GetValue<int>(), y = node["y"]!.GetValue<int>();
            bool rotated = node["r"]?.ToString() is "Vertical" or "1";
            var size = itemHelper.GetItemSize(inventory.Items!, child.Id);
            if (size is null) continue;
            int width = rotated ? size.Height : size.Width, height = rotated ? size.Width : size.Height;
            var map = details.ContainerGridDetails[index].GridMap;
            for (int dy = 0; dy < height; dy++)
                for (int dx = 0; dx < width; dx++)
                    if (y + dy < map.GetLength(0) && x + dx < map.GetLength(1)) map[y + dy, x + dx] = 1;
        }
    }
}
