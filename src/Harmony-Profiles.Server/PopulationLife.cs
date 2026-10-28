using System.Text.Json;
using System.Text.Json.Nodes;

namespace HarmonyProfiles.Server;

public sealed class PersonalityTemplate
{
    public string Id { get; set; } = "Casual";
    public int Weight { get; set; } = 40;
    public int MinimumHours { get; set; } = 100;
    public int MaximumHours { get; set; } = 2500;
    public int MinimumLevel { get; set; } = 15;
    public int MaximumLevel { get; set; } = 50;
    public double MinimumDailyHours { get; set; } = .15;
    public double MaximumDailyHours { get; set; } = 2.65;
    public double MinimumSurvival { get; set; } = .25;
    public double MaximumSurvival { get; set; } = .5;
    public double MinimumRaidMinutes { get; set; } = 8;
    public double MaximumRaidMinutes { get; set; } = 42;
    public string? ProgressTo { get; set; }
    public double TransitionHours { get; set; } = 200;
}

public sealed partial class PopulationStore
{
    private List<PersonalityTemplate> personalities = [];

    private void LoadPersonalities()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(directory)!, "personalities.json");
        if (!File.Exists(path))
        {
            personalities =
            [
                new() { Id = "Timmy", Weight = 30, MinimumHours = 5, MaximumHours = 100, MinimumLevel = 1,
                    MaximumLevel = 15, MinimumSurvival = .12, MaximumSurvival = .3,
                    MinimumRaidMinutes = 4, MaximumRaidMinutes = 28, ProgressTo = "Casual" },
                new(),
                new() { Id = "Aggressive", Weight = 20, MinimumRaidMinutes = 5, MaximumRaidMinutes = 32 },
                new() { Id = "Survivor", Weight = 10, MinimumSurvival = .5, MaximumSurvival = .7,
                    MinimumRaidMinutes = 16, MaximumRaidMinutes = 48 }
            ];
            AtomicFile.Write(path, JsonSerializer.Serialize(personalities, new JsonSerializerOptions { WriteIndented = true }));
        }
        else personalities = JsonSerializer.Deserialize<List<PersonalityTemplate>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Personalities file is empty.");
        ValidatePersonalities(personalities);
    }

    private static void ValidatePersonalities(List<PersonalityTemplate> candidates)
    {
        if (candidates.Count is < 1 or > 100 || candidates.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id)
            || p.Id.Length > 64 || p.Weight is < 1 or > 1000 || p.MinimumHours < 1 || p.MaximumHours < p.MinimumHours
            || p.MaximumHours > 50000 || p.MinimumLevel < 1 || p.MaximumLevel < p.MinimumLevel || p.MaximumLevel > 79
            || !double.IsFinite(p.MinimumDailyHours) || !double.IsFinite(p.MaximumDailyHours)
            || p.MinimumDailyHours < 0 || p.MaximumDailyHours < p.MinimumDailyHours || p.MaximumDailyHours > 24
            || !double.IsFinite(p.MinimumSurvival) || !double.IsFinite(p.MaximumSurvival)
            || p.MinimumSurvival < 0 || p.MaximumSurvival < p.MinimumSurvival || p.MaximumSurvival > 1
            || !double.IsFinite(p.MinimumRaidMinutes) || !double.IsFinite(p.MaximumRaidMinutes)
            || p.MinimumRaidMinutes < 2 || p.MaximumRaidMinutes < p.MinimumRaidMinutes || p.MaximumRaidMinutes > 60
            || !double.IsFinite(p.TransitionHours) || p.TransitionHours < 1)
            || candidates.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != candidates.Count
            || candidates.Any(p => p.ProgressTo is not null && !candidates.Any(t => t.Id == p.ProgressTo)))
            throw new InvalidDataException("Invalid personality templates; existing population preserved.");
    }

    private PersonalityTemplate Personality(Career c) => personalities.FirstOrDefault(p => p.Id == c.Archetype)
        ?? personalities[0];

    private PersonalityTemplate PickPersonality()
    {
        int roll = Random.Shared.Next(personalities.Sum(p => p.Weight));
        foreach (var p in personalities) { roll -= p.Weight; if (roll < 0) return p; }
        return personalities[^1];
    }

    private double RaidDuration(Career c, bool random)
    {
        var p = Personality(c);
        double lower = Math.Clamp(p.MinimumRaidMinutes, 5, 45);
        double upper = Math.Max(lower, Math.Clamp(p.MaximumRaidMinutes, 5, 45));
        double personality = c.Archetype == "Aggressive" ? .8 : c.Archetype == "Survivor" ? 1.15 : 1;
        double mean = Cohort(c).Minutes * (.65 + Stable(c.Id + ":raid-duration") * .7)
            * (.7 + c.SurvivalChance * .6) * personality;
        double minutes = mean * (random ? .35 + Random.Shared.NextDouble() * 1.3 : 1);
        return Math.Clamp(minutes, lower, upper) * 60;
    }

    private void EnsureLife(Career c, long now)
    {
        bool migrating = c.LifeVersion < 2;
        if (migrating)
        {
            // Repair implausibly short history by adding time; never remove published raids or kills.
            c.RaidSeconds = Math.Max(c.RaidSeconds, c.Raids * RaidDuration(c, false));
            c.OnlineSeconds = Math.Max(c.OnlineSeconds, c.RaidSeconds);
            c.TargetRaidSeconds = RaidDuration(c, true);
            c.PendingRaidSeconds = Math.Clamp(c.PendingRaidSeconds, 0, Math.Max(0, c.TargetRaidSeconds - .001));
            c.LifeVersion = 2;
        }
        if (c.NextActivityUtc <= 0)
        {
            c.LastActivityUtc = now;
            c.NextActivityUtc = now + Random.Shared.Next(600, 1801);
        }
        if (migrating || c.RegisteredUtc <= 0)
        {
            double age = Math.Min(3650d * 86400, (c.RaidSeconds + c.Observed.ActiveSeconds)
                * (12 + Stable(c.Id + ":account-age") * 60));
            c.RegisteredUtc = Math.Max(1, Math.Min(c.RegisteredUtc > 0 ? c.RegisteredUtc : now, now - (long)age));
            if (c.PrestigeLevel > 0)
                c.RegisteredUtc = Math.Min(c.RegisteredUtc, now - Math.Min(3650L, c.PrestigeLevel * 180L) * 86400);
        }
    }

    private void ApplySimulation(Career c, double seconds)
    {
        int previousRaids = c.Raids, previousSurvived = c.Survived;
        AddRaids(c, seconds);
        int completed = c.Raids - previousRaids, survived = c.Survived - previousSurvived;
        c.Experience = (int)Math.Min(int.MaxValue, (long)c.Experience + completed * (c.Archetype == "Timmy" ? 700 : 1800));
        c.WealthRubles = Math.Clamp(c.WealthRubles + survived * (c.Archetype == "Timmy" ? 60000d : 150000d)
            - (completed - survived) * (c.Archetype == "Timmy" ? 30000d : 80000d), 0, 100_000_000);
        var personality = Personality(c);
        if (personality.ProgressTo is not null && (c.RaidSeconds + c.Observed.ActiveSeconds) / 3600 >= personality.TransitionHours)
        {
            c.Archetype = personality.ProgressTo;
            personality = Personality(c);
            c.SurvivalChance = personality.MinimumSurvival + (personality.MaximumSurvival - personality.MinimumSurvival)
                * Stable(c.Id + ":transition:" + c.Archetype);
        }
        if (completed > 0)
        {
            Develop(c);
            TryPrestige(c, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            c.GearPending = true;
        }
    }

    private bool IsObservedActive(Career c, long now) => population.RaidObservations.Values.Any(o => !o.Finished
        && now - o.LastReportUtc < 180 && encounters.TryGetValue(o.AccountId, out var active) && active.CareerId == c.Id);

    private void AdvanceOnline(long now)
    {
        foreach (var c in population.Careers.Values.Where(c => !c.Retired && now >= c.NextActivityUtc))
        {
            if (IsObservedActive(c, now) || now < c.StoryHoldUntilUtc) continue;
            long elapsed = Math.Clamp(now - Math.Max(c.LastActivityUtc, now - 1800), 0, 1800);
            bool friend = population.Friendships.Values.Any(links => links.TryGetValue(c.Id, out var f) && f.Accepted && !f.Removed);
            c.IsOnline = c.DailyRaidHours > 0 && Random.Shared.NextDouble()
                < Math.Min(.85, c.DailyRaidHours / 24 * (friend ? settings.FriendActivityMultiplier : 1));
            if (c.IsOnline)
            {
                ApplySimulation(c, elapsed);
                c.PendingLiveSeconds += elapsed;
                c.Achievements = EarnedMilestones(c, now);
            }
            c.LastActivityUtc = now;
            c.NextActivityUtc = now + Random.Shared.Next(600, 1801);
            dirty = true;
        }
    }

    private bool TryRegear(Career c, bool story = false)
    {
        c.GearPending = true;
        if ((!story && !c.IsOnline) || gearSession is not SPTarkov.Server.Core.Models.Common.MongoId session) return false;
        return GenerateCareerGear(c, session, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), story);
    }

    private static string? PrimaryWeapon(JsonObject equipment) => equipment["Items"]?.AsArray().OfType<JsonObject>()
        .FirstOrDefault(i => i["slotId"]?.ToString() == "FirstPrimaryWeapon")?["_tpl"]?.ToString();

    private static JsonObject NakedEquipment(JsonObject source)
    {
        string root = source["Id"]!.GetValue<string>();
        var all = source["Items"]!.AsArray().OfType<JsonObject>().ToArray();
        var keep = new HashSet<string>(all.Where(i => i["parentId"]?.ToString() == root && i["slotId"]?.ToString() == "Scabbard")
            .Select(i => i["_id"]!.GetValue<string>()));
        bool changed;
        do
        {
            changed = false;
            foreach (var i in all)
                if (i["parentId"] is { } p && keep.Contains(p.ToString())) changed |= keep.Add(i["_id"]!.GetValue<string>());
        } while (changed);
        keep.Add(root);
        return new JsonObject { ["Id"] = root, ["Items"] = new JsonArray(all.Where(i => keep.Contains(i["_id"]!.GetValue<string>()))
            .Select(i => (JsonNode)i.DeepClone()).ToArray()) };
    }

    private JsonObject RepairEquipment(JsonObject source)
    {
        try
        {
            string root = source["Id"]!.GetValue<string>();
            var valid = source["Items"]!.AsArray().OfType<JsonObject>()
                .Where(i => IsId(i["_tpl"]?.ToString()) && templates.Items.ContainsKey(new(i["_tpl"]!.ToString()))).ToArray();
            var keep = new HashSet<string> { root };
            bool changed;
            do
            {
                changed = false;
                foreach (var i in valid)
                    if (i["parentId"] is { } p && keep.Contains(p.ToString())) changed |= keep.Add(i["_id"]!.ToString());
            } while (changed);
            var result = new JsonObject { ["Id"] = root, ["Items"] = new JsonArray(valid.Where(i => keep.Contains(i["_id"]!.ToString()))
                .Select(i => (JsonNode)i.DeepClone()).ToArray()) };
            return ValidEquipment(result) ? result : EmptyEquipment();
        }
        catch { return EmptyEquipment(); }
    }
}
