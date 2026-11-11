using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Utils;

namespace PBT.Server;

public sealed class CheatReportRequest : IRequestData
{
    [JsonPropertyName("uid")]
    public string? Uid { get; set; }

    // The client sends EReportType, as "cheat" or as its flag value 2.
    [JsonPropertyName("type")]
    public JsonElement Type { get; set; }
}

public sealed class CheaterBanRequest : IRequestData
{
    public int AccountId { get; set; }
}

public sealed partial class PopulationStore
{
    private const string FaceCoverParent = "5a341c4686f77469e155819e";
    private static readonly string[] KeptOnDeath = ["SecuredContainer", "Dogtag", "ArmBand", "Pockets"];

    public bool CheatReports => Ready && policy.Cheaters.Enabled;

    private bool IsCheater(Career c) => policy.Cheaters.Enabled && c.Cheater && c.BannedUtc == 0;

    private double Survival(Career c) => Blatant(c) ? policy.Cheaters.Survival : c.SurvivalChance * (Tilted(c) ? .8 : 1);

    private void ProcessCheaters(long now)
    {
        var s = policy.Cheaters;
        if (!s.Enabled)
            return;
        ResolveCheaterReports(now);
        var active = population.Careers.Values.Where(c => !c.Retired).ToList();
        int target = s.Share <= 0 ? 0 : Math.Max(1, (int)Math.Round(active.Count * s.Share));
        int have = active.Count(IsCheater);
        if (have >= target)
            return;
        // A fresh account that is suddenly a god, so young careers first.
        var pick = active
            .Where(c => !c.Cheater && c.BannedUtc == 0 && !Notable(c) && Level(c.Experience) <= 35)
            .OrderBy(_ => Random.Shared.Next())
            .Take(Math.Min(target - have, 3))
            .ToList();
        foreach (var c in pick)
            MakeCheater(c);
        if (pick.Count > 0)
            dirty = true;
    }

    private void MakeCheater(Career c)
    {
        c.Cheater = true;
        c.GearPending = true;
        if (Closet(c))
        {
            // A strong but believable record, the kind a good player also has.
            int done = c.MissingRaids + c.AwolRaids + c.RunThroughRaids;
            c.Raids = Math.Max(c.Raids, Math.Max(done + 1, Random.Shared.Next(40, 160)));
            c.Survived = Math.Max(c.Survived, Math.Min(c.Raids - done, (int)(c.Raids * (.6 + Random.Shared.NextDouble() * .15))));
            c.PmcKills = Math.Max(c.PmcKills, (int)(c.Raids * (1.2 + Random.Shared.NextDouble() * 1.3)));
            return;
        }

        if (!Blatant(c))
            return;
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
            if (
                result?.Result != ExitStatus.KILLED
                || !int.TryParse(result.KillerAid, out int killer)
                || result.Profile?.Inventory is not { Items: { } items, Equipment: { } equipment }
            )
                return;
            lock (gate)
            {
                if (!Ready || !policy.Cheaters.Enabled || !encounters.TryGetValue(killer, out var e) || !CanAccess(e, session))
                    return;
                var insured = new HashSet<string>((request!.LostInsuredItems ?? []).Select(i => i.Id.ToString()));
                var all = ToNode(items).AsArray();
                string root = equipment.ToString();
                var kept = new HashSet<string>();
                foreach (
                    var top in all.OfType<JsonObject>()
                        .Where(i => i["parentId"]?.ToString() == root && KeptOnDeath.Contains(i["slotId"]?.ToString()))
                )
                {
                    // Pocket contents are lost, the pockets themselves are not.
                    if (top["slotId"]!.ToString() == "Pockets")
                    {
                        kept.Add(top["_id"]!.ToString());
                        continue;
                    }

                    foreach (var node in ItemTree(all, top["_id"]!.ToString()))
                        kept.Add(node!["_id"]!.ToString());
                }

                var lostItems = new JsonArray();
                foreach (var top in all.OfType<JsonObject>().Where(i => i["parentId"]?.ToString() == root))
                foreach (var node in ItemTree(all, top["_id"]!.ToString()))
                {
                    string id = node!["_id"]!.ToString();
                    if (!kept.Contains(id) && !insured.Contains(id) && lostItems.Count < 400)
                        lostItems.Add(node.DeepClone());
                }

                population.RaidLosses[session.ToString()] = new RaidLoss
                {
                    KillerAccountId = killer,
                    Utc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Items = lostItems,
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
            _ => false,
        };
        if (!cheat || !int.TryParse(request.Uid, out int account))
            return;
        lock (gate)
        {
            if (!Ready || !policy.Cheaters.Enabled || ReportTarget(account, session) is not { } career)
                return;
            // Already banned, or every new report pays again.
            if (career.BannedUtc > 0)
                return;
            string sid = session.ToString();
            if (population.CheaterReports.Values.Any(r => r.SessionId == sid && r.CareerId == career.Id))
                return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var s = policy.Cheaters;
            var report = new CheaterReport
            {
                CareerId = career.Id,
                SessionId = sid,
                ReportedUtc = now,
                DueUtc =
                    now
                    + (long)((s.BanMinimumHours + Random.Shared.NextDouble() * (s.BanMaximumHours - s.BanMinimumHours)) * 3600),
            };
            // The death screen reports the raid alias, a looked-up profile the career's own account. Either gets the gear back.
            if (
                population.RaidLosses.TryGetValue(sid, out var loss)
                && now - loss.Utc < 6 * 3600
                && (
                    loss.KillerAccountId == account
                    || encounters.TryGetValue(loss.KillerAccountId, out var killer) && killer.CareerId == career.Id
                )
            )
            {
                report.KilledYou = true;
                report.LostGear = StillLost(loss.Items, sid);
                population.RaidLosses.Remove(sid);
            }

            population.CheaterReports[Guid.NewGuid().ToString("N")] = report;
            dirty = true;
        }
    }

    // A raid alias, or a career's own account from a profile lookup. Only accounts you met in a raid, so browsing profiles for blatant stats can't farm rewards.
    private Career? ReportTarget(int account, MongoId session)
    {
        if (encounters.TryGetValue(account, out var e))
            return CanAccess(e, session) && population.Careers.TryGetValue(e.CareerId, out var met) ? met : null;
        if (!byAccount.TryGetValue(account, out var career))
            return null;
        return encounters.Values.Any(x => x.CareerId == career.Id && CanAccess(x, session)) ? career : null;
    }

    private void ResolveCheaterReports(long now)
    {
        foreach (var (id, report) in population.CheaterReports.Where(p => now >= p.Value.DueUtc).ToArray())
        {
            population.CheaterReports.Remove(id);
            dirty = true;
            if (!population.Careers.TryGetValue(report.CareerId, out var c) || !c.Cheater)
                continue;
            if (c.BannedUtc == 0)
            {
                if (Random.Shared.NextDouble() >= policy.Cheaters.BanChance)
                    continue;
                Ban(c, "Reported by you", now);
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

    // The client's in-raid anticheat. Only real cheater careers, so a career borrowed for debug is never banned.
    public bool BannedInRaid(CheaterBanRequest request, MongoId session)
    {
        lock (gate)
        {
            if (
                !Ready
                || !policy.Cheaters.Enabled
                || !encounters.TryGetValue(request.AccountId, out var e)
                || !CanAccess(e, session)
                || !population.Careers.TryGetValue(e.CareerId, out var career)
                || !IsCheater(career)
            )
                return false;
            string map = MapLabel(e.Map);
            Ban(
                career,
                map.Length > 0 ? $"Banned mid-raid on {map}" : "Banned mid-raid",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            );
            // The next checkpoint saves it. A full save here would hold the gate mid-raid.
            dirty = true;
            return true;
        }
    }

    // Swaps in a new list, since SPT reads the old one on its own threads.
    private static void RemoveFirstName(BotType? type, Career c)
    {
        if (type?.FirstNames is not { } names)
            return;
        string key = NameKey(c.Side, c.SourceName);
        type.FirstNames = names.Where(n => NameKey(c.Side, n) != key).ToList();
    }

    private void Ban(Career c, string reason, long now)
    {
        if (c.BannedUtc > 0)
            return;
        c.BannedUtc = now;
        c.BanReason = reason;
        c.Retired = true;
        c.IsOnline = false;
        // The world tick skips retired careers, so a raid left running would count on its map forever.
        if (activities.TryGetValue(c.Id, out var a))
            Set(a, "Offline", "", now, 0);
        if (bots.Types.TryGetValue(c.Side.ToLowerInvariant(), out var type))
            RemoveFirstName(type, c);
        Log("ban", $"{c.Name} was banned for cheating");
        dirty = true;
    }

    // Killed by them: what you still lack from that raid, plus the full roubles. Survived or only met them: a smaller thank-you.
    private void SendBanReward(CheaterReport report)
    {
        var s = policy.Cheaters;
        var items = new List<Item>();
        // Reports filed before KilledYou existed only carry gear when the cheater killed you.
        bool killed = report.KilledYou || report.LostGear.Count > 0;
        // Checked again at payout, since a restore mod or insurance can hand gear back after the report.
        var gear = s.ReturnLostGear && killed ? StillLost(report.LostGear, report.SessionId) : [];
        if (gear.Count > 0)
            items.AddRange((json.Deserialize<List<Item>>(gear.ToJsonString()) ?? []).ReplaceIDs());
        long rubles = killed ? s.RewardRubles : (long)Math.Round(s.RewardRubles * s.WitnessRewardShare / 1000) * 1000;
        if (rubles > 0)
            items.AddRange(
                itemHelper.SplitStack(
                    new Item
                    {
                        Id = new MongoId(),
                        Template = new MongoId("5449016a4bdc2d6f028b456f"),
                        Upd = new Upd { StackObjectsCount = rubles },
                    }
                )
            );
        string text = "Thank you for your report. A player you reported has been banned for cheating.";
        if (gear.Count > 0)
            text += " The equipment you lost to them has been returned, along with compensation.";
        else if (items.Count > 0)
            text += killed ? " Please accept this compensation." : " Please accept this as thanks.";
        mail.SendSystemMessageToPlayer(new MongoId(report.SessionId), text, items, 7 * 86400L);
    }

    // Gear a restore mod (Fair Equipment Restoration and the like) already put back keeps its ids, so only items
    // missing from your inventory count. Children of a returned item go too, since they'd arrive without a parent.
    private JsonArray StillLost(JsonArray items, string session)
    {
        HashSet<string> owned;
        try
        {
            owned =
                saves.GetProfile(new MongoId(session)).CharacterData?.PmcData?.Inventory?.Items?.Select(i => i.Id.ToString()).ToHashSet()
                ?? [];
        }
        catch
        {
            owned = [];
        }

        var nodes = items.OfType<JsonObject>().ToList();
        var all = nodes.Select(n => n["_id"]?.ToString()).OfType<string>().ToHashSet();
        var kept = all.Where(id => !owned.Contains(id)).ToHashSet();
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var n in nodes)
            {
                string? id = n["_id"]?.ToString(),
                    parent = n["parentId"]?.ToString();
                if (id is not null && kept.Contains(id) && parent is not null && all.Contains(parent) && !kept.Contains(parent))
                {
                    kept.Remove(id);
                    changed = true;
                }
            }
        }

        var result = new JsonArray();
        foreach (var n in nodes)
            if (n["_id"]?.ToString() is { } id && kept.Contains(id))
                result.Add(n.DeepClone());
        return result;
    }

    // A swap after APBS picked the kit, so the rest of the loadout stays a normal roll.
    private void ApplyCheaterHelmet(JsonObject equipment)
    {
        var pool = policy.Cheaters.Helmets.Where(h => h.Weight > 0 && templates.Items.ContainsKey(new MongoId(h.Tpl))).ToList();
        if (pool.Count == 0)
            return;
        int roll = Random.Shared.Next(pool.Sum(h => h.Weight));
        var pick = pool.First(h => (roll -= h.Weight) < 0);
        var template = templates.Items[new MongoId(pick.Tpl)];
        string slot = template.Parent.ToString() == FaceCoverParent ? "FaceCover" : "Headwear";
        if (CreateShowcaseCollectible(pick.Tpl) is not { } tree)
            return;
        if (
            pick.FaceShield.Length == 24
            && templates.Items.ContainsKey(new MongoId(pick.FaceShield))
            && CreateShowcaseCollectible(pick.FaceShield) is { } shield
        )
        {
            string helmet = tree[0]!["_id"]!.ToString();
            string? mount = ShowcaseProps(pick.Tpl)["Slots"]
                ?.AsArray()
                .OfType<JsonObject>()
                .FirstOrDefault(x =>
                    x["_props"]
                        ?["filters"]?.AsArray()
                        .OfType<JsonObject>()
                        .Any(f => f["Filter"]?.AsArray().Any(v => v?.ToString() == pick.FaceShield) == true) == true
                )
                ?["_name"]?.ToString();
            if (mount is not null)
            {
                if (
                    tree.OfType<JsonObject>()
                        .FirstOrDefault(i => i["parentId"]?.ToString() == helmet && i["slotId"]?.ToString() == mount) is
                    { } old
                )
                    RemoveTree(tree, old["_id"]!.ToString());
                var down = (JsonObject)shield[0]!;
                var upd = down["upd"] as JsonObject ?? new JsonObject();
                upd["Togglable"] = new JsonObject { ["On"] = true };
                down["upd"] = upd;
                Attach(tree, helmet, mount, shield);
            }
        }

        string root = equipment["Id"]!.ToString();
        var items = equipment["Items"]!.AsArray();
        var props = template.Properties;
        var clear = HeadSlots.ToHashSet();
        if (props?.BlocksEyewear == true)
            clear.Add("Eyewear");
        if (props?.BlocksFaceCover == true)
            clear.Add("FaceCover");
        if (props?.BlocksEarpiece == true)
            clear.Add("Earpiece");
        if (props?.BlocksHeadwear == true)
            clear.Add("Headwear");
        var added = tree.OfType<JsonObject>().Select(i => i["_tpl"]!.ToString()).ToHashSet();
        foreach (var top in items.OfType<JsonObject>().Where(i => i["parentId"]?.ToString() == root).ToArray())
        {
            string? topSlot = top["slotId"]?.ToString();
            if (
                topSlot is null
                || clear.Contains(topSlot)
                || topSlot is not ("Headwear" or "FaceCover" or "Eyewear" or "Earpiece")
            )
                continue;
            // Helmets list what they cannot be worn with, either way round.
            var subtree = ItemTree(items, top["_id"]!.ToString()).Select(n => n!["_tpl"]!.ToString()).ToHashSet();
            if (Conflicts(added, subtree) || Conflicts(subtree, added))
                clear.Add(topSlot);
        }

        foreach (
            var top in items
                .OfType<JsonObject>()
                .Where(i => i["parentId"]?.ToString() == root && clear.Contains(i["slotId"]?.ToString() ?? ""))
                .ToArray()
        )
        {
            var remove = ItemTree(items, top["_id"]!.ToString()).Select(n => n!["_id"]!.ToString()).ToHashSet();
            foreach (var node in items.OfType<JsonObject>().Where(i => remove.Contains(i["_id"]!.ToString())).ToArray())
                items.Remove(node);
        }

        var first = (JsonObject)tree[0]!;
        first["parentId"] = root;
        first["slotId"] = slot;
        first.Remove("location");
        foreach (var node in tree)
            items.Add(node!.DeepClone());
    }

    private bool Conflicts(HashSet<string> from, HashSet<string> against) =>
        from.Any(tpl =>
            templates.Items.TryGetValue(new MongoId(tpl), out var t)
            && t.Properties?.ConflictingItems is { } c
            && c.Any(x => against.Contains(x.ToString()))
        );
}
