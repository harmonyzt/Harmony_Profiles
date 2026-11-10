using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;

namespace PBT.Server;

public sealed partial class PopulationStore
{
    private sealed record ChatSender(int AccountId, string CareerId, double Distance, bool Headshot);

    private sealed record ChatIntent(
        MongoId Session,
        bool Killer,
        string PlayerName,
        int PlayerLevel,
        string Map,
        List<ChatSender> Senders,
        bool Wipe,
        int PlayerPmcKills,
        bool BossKilled,
        double KitValue,
        long DueUtc
    );

    private readonly List<ChatIntent> chatIntents = new();

    // Returns the victims SPT should still answer for. Careers are ours.
    internal List<Victim> QueueVictimMessages(MongoId session, List<Victim> victims, PmcData player)
    {
        lock (gate)
        {
            if (!Ready)
                return victims;
            var rest = new List<Victim>();
            var senders = new List<ChatSender>();
            foreach (var victim in victims)
            {
                if (
                    !int.TryParse(victim.AccountId, out int account)
                    || !encounters.TryGetValue(account, out var e)
                    || !population.Careers.ContainsKey(e.CareerId)
                )
                {
                    rest.Add(victim);
                    continue;
                }

                senders.Add(
                    new(
                        account,
                        e.CareerId,
                        victim.Distance ?? 0,
                        victim.BodyPart?.Equals("Head", StringComparison.OrdinalIgnoreCase) == true
                    )
                );
            }

            var messages = policy.BotMessages;
            if (!messages.Enabled && !messages.ReplaceNativeMessages)
                return victims;
            if (messages.Enabled && senders.Count > 0)
            {
                var all = player.Stats?.Eft?.Victims?.ToList() ?? victims;
                chatIntents.Add(
                    new(
                        session,
                        false,
                        player.Info?.Nickname ?? "",
                        player.Info?.Level ?? 1,
                        MapName(victims.FirstOrDefault()?.Location),
                        senders,
                        Wipe(all),
                        PmcKills(all),
                        all.Any(IsBoss),
                        0,
                        Due()
                    )
                );
            }

            return messages.ReplaceNativeMessages ? rest : victims;
        }
    }

    internal bool QueueKillerMessage(MongoId session, PmcData player, Aggressor killer)
    {
        lock (gate)
        {
            if (
                !Ready
                || !int.TryParse(killer.AccountId, out int account)
                || !encounters.TryGetValue(account, out var e)
                || !population.Careers.ContainsKey(e.CareerId)
            )
                return false;
            var messages = policy.BotMessages;
            if (messages.Enabled)
            {
                var all = player.Stats?.Eft?.Victims?.ToList() ?? [];
                chatIntents.Add(
                    new(
                        session,
                        true,
                        player.Info?.Nickname ?? "",
                        player.Info?.Level ?? 1,
                        MapName(all.FirstOrDefault()?.Location),
                        [new(account, e.CareerId, 0, false)],
                        false,
                        PmcKills(all),
                        all.Any(IsBoss),
                        KitValue(player),
                        Due()
                    )
                );
            }

            return messages.ReplaceNativeMessages;
        }
    }

    private long Due() =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        + Random.Shared.Next(policy.BotMessages.MinimumDelaySeconds, policy.BotMessages.MaximumDelaySeconds + 1);

    private static bool IsBoss(Victim v) => v.Role?.StartsWith("boss", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsPmcVictim(Victim v) => v.Role?.ToLowerInvariant() is "pmcbear" or "pmcusec";

    private static int PmcKills(List<Victim> all) => all.Count(IsPmcVictim);

    // Three PMC kills inside three minutes reads as a squad wipe. Without usable times, four in one raid.
    private static bool Wipe(List<Victim> all)
    {
        var times = all.Where(IsPmcVictim).Select(v => TimeSpan.TryParse(v.Time, out var t) ? t : (TimeSpan?)null).ToList();
        if (times.Count < 3)
            return false;
        if (times.Any(t => t is null))
            return times.Count >= 4;
        var sorted = times.Select(t => t!.Value).Order().ToList();
        for (int i = 2; i < sorted.Count; i++)
            if (sorted[i] - sorted[i - 2] <= TimeSpan.FromMinutes(3))
                return true;
        return false;
    }

    private double KitValue(PmcData player)
    {
        var items = player.Inventory?.Items;
        if (items is null || player.Inventory?.Equipment is not MongoId root)
            return 0;
        var worn = new HashSet<string> { root.ToString() };
        bool changed;
        do
        {
            changed = false;
            foreach (var item in items)
                if (item.ParentId is { } parent && worn.Contains(parent))
                    changed |= worn.Add(item.Id.ToString());
        } while (changed);

        var handbook = HandbookPrices();
        return items
            .Where(i => worn.Contains(i.Id.ToString()) && i.Id != root)
            .Sum(i =>
                (
                    templates.Prices.TryGetValue(i.Template, out double flea)
                        ? flea
                        : handbook.GetValueOrDefault(i.Template.ToString())
                ) * Math.Max(1, i.Upd?.StackObjectsCount ?? 1)
            );
    }

    private string MapName(string? location)
    {
        if (string.IsNullOrEmpty(location))
            return "the map";
        try
        {
            return locales.GetLocaleDb().GetValueOrDefault(location, location);
        }
        catch
        {
            return location;
        }
    }

    // Caller holds the gate.
    private void SendDueMessages(long now)
    {
        foreach (var intent in chatIntents.Where(i => i.DueUtc <= now).ToList())
        {
            chatIntents.Remove(intent);
            if (ComposeMessage(intent) is not { } message)
                continue;
            var (career, text) = message;
            var sender = new UserDialogInfo
            {
                Id = new MongoId(career.Id),
                Aid = career.AccountId,
                Info = new UserDialogDetails
                {
                    Nickname = career.Name,
                    Side = career.Side,
                    Level = Level(career.Experience),
                    MemberCategory = (MemberCategory)career.MemberCategory,
                    SelectedMemberCategory = (MemberCategory)career.SelectedMemberCategory,
                },
            };
            _ = SendChat(intent.Session, sender, text);
        }
    }

    private async Task SendChat(MongoId session, UserDialogInfo sender, string text)
    {
        try
        {
            await notifications.SendMessageToPlayerAsync(session, sender, text, MessageType.UserMessage);
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    // At most one message per raid. Each notable event rolls the chance; the most notable one speaks.
    private (Career, string)? ComposeMessage(ChatIntent intent)
    {
        double chance = policy.BotMessages.Chance;
        var events = new List<(Career Career, string Event, double Weight, Dictionary<string, string> Values)>();
        foreach (var s in intent.Senders)
        {
            if (!population.Careers.TryGetValue(s.CareerId, out var career))
                continue;
            population.RaidObservations.TryGetValue(s.AccountId, out var seen);
            var values = new Dictionary<string, string>
            {
                ["player"] = intent.PlayerName,
                ["map"] = intent.Map,
                ["distance"] = ((int)s.Distance).ToString(),
                ["kills"] = intent.PlayerPmcKills.ToString(),
                ["playerLevel"] = intent.PlayerLevel.ToString(),
            };
            if (intent.Killer)
            {
                if (intent.PlayerPmcKills >= 3)
                    events.Add((career, "rampage", 2, values));
                if (Level(career.Experience) <= intent.PlayerLevel - 25)
                    events.Add((career, "underdog", 1.5, values));
                if (intent.KitValue >= 1_000_000)
                    events.Add((career, "kit", 1, values));
                continue;
            }

            if (intent.Wipe)
                events.Add((career, "wipe", 3, values));
            if (s.Distance >= 150)
                events.Add((career, "longshot", s.Headshot ? 2 : 1.5, values));
            if (seen?.NearExtract == true)
                events.Add((career, "extract", 1.5, values));
            if (seen?.LootValue >= 400_000)
                events.Add((career, "loaded", 1, values));
            if (intent.BossKilled)
                events.Add((career, "boss", 1, values));
        }

        var spoken = events.Where(e => Random.Shared.NextDouble() < Math.Min(1, chance * e.Weight)).ToList();
        if (spoken.Count == 0)
            return null;
        var pick = spoken.OrderByDescending(e => e.Weight).ThenBy(_ => Random.Shared.Next()).First();
        string tone = Tone(pick.Career);
        if (!BotMessageLines.Pools.TryGetValue(pick.Event + ":" + tone, out var lines))
            return null;
        string text = lines[Random.Shared.Next(lines.Length)];
        foreach (var (key, value) in pick.Values)
            text = text.Replace("{" + key + "}", value);
        if (tone == "timmy")
            text = text.ToLowerInvariant();
        return (pick.Career, text);
    }

    private string Tone(Career c)
    {
        if (Tilted(c))
            return "salty";
        if (Notable(c))
            return c.Name.Contains("chad", StringComparison.OrdinalIgnoreCase)
                ? (Stable(c.Id + ":tone") < .5 ? "salty" : "chill")
                : "streamer";
        return c.Archetype switch
        {
            "Timmy" => "timmy",
            "Aggressive" => Stable(c.Id + ":tone") < .5 ? "salty" : "tryhard",
            "Casual" => Stable(c.Id + ":tone") switch
            {
                < .25 => "salty",
                < .6 => "chill",
                _ => "friendly",
            },
            _ => Stable(c.Id + ":tone") < .3 ? "friendly" : "chill",
        };
    }
}
