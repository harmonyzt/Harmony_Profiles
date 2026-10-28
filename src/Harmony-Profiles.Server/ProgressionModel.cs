using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;

namespace HarmonyProfiles.Server;

public sealed partial class PopulationStore
{
    private readonly HashSet<string> reportedRegressions = new();

    // Season 11 unflagged, established-player medians. Personalities deliberately overlap these priors.
    private (double Minutes, double PmcKills, double ScavKills) Cohort(Career career)
    {
        int level = Level(career.Experience);
        var prior = policy.ProgressionPriors.First(p => level <= p.MaximumLevel);
        return (prior.RaidMinutes, prior.PmcKills, prior.ScavKills);
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
        double skill = .45 + Stable(career.Id + ":kill-rate") * 1.1;
        double aggression = career.Archetype == "Timmy" ? .35 : career.Archetype == "Aggressive" ? 1.3
            : career.Archetype == "Survivor" ? .8 : 1;
        career.PmcKills = (int)Math.Min(int.MaxValue, (long)career.PmcKills + SampleKills(cohort.PmcKills * skill * aggression));
        career.ScavKills = (int)Math.Min(int.MaxValue, (long)career.ScavKills + SampleKills(cohort.ScavKills * (.6 + skill * .5)));
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
        Lifetime(career, "TimeInRaid", (long)career.RaidSeconds + career.Observed.ActiveSeconds);
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
                    double minimumHours = target * target * .12;
                    if (candidate.RaidSeconds < minimumHours * 3600) AddRaids(candidate, minimumHours * 3600 - candidate.RaidSeconds);
                    candidate.RegisteredUtc = Math.Min(candidate.RegisteredUtc,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)Math.Min(3650d * 86400, candidate.RaidSeconds * (12 + Stable(candidate.Id + ":account-age") * 60)));
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
