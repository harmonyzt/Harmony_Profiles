using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;

namespace PBT.Server;

// A friend who sees you struggling mails you a spare kit from their own stash.
public sealed partial class PopulationStore
{
    private sealed record GiftIntent(MongoId Session, string CareerId, long DueUtc);

    private readonly List<GiftIntent> giftIntents = new();

    private static readonly string[] GiftSlots = ["FirstPrimaryWeapon", "Headwear", "TacticalVest", "ArmorVest", "Backpack"];
    private const string AmmoParent = "5485a8684bdc2da71d8b4567";
    private const string Roubles = "5449016a4bdc2d6f028b456f";

    private static readonly Dictionary<string, string[]> GiftLines = new()
    {
        ["friendly"] =
        [
            "saw you've been having a rough time, here's a kit, go get em",
            "Had a spare loadout lying around. Take it, you need it more than me right now.",
            "Sent you some gear. Don't thank me, just extract with it this time!",
            "here, take this. we all have those wipes"
        ],
        ["chill"] =
        [
            "spare kit, all yours",
            "had extra gear, sent you some",
            "take it, stash is full anyway",
            "here. bring your own ammo tho"
        ],
        ["salty"] =
        [
            "ok this is getting sad. take the kit and stop dying",
            "sent you gear. if you lose this one too im unfriending you",
            "you're making me look bad. here, take something that isn't a pistol"
        ],
        ["tryhard"] =
        [
            "Kit sent. Play slower, hold angles, stop pushing every shot you hear.",
            "Take the gear. Watch your corners and don't sprint into buildings.",
            "Sent a loadout. Ammo is on you, buy something that pens."
        ],
        ["timmy"] =
        [
            "i found this stuff and i dont need it u can have it",
            "here i got 2 of these lol",
            "take it im scared to use it anyway"
        ],
        ["streamer"] =
        [
            "chat said I had to send you a kit, so here you go",
            "my viewers voted, you're getting a care package. good luck out there",
            "sent you a kit. chat is watching, don't let them down"
        ]
    };

    // Struggling: low level and low on money, or a long death streak. Prestiged players are on their own.
    public void RecordRaidOutcome(EndLocalRaidRequestData? request, MongoId session)
    {
        try
        {
            var status = request?.Results?.Result;
            if (status is null) return;
            lock (gate)
            {
                if (!Ready) return;
                string sid = session.ToString();
                bool died = status is ExitStatus.KILLED or ExitStatus.MISSINGINACTION;
                population.PlayerDeathStreaks[sid] = died ? population.PlayerDeathStreaks.GetValueOrDefault(sid) + 1 : 0;
                dirty = true;
                QueueGift(session, population.PlayerDeathStreaks[sid]);
            }
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    // Caller holds the gate.
    private void QueueGift(MongoId session, int streak)
    {
        var g = policy.FriendGifts;
        string sid = session.ToString();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!g.Enabled || !SocialReady || giftIntents.Any(i => i.Session == session)
            || now - population.LastFriendGiftUtc.GetValueOrDefault(sid) < g.CooldownHours * 3600) return;
        var pmc = saves.GetProfile(session).CharacterData?.PmcData;
        if (pmc?.Info is null || (pmc.Info.PrestigeLevel ?? 0) > 0) return;
        double rubles = pmc.Inventory?.Items?.Where(i => i.Template.ToString() == Roubles).Sum(i => i.Upd?.StackObjectsCount ?? 0) ?? 0;
        bool poor = (pmc.Info.Level ?? 1) < g.MaximumLevel && rubles < g.MaximumRubles;
        if (!poor && streak < g.DeathStreak || Random.Shared.NextDouble() >= g.Chance) return;
        if (!population.Friendships.TryGetValue(sid, out var links)) return;
        var friends = links.Where(p => p.Value.Accepted && !p.Value.Removed && !p.Value.Cancelled && population.Careers.TryGetValue(p.Key, out var c) && !c.Retired)
            .Select(p => population.Careers[p.Key]).OrderByDescending(c => c.IsOnline).ThenBy(_ => Random.Shared.Next()).ToList();
        if (friends.Count == 0) return;
        giftIntents.Add(new(session, friends[0].Id, now + Random.Shared.Next(120, 901)));
    }

    // Caller holds the gate. APBS runs off the gate, like a profile view.
    private void SendDueGifts(long now)
    {
        foreach (var intent in giftIntents.Where(i => i.DueUtc <= now).ToList())
        {
            giftIntents.Remove(intent);
            if (!population.Careers.TryGetValue(intent.CareerId, out var career) || career.Retired) continue;
            population.LastFriendGiftUtc[intent.Session.ToString()] = now;
            dirty = true;
            _ = Task.Run(() => SendGift(career, intent.Session));
        }
    }

    private void SendGift(Career career, MongoId session)
    {
        try
        {
            var equipment = GenerateIsolatedEquipment(career, session, cheaterLook: false);
            var items = GiftItems(equipment);
            if (items.Count == 0) return;
            string text;
            UserDialogInfo sender;
            lock (gate)
            {
                var lines = GiftLines.GetValueOrDefault(Tone(career)) ?? GiftLines["friendly"];
                text = lines[Random.Shared.Next(lines.Length)];
                sender = new UserDialogInfo
                {
                    Id = new MongoId(career.Id),
                    Aid = career.AccountId,
                    Info = new UserDialogDetails
                    {
                        Nickname = career.Name,
                        Side = career.Side,
                        Level = Level(career.Experience),
                        MemberCategory = (MemberCategory)career.MemberCategory,
                        SelectedMemberCategory = (MemberCategory)career.SelectedMemberCategory
                    }
                };
                Log("gift", $"{career.Name} sent a friend a spare kit");
            }

            mail.SendUserMessageToPlayer(session, sender, text, items, 7 * 86400L);
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    // Gun, helmet, rig, armor and backpack, emptied: no ammo and nothing in the containers.
    private List<Item> GiftItems(JsonObject equipment)
    {
        var all = equipment["Items"]!.AsArray();
        string root = equipment["Id"]!.ToString();
        string parent = new MongoId().ToString();
        var result = new JsonArray();
        foreach (var top in all.OfType<JsonObject>().Where(i => i["parentId"]?.ToString() == root && GiftSlots.Contains(i["slotId"]?.ToString())))
        {
            var tree = ItemTree(all, top["_id"]!.ToString());
            var nodes = tree.OfType<JsonObject>().ToList();
            var dropped = nodes.Where(n => n != tree[0] && (n["location"] is not null || ShowcaseBasedOn(n["_tpl"]!.ToString(), AmmoParent)))
                .Select(n => n["_id"]!.ToString()).ToHashSet();
            bool grew;
            do
            {
                grew = false;
                foreach (var n in nodes)
                    if (n["parentId"]?.ToString() is { } p && dropped.Contains(p))
                        grew |= dropped.Add(n["_id"]!.ToString());
            } while (grew);

            foreach (var node in nodes)
            {
                if (dropped.Contains(node["_id"]!.ToString())) continue;
                var copy = node.DeepClone().AsObject();
                if (node == tree[0])
                {
                    copy["parentId"] = parent;
                    copy["slotId"] = "hideout";
                    copy.Remove("location");
                }

                result.Add(copy);
            }
        }

        return json.Deserialize<List<Item>>(result.ToJsonString()) ?? [];
    }
}
