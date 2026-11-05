using System.Text.Json.Nodes;

namespace PBT.Server
{
    public sealed partial class PopulationStore
    {
        private readonly Dictionary<(int Area, int Level), List<DisplaySpace>> displaySpaces = new Dictionary<(int Area, int Level), List<DisplaySpace>>();
        private readonly Dictionary<string, long> rackViews = new Dictionary<string, long>();
        private readonly Dictionary<string, HashSet<string>> showcaseAncestors = new Dictionary<string, HashSet<string>>();
        private readonly Dictionary<string, JsonObject> showcaseProperties = new Dictionary<string, JsonObject>();
        private readonly Dictionary<string, (long Due, int Attempts)> showcaseQueue = new Dictionary<string, (long Due, int Attempts)>();
        private readonly Dictionary<string, (int Width, int Height)> showcaseSizes = new Dictionary<string, (int Width, int Height)>();
        private readonly HashSet<string> showcaseWarnings = new HashSet<string>();
        private readonly Dictionary<int, List<KeyValuePair<string, double>>> trophyCatalogs = new Dictionary<int, List<KeyValuePair<string, double>>>();
        private readonly HashSet<string> validatedFavorites = new HashSet<string>();
        private Dictionary<string, double>? handbookPrices;

        private Dictionary<string, double> HandbookPrices()
        {
            return handbookPrices ??= templates.Handbook.Items.Where(i => i.Price is > 0).GroupBy(i => i.Id.ToString()).ToDictionary(g => g.Key, g => g.First().Price!.Value);
        }

        private JsonObject ShowcaseProps(string tpl)
        {
            if (showcaseProperties.TryGetValue(tpl, out JsonObject? props)) return props;
            props = JsonNode.Parse(json.Serialize(templates.Items[new MongoId(tpl)])!)?["_props"]?.AsObject() ?? new JsonObject();
            showcaseProperties.Add(tpl, props);
            return props;
        }

        private bool IsShowcaseDogtag(string tpl)
        {
            return ShowcaseProps(tpl).ContainsKey("DogTagQualities") || templates.Items[new MongoId(tpl)].Name?.Contains("dogtag", StringComparison.OrdinalIgnoreCase) == true;
        }

        private List<DisplaySpace> ShowcaseSpaces(Career c, int area)
        {
            int level = AreaLevel(c, area);
            if (displaySpaces.TryGetValue((area, level), out List<DisplaySpace>? cached)) return cached;
            List<DisplaySpace> result = new List<DisplaySpace>();
            var definition = hideout.Areas.FirstOrDefault(a => (int?)a.Type == area);
            if (level > 0 && definition?.Stages?.TryGetValue(level.ToString(), out var stage) == true && stage.Container is MongoId container
                && templates.Items.ContainsKey(container))
            {
                JsonObject props = ShowcaseProps(container.ToString());
                foreach (JsonObject slot in props["Slots"]?.AsArray().OfType<JsonObject>() ?? [])
                {
                    string name = slot["_name"]?.ToString() ?? "";
                    if (name.Length == 0 || name.Contains("dogtag", StringComparison.OrdinalIgnoreCase) || slot["_props"] is not JsonObject filters) continue;
                    result.Add(new DisplaySpace(name, filters, 0, 0));
                }

                foreach (JsonObject grid in props["Grids"]?.AsArray().OfType<JsonObject>() ?? [])
                {
                    if (grid["_props"] is not JsonObject filters) continue;
                    int width = filters["cellsH"]?.GetValue<int>() ?? 0, height = filters["cellsV"]?.GetValue<int>() ?? 0;
                    if (width is > 0 and <= 64 && height is > 0 and <= 64)
                        result.Add(new DisplaySpace(grid["_name"]?.ToString() ?? "", filters, width, height));
                }
            }

            displaySpaces.Add((area, level), result);
            return result;
        }

        private bool ShowcaseBasedOn(string tpl, string ancestor)
        {
            if (!showcaseAncestors.TryGetValue(tpl, out HashSet<string>? chain))
            {
                chain = new HashSet<string>();
                string current = tpl;
                while (IsId(current) && chain.Add(current) && templates.Items.TryGetValue(new MongoId(current), out var item))
                {
                    current = item.Parent.ToString();
                }

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
            JsonObject wrapper = EmptyEquipment();
            string parent = wrapper["Id"]!.ToString();
            foreach (JsonNode? node in tree)
            {
                if (node is not JsonObject source) return false;
                JsonObject item = source.DeepClone().AsObject();
                if (source == first) item["parentId"] = parent;
                wrapper["Items"]!.AsArray().Add(item);
            }

            if (!ValidEquipment(wrapper)) return false;
            foreach (JsonObject item in tree.OfType<JsonObject>())
            {
                string tpl = item["_tpl"]!.ToString();
                if (IsShowcaseDogtag(tpl)) return false;
                foreach (JsonObject slot in ShowcaseProps(tpl)["Slots"]?.AsArray().OfType<JsonObject>() ?? [])
                {
                    if (slot["_required"]?.GetValue<bool>() != true) continue;
                    string name = slot["_name"]?.ToString() ?? "";
                    JsonObject? child = tree.OfType<JsonObject>().FirstOrDefault(i => i["parentId"]?.ToString() == item["_id"]!.ToString() && i["slotId"]?.ToString() == name);
                    if (child is null || slot["_props"] is not JsonObject props || !DisplayAllows(new DisplaySpace(name, props, 0, 0), child["_tpl"]!.ToString())) return false;
                }
            }

            return true;
        }

        private bool FitsShowcase(Career c, int area, IEnumerable<JsonArray> trees)
        {
            try
            {
                return FitShowcaseTrees(c, area, trees);
            }
            catch (Exception ex)
            {
                if (showcaseWarnings.Add(c.Id + ":layout:" + area))
                    logger.Warning($"[PBT] Showcase layout unavailable for account {c.AccountId}, area {area}; affected items hidden. {ex.GetBaseException().Message}");
                return false;
            }
        }

        private bool FitShowcaseTrees(Career c, int area, IEnumerable<JsonArray> trees)
        {
            List<DisplaySpace> spaces = ShowcaseSpaces(c, area);
            bool[][,] occupied = spaces.Select(s => new bool[Math.Max(1, s.Width), Math.Max(1, s.Height)]).ToArray();
            foreach (JsonArray tree in trees)
            {
                if (!ValidShowcaseTree(tree)) return false;
                string tpl = tree[0]!["_tpl"]!.ToString();
                string root = tree[0]!["_id"]!.ToString();
                if (!showcaseSizes.TryGetValue(root, out (int Width, int Height) size))
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
                    DisplaySpace space = spaces[index];
                    if (!DisplayAllows(space, tpl)) continue;
                    if (space.Width == 0)
                    {
                        if (!occupied[index][0, 0])
                        {
                            occupied[index][0, 0] = true;
                            placed = true;
                        }

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
                            {
                                if (occupied[index][x + dx, y + dy])
                                {
                                    free = false;
                                    break;
                                }
                            }

                            if (!free) continue;
                            for (int dy = 0; dy < height; dy++)
                            for (int dx = 0; dx < width; dx++)
                            {
                                occupied[index][x + dx, y + dy] = true;
                            }

                            placed = true;
                        }
                    }
                }

                if (!placed) return false;
            }

            return true;
        }

        private int ShowcaseTarget(Career c, int area)
        {
            return area == 24
                ? AreaLevel(c, area) > 0 ? Math.Clamp(2 + Level(c.Experience) / 20, 2, 2 + AreaLevel(c, area)) : 0
                : Math.Min(ShowcaseSpaces(c, area).Count, 3 + Level(c.Experience) / 5 + (int)(Stable(c.Id + ":collection-size") * 4));
        }

        private List<JsonArray> VisibleShowcaseTrees(Career c, int area)
        {
            List<JsonArray> trees = new List<JsonArray>();
            if (AreaLevel(c, area) <= 0) return trees;
            JsonArray items = Assets(c).ShowcaseItems;
            foreach ((string root, int type) in c.ShowcaseAreas)
            {
                if (type != area) continue;
                JsonArray tree = ItemTree(items, root);
                if (FitsShowcase(c, area, trees.Append(tree))) trees.Add(tree);
            }

            return trees;
        }

        private bool PinShowcase(Career c, JsonArray source, string root, int area)
        {
            JsonArray tree = ItemTree(source, root);
            CareerAssets assets = Assets(c);
            if (!ValidShowcaseTree(tree) || c.ShowcaseAreas.Count >= 128 || assets.ShowcaseItems.Count + tree.Count > 4096) return false;
            string tpl = tree[0]!["_tpl"]!.ToString();
            if (c.ShowcaseAreas.Keys.Any(id => assets.ShowcaseItems.OfType<JsonObject>().Any(i => i["_id"]?.ToString() == id && i["_tpl"]?.ToString() == tpl))) return false;
            List<JsonArray> current = VisibleShowcaseTrees(c, area);
            if (current.Count >= ShowcaseTarget(c, area) || !FitsShowcase(c, area, current.Append(tree))) return false;
            Dictionary<string, string> ids = tree.OfType<JsonObject>().ToDictionary(i => i["_id"]!.ToString(), _ => NewId());
            foreach (JsonObject item in tree.OfType<JsonObject>())
            {
                string old = item["_id"]!.ToString();
                item["_id"] = ids[old];
                if (old == root)
                {
                    item.Remove("parentId");
                    item.Remove("slotId");
                    item.Remove("location");
                }
                else item["parentId"] = ids[item["parentId"]!.ToString()];

                assets.ShowcaseItems.Add(item.DeepClone());
            }

            assets.Changed();
            c.ShowcaseAreas.Add(ids[root], area);
            validatedFavorites.Add(ids[root]);
            dirty = true;
            return true;
        }

        private static IEnumerable<JsonObject> EquippedWeapons(JsonObject equipment)
        {
            return equipment["Items"]!.AsArray().OfType<JsonObject>().Where(i => i["parentId"]?.ToString() == equipment["Id"]!.ToString()
                                                                                 && i["slotId"]?.ToString() is "FirstPrimaryWeapon" or "SecondPrimaryWeapon" or "Holster");
        }

        // Rack guns come from a separately generated loadout, never one the PMC is carrying right now.
        private void PinGeneratedWeapons(Career c, JsonObject equipment)
        {
            if (AreaLevel(c, 24) <= 0) return;
            HashSet<string> carried = EquippedWeapons(CareerEquipment(c)).Select(i => i["_tpl"]!.ToString()).ToHashSet();
            JsonArray items = equipment["Items"]!.AsArray();
            foreach (JsonObject weapon in EquippedWeapons(equipment).Where(i => !carried.Contains(i["_tpl"]!.ToString())))
            {
                PinShowcase(c, items, weapon["_id"]!.ToString(), 24);
            }
        }

        private void EnsureShowcase(Career c)
        {
            if (!policy.EnableShowcase) return;
            try
            {
                DevelopShowcase(c);
            }
            catch (Exception ex)
            {
                if (showcaseWarnings.Add(c.Id + ":collection"))
                    logger.Warning($"[PBT] Showcase update deferred for account {c.AccountId}; existing favorites retained. {ex.GetBaseException().Message}");
            }
        }

        private void DevelopShowcase(Career c)
        {
            // The pinned count is checked first so a full collection never loads the career file.
            if (AreaLevel(c, 16) <= 0 || c.ShowcaseAreas.Count(p => p.Value == 16) >= ShowcaseTarget(c, 16) || VisibleShowcaseTrees(c, 16).Count >= ShowcaseTarget(c, 16)) return;
            Dictionary<string, double> showcasePrices = HandbookPrices();
            double budget = Math.Clamp(25000 + Math.Pow(Level(c.Experience), 2) * 800 + TotalSeconds(c) / 36, 25000, 10000000);
            HashSet<string?> used = c.ShowcaseAreas.Keys
                .Select(id => Assets(c).ShowcaseItems.OfType<JsonObject>().FirstOrDefault(i => i["_id"]?.ToString() == id)?["_tpl"]?.ToString()).ToHashSet();
            int level = AreaLevel(c, 16);
            if (!trophyCatalogs.TryGetValue(level, out List<KeyValuePair<string, double>>? catalog))
            {
                List<DisplaySpace> allowed = ShowcaseSpaces(c, 16);
                catalog = showcasePrices.Where(p => p.Value >= 1000 && templates.Items.ContainsKey(new MongoId(p.Key))
                                                                    && allowed.Any(s => DisplayAllows(s, p.Key)) && !IsShowcaseDogtag(p.Key)
                                                                    && ShowcaseProps(p.Key)["QuestItem"]?.GetValue<bool>() != true).ToList();
                trophyCatalogs.Add(level, catalog);
            }

            foreach (KeyValuePair<string, double> entry in catalog.Where(p => p.Value <= budget && !used.Contains(p.Key)).OrderBy(p =>
                         -Math.Log(Math.Max(.000001, Stable(c.Id + ":favorite:" + p.Key)))
                         / (1 + Math.Log10(Math.Max(1, p.Value)) + (ShowcaseBasedOn(p.Key, "543be5e94bdc2df1348b4568") ? 2 : 0))))
            {
                JsonArray? tree = CreateShowcaseCollectible(entry.Key);
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
                    JsonArray source = JsonNode.Parse(json.Serialize(preset.Items)!)!.AsArray();
                    JsonObject? root = source.OfType<JsonObject>().FirstOrDefault(i => i["_tpl"]?.ToString() == tpl
                                                                                       && !source.OfType<JsonObject>().Any(p => p["_id"]?.ToString() == i["parentId"]?.ToString()));
                    if (root is not null)
                    {
                        JsonArray tree = ItemTree(source, root["_id"]!.ToString());
                        if (ValidShowcaseTree(tree)) return tree;
                    }
                }
            }

            var upd = itemHelper.GenerateUpdForItem(templates.Items[id]) ?? new Upd();
            upd.StackObjectsCount = 1;
            upd.SpawnedInSession = false;
            var item = new Item
            {
                Id = new MongoId(),
                Template = id,
                Upd = upd
            };
            JsonArray single = new JsonArray(JsonNode.Parse(json.Serialize(item)!));
            return ValidShowcaseTree(single) ? single : null;
        }

        private JsonArray PublicShowcase(Career c)
        {
            if (!policy.EnableShowcase) return new JsonArray();
            return new JsonArray(new[] { 24, 16 }.SelectMany(area => VisibleShowcaseTrees(c, area)).SelectMany(tree => tree.Select(item => item!.DeepClone())).ToArray());
        }

        private void RepairShowcase(Career c)
        {
            JsonArray retained = new JsonArray();
            Dictionary<string, int> roots = new Dictionary<string, int>();
            HashSet<string> ids = new HashSet<string>();
            CareerAssets assets = Assets(c);
            HashSet<string> carried = EquippedWeapons(assets.Equipment).Select(i => i["_tpl"]!.ToString()).ToHashSet();
            foreach ((string root, int area) in c.ShowcaseAreas)
            {
                JsonArray tree;
                try
                {
                    tree = ItemTree(assets.ShowcaseItems, root);
                    if (!ValidShowcaseTree(tree) || tree.OfType<JsonObject>().Any(i => ids.Contains(i["_id"]!.ToString()))
                                                 || area == 24 && carried.Contains(tree[0]!["_tpl"]!.ToString())) continue;
                }
                catch
                {
                    continue;
                }

                roots.Add(root, area);
                validatedFavorites.Add(root);
                foreach (JsonObject item in tree.OfType<JsonObject>())
                {
                    if (item["_id"]!.ToString() == root)
                    {
                        item.Remove("parentId");
                        item.Remove("slotId");
                        item.Remove("location");
                    }

                    ids.Add(item["_id"]!.ToString());
                    retained.Add(item.DeepClone());
                }
            }

            if (assets.ShowcaseItems.ToJsonString() == retained.ToJsonString() && c.ShowcaseAreas.Count == roots.Count) return;
            dirty = true;
            assets.Changed();
            if (c.ShowcaseAreas.Count > roots.Count)
                logger.Debug($"[PBT] Removed {c.ShowcaseAreas.Count - roots.Count} invalid, dogtag or carried-gun showcase trees for account {c.AccountId}; career retained.");
            assets.ShowcaseItems = retained;
            c.ShowcaseAreas = roots;
        }

        private int RackTiers()
        {
            return Random.Shared.Next(policy.RackTierBoostMinimum, policy.RackTierBoostMaximum + 1);
        }

        private bool RackFull(Career c)
        {
            return c.ShowcaseAreas.Count(p => p.Value == 24) >= ShowcaseTarget(c, 24);
        }

        // The checkpoint queue is too slow for viewed racks, so fill them on view.
        private void ShowcaseForView(int accountId, MongoId session)
        {
            Career? career;
            lock (gate)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (!Ready || !policy.EnableShowcase || gearFactory is null) return;
                if (!byAccount.TryGetValue(accountId, out career) && !(encounters.TryGetValue(accountId, out Encounter? encounter) && CanAccess(encounter, session)
                        && population.Careers.TryGetValue(encounter.CareerId, out career))) return;
                if (career is null || career.Retired || AreaLevel(career, 24) <= 0 || IsObservedActive(career, now) || RackFull(career)
                    || rackViews.TryGetValue(career.Id, out long next) && now < next) return;
                rackViews[career.Id] = now + 300;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                JsonObject loadout;
                try
                {
                    loadout = GenerateIsolatedEquipment(career, session, RackTiers());
                }
                catch (Exception ex)
                {
                    lock (gate)
                    {
                        if (showcaseWarnings.Add(career.Id + ":rack"))
                            logger.Warning($"[PBT] Weapon rack generation deferred for account {career.AccountId}. {ex.GetBaseException().Message}");
                    }

                    return;
                }

                lock (gate)
                {
                    if (!Ready) return;
                    PinGeneratedWeapons(career, loadout);
                    if (RackFull(career)) return;
                }
            }
        }

        private void QueueShowcase(Career c)
        {
            if (!policy.EnableShowcase || c.Retired || AreaLevel(c, 24) <= 0 || showcaseQueue.Count >= 256
                || c.ShowcaseAreas.Count(p => p.Value == 24) >= ShowcaseTarget(c, 24)) return;
            showcaseQueue.TryAdd(c.Id, (0, 0));
        }

        private bool ProcessPendingShowcase(long now)
        {
            if (!policy.EnableShowcase || GearSession() is not MongoId session || gearFactory is null) return false;
            foreach ((string id, (long Due, int Attempts) work) in showcaseQueue.OrderBy(p => p.Value.Due).ToArray())
            {
                if (!population.Careers.TryGetValue(id, out Career? c) || c.Retired || AreaLevel(c, 24) <= 0 || c.ShowcaseAreas.Count(p => p.Value == 24) >= ShowcaseTarget(c, 24))
                {
                    showcaseQueue.Remove(id);
                    continue;
                }

                if (work.Due > now || IsObservedActive(c, now) || now < c.StoryHoldUntilUtc || now - c.LastEquipmentGeneratedUtc < 900) continue;
                try
                {
                    PinGeneratedWeapons(c, GenerateIsolatedEquipment(c, session, RackTiers()));
                }
                catch (Exception ex)
                {
                    if (work.Attempts == 0)
                        logger.Warning($"[PBT] Showcase APBS generation deferred for account {c.AccountId}; pinned favorites retained. {ex.GetBaseException().Message}");
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

        private sealed record DisplaySpace(string Name, JsonObject Props, int Width, int Height);
    }
}
