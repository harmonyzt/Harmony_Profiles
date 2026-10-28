using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace HarmonyProfiles.Server;

public sealed partial class PopulationStore
{
    private sealed record DisplaySpace(string Name, JsonObject Props, int Width, int Height);
    private readonly Dictionary<(int Area, int Level), List<DisplaySpace>> displaySpaces = new();
    private readonly Dictionary<string, JsonObject> showcaseProperties = new();
    private readonly Dictionary<string, (long Due, int Attempts)> showcaseQueue = new();
    private Dictionary<string, double>? showcasePrices;
    private readonly Dictionary<int, List<KeyValuePair<string, double>>> trophyCatalogs = new();
    private readonly Dictionary<string, HashSet<string>> showcaseAncestors = new();
    private readonly HashSet<string> validatedFavorites = new();
    private readonly Dictionary<string, (int Width, int Height)> showcaseSizes = new();
    private readonly HashSet<string> showcaseWarnings = new();

    private JsonObject ShowcaseProps(string tpl)
    {
        if (showcaseProperties.TryGetValue(tpl, out var props)) return props;
        props = JsonNode.Parse(json.Serialize(templates.Items[new MongoId(tpl)])!)?["_props"]?.AsObject() ?? new();
        showcaseProperties.Add(tpl, props);
        return props;
    }

    private bool IsShowcaseDogtag(string tpl) => ShowcaseProps(tpl).ContainsKey("DogTagQualities")
        || templates.Items[new MongoId(tpl)].Name?.Contains("dogtag", StringComparison.OrdinalIgnoreCase) == true;

    private List<DisplaySpace> ShowcaseSpaces(Career c, int area)
    {
        int level = AreaLevel(c, area);
        if (displaySpaces.TryGetValue((area, level), out var cached)) return cached;
        var result = new List<DisplaySpace>();
        var definition = hideout.Areas.FirstOrDefault(a => (int?)a.Type == area);
        if (level > 0 && definition?.Stages?.TryGetValue(level.ToString(), out var stage) == true
            && stage.Container is MongoId container && templates.Items.ContainsKey(container))
        {
            var props = ShowcaseProps(container.ToString());
            foreach (var slot in props["Slots"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                string name = slot["_name"]?.ToString() ?? "";
                if (name.Length == 0 || name.Contains("dogtag", StringComparison.OrdinalIgnoreCase)
                    || slot["_props"] is not JsonObject filters) continue;
                result.Add(new(name, filters, 0, 0));
            }
            foreach (var grid in props["Grids"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                if (grid["_props"] is not JsonObject filters) continue;
                int width = filters["cellsH"]?.GetValue<int>() ?? 0, height = filters["cellsV"]?.GetValue<int>() ?? 0;
                if (width is > 0 and <= 64 && height is > 0 and <= 64)
                    result.Add(new(grid["_name"]?.ToString() ?? "", filters, width, height));
            }
        }
        displaySpaces.Add((area, level), result);
        return result;
    }

    private bool ShowcaseBasedOn(string tpl, string ancestor)
    {
        if (!showcaseAncestors.TryGetValue(tpl, out var chain))
        {
            chain = new HashSet<string>();
            string current = tpl;
            while (IsId(current) && chain.Add(current) && templates.Items.TryGetValue(new MongoId(current), out var item))
                current = item.Parent.ToString();
            showcaseAncestors.Add(tpl, chain);
        }
        return chain.Contains(ancestor);
    }

    private bool DisplayAllows(DisplaySpace space, string tpl)
    {
        if (space.Props["filters"] is not JsonArray { Count: > 0 } filters) return false;
        return filters.OfType<JsonObject>().Any(f =>
            !(f["ExcludedFilter"]?.AsArray().Any(v => v is not null && ShowcaseBasedOn(tpl, v.ToString())) ?? false)
            && (f["Filter"]?.AsArray().Any(v => v is not null && ShowcaseBasedOn(tpl, v.ToString())) ?? false));
    }

    private bool ValidShowcaseTree(JsonArray tree)
    {
        if (tree.Count is < 1 or > 512 || tree[0] is not JsonObject first || !IsId(first["_id"]?.ToString())) return false;
        if (validatedFavorites.Contains(first["_id"]!.ToString())) return true;
        // Reuse the equipment graph validator by attaching the detached tree to a temporary root.
        var wrapper = EmptyEquipment();
        string parent = wrapper["Id"]!.ToString();
        foreach (var node in tree)
        {
            if (node is not JsonObject source) return false;
            var item = source.DeepClone().AsObject();
            if (source == first) item["parentId"] = parent;
            wrapper["Items"]!.AsArray().Add(item);
        }
        if (!ValidEquipment(wrapper)) return false;
        foreach (var item in tree.OfType<JsonObject>())
        {
            string tpl = item["_tpl"]!.ToString();
            if (IsShowcaseDogtag(tpl)) return false;
            foreach (var slot in ShowcaseProps(tpl)["Slots"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                if (slot["_required"]?.GetValue<bool>() != true) continue;
                string name = slot["_name"]?.ToString() ?? "";
                var child = tree.OfType<JsonObject>().FirstOrDefault(i => i["parentId"]?.ToString() == item["_id"]!.ToString()
                    && i["slotId"]?.ToString() == name);
                if (child is null || slot["_props"] is not JsonObject props
                    || !DisplayAllows(new(name, props, 0, 0), child["_tpl"]!.ToString())) return false;
            }
        }
        return true;
    }

    private bool FitsShowcase(Career c, int area, IEnumerable<JsonArray> trees)
    {
        try { return FitShowcaseTrees(c, area, trees); }
        catch (Exception ex)
        {
            if (showcaseWarnings.Add(c.Id + ":layout:" + area))
                logger.Warning($"[Harmony-Profiles] Showcase layout unavailable for account {c.AccountId}, area {area}; affected items hidden. {ex.GetBaseException().Message}");
            return false;
        }
    }

    private bool FitShowcaseTrees(Career c, int area, IEnumerable<JsonArray> trees)
    {
        var spaces = ShowcaseSpaces(c, area);
        var occupied = spaces.Select(s => new bool[Math.Max(1, s.Width), Math.Max(1, s.Height)]).ToArray();
        foreach (var tree in trees)
        {
            if (!ValidShowcaseTree(tree)) return false;
            string tpl = tree[0]!["_tpl"]!.ToString();
            string root = tree[0]!["_id"]!.ToString();
            if (!showcaseSizes.TryGetValue(root, out var size))
            {
                var items = json.Deserialize<List<Item>>(tree.ToJsonString())!;
                var nativeSize = itemHelper.GetItemSize(items, new MongoId(root));
                if (nativeSize is null || nativeSize.Width is < 1 or > 64 || nativeSize.Height is < 1 or > 64) return false;
                size = (nativeSize.Width, nativeSize.Height);
                if (validatedFavorites.Contains(root)) showcaseSizes[root] = size;
            }
            bool placed = false;
            for (int index = 0; index < spaces.Count && !placed; index++)
            {
                var space = spaces[index];
                if (!DisplayAllows(space, tpl)) continue;
                if (space.Width == 0)
                {
                    if (!occupied[index][0, 0]) { occupied[index][0, 0] = true; placed = true; }
                    continue;
                }
                for (int rotation = 0; rotation < 2 && !placed; rotation++)
                {
                    int width = rotation == 0 ? size.Width : size.Height, height = rotation == 0 ? size.Height : size.Width;
                    for (int y = 0; y + height <= space.Height && !placed; y++)
                        for (int x = 0; x + width <= space.Width && !placed; x++)
                        {
                            bool free = true;
                            for (int dy = 0; dy < height && free; dy++)
                                for (int dx = 0; dx < width; dx++)
                                    if (occupied[index][x + dx, y + dy]) { free = false; break; }
                            if (!free) continue;
                            for (int dy = 0; dy < height; dy++)
                                for (int dx = 0; dx < width; dx++) occupied[index][x + dx, y + dy] = true;
                            placed = true;
                        }
                }
            }
            if (!placed) return false;
        }
        return true;
    }

    private int ShowcaseTarget(Career c, int area) => area == 24
        ? AreaLevel(c, area) > 0 ? Math.Clamp(2 + Level(c.Experience) / 20, 2, 2 + AreaLevel(c, area)) : 0
        : Math.Min(ShowcaseSpaces(c, area).Count, 3 + Level(c.Experience) / 5 + (int)(Stable(c.Id + ":collection-size") * 4));

    private List<JsonArray> VisibleShowcaseTrees(Career c, int area)
    {
        var trees = new List<JsonArray>();
        if (AreaLevel(c, area) <= 0) return trees;
        foreach (var (root, type) in c.ShowcaseAreas)
        {
            if (type != area) continue;
            var tree = ItemTree(c.ShowcaseItems, root);
            if (FitsShowcase(c, area, trees.Append(tree))) trees.Add(tree);
        }
        return trees;
    }

    private bool PinShowcase(Career c, JsonArray source, string root, int area)
    {
        var tree = ItemTree(source, root);
        if (!ValidShowcaseTree(tree) || c.ShowcaseAreas.Count >= 128 || c.ShowcaseItems.Count + tree.Count > 4096) return false;
        string tpl = tree[0]!["_tpl"]!.ToString();
        if (c.ShowcaseAreas.Keys.Any(id => c.ShowcaseItems.OfType<JsonObject>()
            .Any(i => i["_id"]?.ToString() == id && i["_tpl"]?.ToString() == tpl))) return false;
        var current = VisibleShowcaseTrees(c, area);
        if (current.Count >= ShowcaseTarget(c, area) || !FitsShowcase(c, area, current.Append(tree))) return false;
        var ids = tree.OfType<JsonObject>().ToDictionary(i => i["_id"]!.ToString(), _ => NewId());
        foreach (var item in tree.OfType<JsonObject>())
        {
            string old = item["_id"]!.ToString();
            item["_id"] = ids[old];
            if (old == root) { item.Remove("parentId"); item.Remove("slotId"); item.Remove("location"); }
            else item["parentId"] = ids[item["parentId"]!.ToString()];
            c.ShowcaseItems.Add(item.DeepClone());
        }
        c.ShowcaseAreas.Add(ids[root], area);
        validatedFavorites.Add(ids[root]);
        dirty = true;
        return true;
    }

    private static IEnumerable<JsonObject> EquippedWeapons(JsonObject equipment) =>
        equipment["Items"]!.AsArray().OfType<JsonObject>().Where(i => i["parentId"]?.ToString() == equipment["Id"]!.ToString()
            && i["slotId"]?.ToString() is "FirstPrimaryWeapon" or "SecondPrimaryWeapon" or "Holster");

    // Rack guns come from a separately generated loadout, never one the PMC is carrying right now.
    private void PinGeneratedWeapons(Career c, JsonObject equipment)
    {
        if (AreaLevel(c, 24) <= 0) return;
        var carried = EquippedWeapons(c.Equipment).Select(i => i["_tpl"]!.ToString()).ToHashSet();
        var items = equipment["Items"]!.AsArray();
        foreach (var weapon in EquippedWeapons(equipment).Where(i => !carried.Contains(i["_tpl"]!.ToString())))
            PinShowcase(c, items, weapon["_id"]!.ToString(), 24);
    }

    private void EnsureShowcase(Career c)
    {
        if (!policy.EnableShowcase) return;
        try { DevelopShowcase(c); }
        catch (Exception ex)
        {
            if (showcaseWarnings.Add(c.Id + ":collection"))
                logger.Warning($"[Harmony-Profiles] Showcase update deferred for account {c.AccountId}; existing favorites retained. {ex.GetBaseException().Message}");
        }
    }

    private void DevelopShowcase(Career c)
    {
        if (AreaLevel(c, 16) <= 0 || VisibleShowcaseTrees(c, 16).Count >= ShowcaseTarget(c, 16)) return;
        showcasePrices ??= templates.Handbook.Items.Where(i => i.Price is > 0)
            .GroupBy(i => i.Id.ToString()).ToDictionary(g => g.Key, g => g.First().Price!.Value);
        double budget = Math.Clamp(25000 + Math.Pow(Level(c.Experience), 2) * 800 + TotalSeconds(c) / 36, 25000, 10000000);
        var used = c.ShowcaseAreas.Keys.Select(id => c.ShowcaseItems.OfType<JsonObject>()
            .FirstOrDefault(i => i["_id"]?.ToString() == id)?["_tpl"]?.ToString()).ToHashSet();
        int level = AreaLevel(c, 16);
        if (!trophyCatalogs.TryGetValue(level, out var catalog))
        {
            var allowed = ShowcaseSpaces(c, 16);
            catalog = showcasePrices.Where(p => p.Value >= 1000 && templates.Items.ContainsKey(new MongoId(p.Key))
                && allowed.Any(s => DisplayAllows(s, p.Key)) && !IsShowcaseDogtag(p.Key)
                && ShowcaseProps(p.Key)["QuestItem"]?.GetValue<bool>() != true).ToList();
            trophyCatalogs.Add(level, catalog);
        }
        foreach (var entry in catalog.Where(p => p.Value <= budget && !used.Contains(p.Key))
            .OrderBy(p => -Math.Log(Math.Max(.000001, Stable(c.Id + ":favorite:" + p.Key)))
                / (1 + Math.Log10(Math.Max(1, p.Value)) + (ShowcaseBasedOn(p.Key, "543be5e94bdc2df1348b4568") ? 2 : 0))))
        {
            var tree = CreateShowcaseCollectible(entry.Key);
            if (tree is not null) PinShowcase(c, tree, tree[0]!["_id"]!.ToString(), 16);
            if (VisibleShowcaseTrees(c, 16).Count >= ShowcaseTarget(c, 16)) break;
        }
    }

    private JsonArray? CreateShowcaseCollectible(string tpl)
    {
        var id = new MongoId(tpl);
        if (presetHelper.HasPreset(id))
        {
            var preset = presetHelper.GetDefaultPreset(id);
            if (preset?.Items is { Count: > 0 })
            {
                var source = JsonNode.Parse(json.Serialize(preset.Items)!)!.AsArray();
                var root = source.OfType<JsonObject>().FirstOrDefault(i => i["_tpl"]?.ToString() == tpl
                    && !source.OfType<JsonObject>().Any(p => p["_id"]?.ToString() == i["parentId"]?.ToString()));
                if (root is not null)
                {
                    var tree = ItemTree(source, root["_id"]!.ToString());
                    if (ValidShowcaseTree(tree)) return tree;
                }
            }
        }
        var upd = itemHelper.GenerateUpdForItem(templates.Items[id]) ?? new Upd();
        upd.StackObjectsCount = 1;
        upd.SpawnedInSession = false;
        var item = new Item { Id = new MongoId(), Template = id, Upd = upd };
        var single = new JsonArray(JsonNode.Parse(json.Serialize(item)!));
        return ValidShowcaseTree(single) ? single : null;
    }

    private JsonArray PublicShowcase(Career c)
    {
        if (!policy.EnableShowcase) return new();
        return new JsonArray(new[] { 24, 16 }.SelectMany(area => VisibleShowcaseTrees(c, area))
            .SelectMany(tree => tree.Select(item => item!.DeepClone())).ToArray());
    }

    private void RepairShowcase(Career c)
    {
        var retained = new JsonArray();
        var roots = new Dictionary<string, int>();
        var ids = new HashSet<string>();
        var carried = EquippedWeapons(c.Equipment).Select(i => i["_tpl"]!.ToString()).ToHashSet();
        foreach (var (root, area) in c.ShowcaseAreas)
        {
            JsonArray tree;
            try
            {
                tree = ItemTree(c.ShowcaseItems, root);
                if (!ValidShowcaseTree(tree) || tree.OfType<JsonObject>().Any(i => ids.Contains(i["_id"]!.ToString()))
                    || (area == 24 && carried.Contains(tree[0]!["_tpl"]!.ToString()))) continue;
            }
            catch { continue; }
            roots.Add(root, area);
            validatedFavorites.Add(root);
            foreach (var item in tree.OfType<JsonObject>())
            {
                if (item["_id"]!.ToString() == root) { item.Remove("parentId"); item.Remove("slotId"); item.Remove("location"); }
                ids.Add(item["_id"]!.ToString());
                retained.Add(item.DeepClone());
            }
        }
        if (c.ShowcaseItems.ToJsonString() != retained.ToJsonString() || c.ShowcaseAreas.Count != roots.Count)
        {
            dirty = true;
            if (c.ShowcaseAreas.Count > roots.Count)
                logger.Info($"[Harmony-Profiles] Removed {c.ShowcaseAreas.Count - roots.Count} invalid, dogtag or carried-gun showcase trees for account {c.AccountId}; career retained.");
        }
        c.ShowcaseItems = retained;
        c.ShowcaseAreas = roots;
    }

    private void QueueShowcase(Career c)
    {
        if (!policy.EnableShowcase || c.Retired || AreaLevel(c, 24) <= 0 || showcaseQueue.Count >= 256
            || c.ShowcaseAreas.Count(p => p.Value == 24) >= ShowcaseTarget(c, 24)) return;
        showcaseQueue.TryAdd(c.Id, (0, 0));
    }

    private bool ProcessPendingShowcase(long now)
    {
        if (!policy.EnableShowcase || gearSession is not MongoId session || gearFactory is null) return false;
        foreach (var (id, work) in showcaseQueue.OrderBy(p => p.Value.Due).ToArray())
        {
            if (!population.Careers.TryGetValue(id, out var c) || c.Retired || AreaLevel(c, 24) <= 0
                || c.ShowcaseAreas.Count(p => p.Value == 24) >= ShowcaseTarget(c, 24))
            { showcaseQueue.Remove(id); continue; }
            if (work.Due > now || IsObservedActive(c, now) || now < c.StoryHoldUntilUtc || now - c.LastEquipmentGeneratedUtc < 900) continue;
            try { PinGeneratedWeapons(c, GenerateIsolatedEquipment(c, session)); }
            catch (Exception ex)
            {
                if (work.Attempts == 0) logger.Warning($"[Harmony-Profiles] Showcase APBS generation deferred for account {c.AccountId}; pinned favorites retained. {ex.GetBaseException().Message}");
            }
            // Repeated unsupported/duplicate rolls back off without replacing existing favorites.
            showcaseQueue[id] = (now + (work.Attempts < 7 ? 60 : 3600), Math.Min(8, work.Attempts + 1));
            return true;
        }
        return false;
    }

    private void ProcessProfileEquipment(long now)
    {
        ProcessPendingGear(now);
        ProcessPendingShowcase(now);
    }
}
