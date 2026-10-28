using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;

namespace HarmonyProfiles.Server;

public sealed partial class PopulationStore
{
    private readonly HashSet<string> reportedRegressions = new();

    // Closed beta opened in July 2017, so no account is older than that.
    private const long FirstAccountUtc = 1501200000;

    private ProgressionPrior Cohort(Career career) => Cohort(Level(career.Experience));
    private ProgressionPrior Cohort(int level) => policy.ProgressionPriors.First(p => level <= p.MaximumLevel);

    // One stable roll per account drives survival, kills and pace together, so the stats agree with each other.
    private static double Skill(Career c) => Stable(c.Id + ":skill");

    private static double Aggression(Career c) => c.Archetype switch
    {
        "Timmy" => .6, "Aggressive" => 1.35, "Survivor" => .75, _ => 1
    };

    private double SurvivalFor(Career c)
    {
        var p = Personality(c);
        return p.MinimumSurvival + (p.MaximumSurvival - p.MinimumSurvival) * Skill(c);
    }

    private double OnlineFactor(Career c) => policy.OnlineTimeMinimum
        + (policy.OnlineTimeMaximum - policy.OnlineTimeMinimum) * Stable(c.Id + ":online-overhead");

    private double OnlineSeconds(Career c) => c.OnlineSeconds + c.Observed.ActiveSeconds * OnlineFactor(c);

    private double RaidExperience(int level, bool survived)
    {
        var x = policy.RaidExperience;
        double earned = x.SurvivedBase + x.SurvivedPerLevel * level + x.SurvivedPerLevelSquared * level * level;
        return survived ? earned : earned * x.DeathShare;
    }

    // Expected raids for this account's survival rate to earn the given experience, level by level.
    private double RaidsToReach(Career c, int experience)
    {
        double raids = 0, p = c.SurvivalChance;
        for (int level = 1; level < 80; level++)
        {
            int start = profileHelper.GetExperience(level) ?? 0;
            if (start >= experience) break;
            int end = Math.Min(experience, profileHelper.GetExperience(level + 1) ?? experience);
            double average = p * RaidExperience(level, true) + (1 - p) * RaidExperience(level, false);
            raids += Math.Max(0, end - start) / Math.Max(1, average);
        }
        return raids;
    }

    // Accounts are at least as old as their playtime needs at a steady pace, and many are older.
    private long AccountAge(Career c, long now)
    {
        long limit = Math.Max(86400, now - FirstAccountUtc);
        double pace = .5 + 2.5 * Stable(c.Id + ":pace");
        double needed = OnlineSeconds(c) / 3600 / pace * 86400;
        double roll = Stable(c.Id + ":account-age");
        double veteran = c.Archetype == "Timmy" ? roll * 120 * 86400 : Math.Pow(roll, 1.5) * limit;
        double age = Math.Max(needed, veteran);
        if (c.PrestigeLevel > 0) age = Math.Max(age, c.PrestigeLevel * 180d * 86400);
        return (long)Math.Clamp(age, 3 * 86400, limit);
    }

    private static int SampleKills(double mean)
    {
        double limit = Math.Exp(-Math.Clamp(mean, 0, 20)), product = 1;
        int count = 0;
        do { count++; product *= Random.Shared.NextDouble(); } while (product > limit && count < 128);
        return count - 1;
    }

    private void AddRaidKills(Career career)
    {
        var cohort = Cohort(career);
        double skill = Skill(career), aggression = Aggression(career);
        career.PmcKills = (int)Math.Min(int.MaxValue, (long)career.PmcKills + SampleKills(cohort.PmcKills * (.3 + 1.4 * skill) * aggression));
        career.ScavKills = (int)Math.Min(int.MaxValue, (long)career.ScavKills
            + SampleKills(cohort.ScavKills * (.6 + .8 * skill) * Math.Sqrt(aggression)));
    }

    private long Lifetime(Career career, string key, long computed)
    {
        long previous = career.LifetimeTotals.GetValueOrDefault(key);
        if (computed < previous && reportedRegressions.Add(career.Id + ":" + key))
            logger.Warning($"[Harmony-Profiles] Lifetime regression retained previous total: account={career.AccountId}, counter={key}, computed={computed}, previous={previous}.");
        long current = Math.Max(previous, Math.Max(0, computed));
        if (current != previous) { career.LifetimeTotals[key] = current; dirty = true; }
        return current;
    }

    private void PreserveLifetime(Career career)
    {
        Lifetime(career, "Sessions/Pmc", career.Raids + career.Observed.Raids);
        Lifetime(career, "Kills", (long)career.PmcKills + career.ScavKills + career.Observed.PmcKills + career.Observed.ScavKills);
        Lifetime(career, "KilledPmc", career.PmcKills + career.Observed.PmcKills);
        Lifetime(career, "KilledSavage", career.ScavKills + career.Observed.ScavKills);
        Lifetime(career, "OnlineTime", (long)OnlineSeconds(career));
    }

    private void EnsureCareerCoverage()
    {
        if (population.CoverageVersion == 1) return;
        var linked = population.Friendships.Values.SelectMany(links => links.Keys).ToHashSet();
        var encountered = encounters.Values.Select(e => e.CareerId).ToHashSet();
        foreach (string side in new[] { "Usec", "Bear" })
        {
            var careers = population.Careers.Values.Where(c => !c.Retired && c.Side == side).ToArray();
            int quota = Math.Clamp(careers.Length / 160, 1, 4), promoted = 0;
            var counts = careers.GroupBy(c => Level(c.Experience)).ToDictionary(g => g.Key, g => g.Count());
            for (int target = 79; target >= 1; target--)
            {
                int? xp = profileHelper.GetExperience(target);
                if (xp is null) continue;
                while (counts.GetValueOrDefault(target) < quota)
                {
                    var candidate = careers.Where(c => !linked.Contains(c.Id) && !encountered.Contains(c.Id)
                            && c.LastEquipmentAccount == 0 && c.PrestigeLevel == 0 && Level(c.Experience) < target
                            && counts.GetValueOrDefault(Level(c.Experience)) > quota)
                        .OrderBy(c => c.Archetype == "Timmy").ThenByDescending(c => Level(c.Experience)).ThenBy(c => c.AccountId).FirstOrDefault();
                    if (candidate is null) break;
                    int previous = Level(candidate.Experience);
                    candidate.Experience = xp.Value;
                    double raids = RaidsToReach(candidate, xp.Value) * (.9 + Stable(candidate.Id + ":coverage") * .3);
                    if (candidate.Raids < raids) AddRaids(candidate, (raids - candidate.Raids) * RaidDuration(candidate, false));
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    candidate.RegisteredUtc = Math.Min(candidate.RegisteredUtc, now - AccountAge(candidate, now));
                    candidate.GearPending = true;
                    counts[previous]--;
                    counts[target] = counts.GetValueOrDefault(target) + 1;
                    promoted++;
                    dirty = true;
                }
            }
            if (promoted > 0) logger.Info($"[Harmony-Profiles] Prepared {promoted} unencountered {side} careers for APBS level coverage; existing friend and encounter careers retained.");
        }
        population.CoverageVersion = 1;
        dirty = true;
    }
}
