using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PBT.Server
{
    public sealed class CheatReportRequest : IRequestData
    {
        [JsonPropertyName("uid")] public string? Uid { get; set; }

        // The client sends EReportType, as "cheat" or as its flag value 2.
        [JsonPropertyName("type")] public JsonElement Type { get; set; }
    }

    public sealed partial class PopulationStore
    {
        private const string FaceCoverParent = "5a341c4686f77469e155819e";
        private static readonly string[] KeptOnDeath = ["SecuredContainer", "Dogtag", "ArmBand", "Pockets"];

        public bool CheatReports => Ready && policy.Cheaters.Enabled;

        private bool IsCheater(Career c)
        {
            return policy.Cheaters.Enabled && c.Cheater && c.BannedUtc == 0;
        }

        private double Survival(Career c)
        {
            return IsCheater(c) ? policy.Cheaters.Survival : c.SurvivalChance * (Tilted(c) ? .8 : 1);
        }

        private void ProcessCheaters(long now)
        {
            CheaterSettings s = policy.Cheaters;
            if (!s.Enabled) return;
            ResolveCheaterReports(now);
            List<Career> active = population.Careers.Values.Where(c => !c.Retired).ToList();
            int target = s.Share <= 0 ? 0 : Math.Max(1, (int)Math.Round(active.Count * s.Share));
            int have = active.Count(IsCheater);
            if (have >= target) return;
            // A fresh account that is suddenly a god, so young careers first.
            List<Career> pick = active.Where(c => !c.Cheater && c.BannedUtc == 0 && !Notable(c) && Level(c.Experience) <= 35).OrderBy(_ => Random.Shared.Next())
                .Take(Math.Min(target - have, 3)).ToList();
            foreach (Career c in pick)
            {
                MakeCheater(c);
            }

            if (pick.Count > 0) dirty = true;
        }

        private void MakeCheater(Career c)
        {
            c.Cheater = true;
            // Existing history gets the cheater look right away instead of only after a few hundred raids.
            int raids = Math.Max(c.Raids, Random.Shared.Next(60, 260));
            int lost = c.MissingRaids + c.AwolRaids + c.RunThroughRaids;
            c.Raids = Math.Max(raids, lost + 1);
            c.Survived = Math.Max(c.Survived, Math.Min(c.Raids - lost, (int)(c.Raids * (.85 + Random.Shared.NextDouble() * .1))));
            c.PmcKills = Math.Max(c.PmcKills, (int)(c.Raids * (2.5 + Random.Shared.NextDouble() * 4)));
            c.ScavKills = Math.Max(c.ScavKills, (int)(c.Raids * (1 + Random.Shared.NextDouble() * 2)));
            c.CurrentSurvivalStreak = Math.Max(c.CurrentSurvivalStreak, Random.Shared.Next(15, 60));
            c.BestSurvivalStreak = Math.Max(c.BestSurvivalStreak, c.CurrentSurvivalStreak);
            c.GearPending = true;
        }

        // What the player died with, minus what death never takes and what insurance already returns.
        public void RecordRaidLoss(EndLocalRaidRequestData request, MongoId session)
        {
            try
            {
                var result = request?.Results;
                if (result?.Result != ExitStatus.KILLED || !int.TryParse(result.KillerAid, out int killer)
                                                        || result.Profile?.Inventory is not { Items: { } items, Equipment: { } equipment }) return;
                lock (gate)
                {
                    if (!Ready || !policy.Cheaters.Enabled || !encounters.TryGetValue(killer, out Encounter? e) || !CanAccess(e, session)) return;
                    HashSet<string> insured = new HashSet<string>((request!.LostInsuredItems ?? []).Select(i => i.Id.ToString()));
                    JsonArray all = ToNode(items).AsArray();
                    string root = equipment.ToString();
                    HashSet<string> kept = new HashSet<string>();
                    foreach (JsonObject top in all.OfType<JsonObject>().Where(i => i["parentId"]?.ToString() == root && KeptOnDeath.Contains(i["slotId"]?.ToString())))
                    {
                        // Pocket contents are lost, the pockets themselves are not.
                        if (top["slotId"]!.ToString() == "Pockets")
                        {
                            kept.Add(top["_id"]!.ToString());
                            continue;
                        }

                        foreach (JsonNode? node in ItemTree(all, top["_id"]!.ToString()))
                        {
                            kept.Add(node!["_id"]!.ToString());
                        }
                    }

                    JsonArray lostItems = new JsonArray();
                    foreach (JsonObject top in all.OfType<JsonObject>().Where(i => i["parentId"]?.ToString() == root))
                    foreach (JsonNode? node in ItemTree(all, top["_id"]!.ToString()))
                    {
                        string id = node!["_id"]!.ToString();
                        if (!kept.Contains(id) && !insured.Contains(id) && lostItems.Count < 400) lostItems.Add(node.DeepClone());
                    }

                    population.RaidLosses[session.ToString()] = new RaidLoss
                    {
                        KillerAccountId = killer,
                        Utc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Items = lostItems
                    };
                    dirty = true;
                }
            }
            catch (Exception ex)
            {
                Report(ex);
            }
        }

        public void ReportCheater(CheatReportRequest request, MongoId session)
        {
            bool cheat = request.Type.ValueKind switch
            {
                JsonValueKind.String => string.Equals(request.Type.GetString(), "cheat", StringComparison.OrdinalIgnoreCase),
                JsonValueKind.Number => request.Type.TryGetInt32(out int flags) && (flags & 2) != 0,
                _ => false
            };
            if (!cheat || !int.TryParse(request.Uid, out int account)) return;
            lock (gate)
            {
                if (!Ready || !policy.Cheaters.Enabled || !encounters.TryGetValue(account, out Encounter? e) || !CanAccess(e, session)
                    || !population.Careers.TryGetValue(e.CareerId, out Career? career)) return;
                // Already banned, or every new report pays again.
                if (career.BannedUtc > 0) return;
                string sid = session.ToString();
                if (population.CheaterReports.Values.Any(r => r.SessionId == sid && r.CareerId == career.Id)) return;
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                CheaterSettings s = policy.Cheaters;
                CheaterReport report = new CheaterReport
                {
                    CareerId = career.Id,
                    SessionId = sid,
                    ReportedUtc = now,
                    DueUtc = now + (long)((s.BanMinimumHours + Random.Shared.NextDouble() * (s.BanMaximumHours - s.BanMinimumHours)) * 3600)
                };
                if (population.RaidLosses.TryGetValue(sid, out RaidLoss? loss) && loss.KillerAccountId == account && now - loss.Utc < 6 * 3600)
                {
                    report.LostGear = loss.Items;
                    population.RaidLosses.Remove(sid);
                }

                population.CheaterReports[Guid.NewGuid().ToString("N")] = report;
                dirty = true;
            }
        }

        private void ResolveCheaterReports(long now)
        {
            foreach ((string id, CheaterReport report) in population.CheaterReports.Where(p => now >= p.Value.DueUtc).ToArray())
            {
                population.CheaterReports.Remove(id);
                dirty = true;
                if (!population.Careers.TryGetValue(report.CareerId, out Career? c) || !c.Cheater) continue;
                if (c.BannedUtc == 0)
                {
                    if (Random.Shared.NextDouble() >= policy.Cheaters.BanChance) continue;
                    c.BannedUtc = now;
                    c.Retired = true;
                    c.IsOnline = false;
                    // The world tick skips retired careers, so a raid left running would count on its map forever.
                    if (activities.TryGetValue(c.Id, out Activity? a)) Set(a, "Offline", "", now, 0);
                    if (bots.Types.TryGetValue(c.Side.ToLowerInvariant(), out var type))
                        type?.FirstNames?.RemoveAll(n => NameKey(c.Side, n) == NameKey(c.Side, c.SourceName));
                    Log("ban", $"{c.Name} was banned for cheating");
                }

                try
                {
                    SendBanReward(report);
                }
                catch (Exception ex)
                {
                    Report(ex);
                }
            }
        }

        private void SendBanReward(CheaterReport report)
        {
            CheaterSettings s = policy.Cheaters;
            var items = new List<Item>();
            if (s.ReturnLostGear && report.LostGear.Count > 0)
                items.AddRange((json.Deserialize<List<Item>>(report.LostGear.ToJsonString()) ?? []).ReplaceIDs());
            if (s.RewardRubles > 0)
                items.AddRange(itemHelper.SplitStack(new Item
                {
                    Id = new MongoId(),
                    Template = new MongoId("5449016a4bdc2d6f028b456f"),
                    Upd = new Upd
                    {
                        StackObjectsCount = s.RewardRubles
                    }
                }));
            string text = "Thank you for your report. A player you reported has been banned for cheating." + (items.Count > 0 ? " Please accept this compensation." : "");
            mail.SendSystemMessageToPlayer(new MongoId(report.SessionId), text, items, 7 * 86400L);
        }

        // A swap after APBS picked the kit, so the rest of the loadout stays a normal roll.
        private void ApplyCheaterHelmet(JsonObject equipment)
        {
            List<CheaterHelmet> pool = policy.Cheaters.Helmets.Where(h => h.Weight > 0 && templates.Items.ContainsKey(new MongoId(h.Tpl))).ToList();
            if (pool.Count == 0) return;
            int roll = Random.Shared.Next(pool.Sum(h => h.Weight));
            CheaterHelmet pick = pool.First(h => (roll -= h.Weight) < 0);
            var template = templates.Items[new MongoId(pick.Tpl)];
            string slot = template.Parent.ToString() == FaceCoverParent ? "FaceCover" : "Headwear";
            if (CreateShowcaseCollectible(pick.Tpl) is not { } tree) return;
            string root = equipment["Id"]!.ToString();
            JsonArray items = equipment["Items"]!.AsArray();
            var props = template.Properties;
            HashSet<string> clear = new HashSet<string>
            {
                slot
            };
            if (props?.BlocksEyewear == true) clear.Add("Eyewear");
            if (props?.BlocksFaceCover == true) clear.Add("FaceCover");
            if (props?.BlocksEarpiece == true) clear.Add("Earpiece");
            if (props?.BlocksHeadwear == true) clear.Add("Headwear");
            HashSet<string> added = tree.OfType<JsonObject>().Select(i => i["_tpl"]!.ToString()).ToHashSet();
            foreach (JsonObject top in items.OfType<JsonObject>().Where(i => i["parentId"]?.ToString() == root).ToArray())
            {
                string? topSlot = top["slotId"]?.ToString();
                if (topSlot is null || clear.Contains(topSlot) || topSlot is not ("Headwear" or "FaceCover" or "Eyewear" or "Earpiece")) continue;
                // Helmets list what they cannot be worn with, either way round.
                HashSet<string> subtree = ItemTree(items, top["_id"]!.ToString()).Select(n => n!["_tpl"]!.ToString()).ToHashSet();
                if (Conflicts(added, subtree) || Conflicts(subtree, added)) clear.Add(topSlot);
            }

            foreach (JsonObject top in items.OfType<JsonObject>().Where(i => i["parentId"]?.ToString() == root && clear.Contains(i["slotId"]?.ToString() ?? "")).ToArray())
            {
                HashSet<string> remove = ItemTree(items, top["_id"]!.ToString()).Select(n => n!["_id"]!.ToString()).ToHashSet();
                foreach (JsonObject node in items.OfType<JsonObject>().Where(i => remove.Contains(i["_id"]!.ToString())).ToArray())
                {
                    items.Remove(node);
                }
            }

            JsonObject first = (JsonObject)tree[0]!;
            first["parentId"] = root;
            first["slotId"] = slot;
            first.Remove("location");
            foreach (JsonNode? node in tree)
            {
                items.Add(node!.DeepClone());
            }
        }

        private bool Conflicts(HashSet<string> from, HashSet<string> against)
        {
            return from.Any(
                tpl => templates.Items.TryGetValue(new MongoId(tpl), out var t) && t.Properties?.ConflictingItems is { } c && c.Any(x => against.Contains(x.ToString())));
        }
    }
}
