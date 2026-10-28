using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;

namespace HarmonyProfiles.Server;

public sealed class ActivityReport : IRequestData
{
    public int ProtocolVersion { get; set; }
    public string RaidId { get; set; } = "";
    public List<RaidObservation> Bots { get; set; } = [];
}

public sealed record RaidObservation
{
    public int AccountId { get; set; }
    public string RuntimeProfileId { get; set; } = "";
    public string RaidId { get; set; } = "";
    public int ActiveSeconds { get; set; }
    public bool Died { get; set; }
    public bool Finished { get; set; }
    public long LastReportUtc { get; set; }
    public int PmcKills { get; set; }
    public int ScavKills { get; set; }
    public int UsecKills { get; set; }
    public int BearKills { get; set; }
}

public sealed record ObservedCareer
{
    public long Raids { get; set; }
    public long Deaths { get; set; }
    public long PmcKills { get; set; }
    public long ScavKills { get; set; }
    public long UsecKills { get; set; }
    public long BearKills { get; set; }
    public long ActiveSeconds { get; set; }
    public long PendingSimulationSeconds { get; set; }
}

public sealed partial class PopulationStore
{
    public int Observe(ActivityReport report, MongoId session)
    {
        lock (gate)
        {
            if (!Ready || report.ProtocolVersion != 1 || !Guid.TryParseExact(report.RaidId, "N", out _)
                || report.Bots is null || report.Bots.Count is < 1 or > 512) return 0;
            var previousObservations = population.RaidObservations;
            var next = new Dictionary<int, RaidObservation>(previousObservations);
            var previousCareers = new Dictionary<string, ObservedCareer>();
            var updates = new Dictionary<string, ObservedCareer>();
            var previousAchievements = new Dictionary<string, Dictionary<string, long>>();
            var previousLife = new Dictionary<string, (bool NeedsRegear, int Streak, long StoryDeaths)>();
            var newDeaths = new HashSet<string>();
            var confirmedStoryDeaths = new Dictionary<int, string>();
            var previousStories = population.AppliedStories;
            var seen = new HashSet<int>();
            int accepted = 0;
            bool changed = false;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var incoming in report.Bots)
            {
                if (incoming is null || !seen.Add(incoming.AccountId)
                    || !encounters.TryGetValue(incoming.AccountId, out var encounter)
                    || encounter.SessionId != session.ToString() || encounter.RuntimeProfileId != incoming.RuntimeProfileId
                    || !population.Careers.TryGetValue(encounter.CareerId, out var career)
                    || incoming.ActiveSeconds < 0 || incoming.ActiveSeconds > 21600
                    || incoming.ActiveSeconds > Math.Max(0, now - encounter.GeneratedUtc) + 60
                    || incoming.PmcKills is < 0 or > 512 || incoming.ScavKills is < 0 or > 512
                    || incoming.UsecKills is < 0 or > 512 || incoming.BearKills is < 0 or > 512
                    || incoming.UsecKills + incoming.BearKills > incoming.PmcKills) continue;
                next.TryGetValue(incoming.AccountId, out var old);
                // A cached alias belongs to at most one observed raid. Never rebind it to a later raid.
                if (old is not null && old.RaidId != report.RaidId) continue;
                var merged = incoming with
                {
                    RaidId = report.RaidId,
                    ActiveSeconds = Math.Max(incoming.ActiveSeconds, old?.ActiveSeconds ?? 0),
                    Died = incoming.Died || old?.Died == true,
                    Finished = incoming.Finished || old?.Finished == true,
                    LastReportUtc = old?.LastReportUtc ?? now,
                    PmcKills = Math.Max(incoming.PmcKills, old?.PmcKills ?? 0),
                    ScavKills = Math.Max(incoming.ScavKills, old?.ScavKills ?? 0),
                    UsecKills = Math.Max(incoming.UsecKills, old?.UsecKills ?? 0),
                    BearKills = Math.Max(incoming.BearKills, old?.BearKills ?? 0)
                };
                if (merged.UsecKills + merged.BearKills > merged.PmcKills) continue;
                accepted++;
                if (merged == old) continue;
                merged = merged with { LastReportUtc = now };
                if (merged.Died && old?.Died != true)
                {
                    newDeaths.Add(career.Id);
                    if (population.AppliedStories.TryGetValue(incoming.AccountId, out string? story) && story is "Died" or "DiedUncounted")
                        confirmedStoryDeaths[incoming.AccountId] = career.Id;
                }
                if (!updates.TryGetValue(career.Id, out var totals))
                {
                    previousCareers.Add(career.Id, career.Observed);
                    updates.Add(career.Id, totals = career.Observed with { });
                }
                totals.Raids += old is null ? 1 : 0;
                totals.Deaths += merged.Died && old?.Died != true ? 1 : 0;
                totals.PmcKills += merged.PmcKills - (old?.PmcKills ?? 0);
                totals.ScavKills += merged.ScavKills - (old?.ScavKills ?? 0);
                totals.UsecKills += merged.UsecKills - (old?.UsecKills ?? 0);
                totals.BearKills += merged.BearKills - (old?.BearKills ?? 0);
                int seconds = merged.ActiveSeconds - (old?.ActiveSeconds ?? 0);
                totals.ActiveSeconds += seconds;
                totals.PendingSimulationSeconds += seconds;
                next[incoming.AccountId] = merged;
                changed = true;
            }
            if (!changed) return accepted;
            bool previousDirty = dirty;
            population.RaidObservations = next;
            foreach (var (id, totals) in updates)
            {
                var career = population.Careers[id];
                previousAchievements.Add(id, career.Achievements);
                previousLife.Add(id, (career.NeedsRegear, career.CurrentSurvivalStreak, career.StoryDeaths));
                if (newDeaths.Contains(id)) { career.NeedsRegear = true; career.CurrentSurvivalStreak = 0; }
                career.Observed = totals;
            }
            population.AppliedStories = new Dictionary<int, string>(previousStories);
            foreach (var (account, id) in confirmedStoryDeaths)
            {
                if (previousStories[account] == "Died") population.Careers[id].StoryDeaths = Math.Max(0, population.Careers[id].StoryDeaths - 1);
                population.AppliedStories[account] = "ConfirmedDeath";
            }
            try
            {
                // Cumulative receipts and lifetime totals commit together, so retries cannot double-count.
                foreach (string id in updates.Keys)
                    population.Careers[id].Achievements = EarnedMilestones(population.Careers[id], now);
                SavePopulation();
            }
            catch
            {
                population.RaidObservations = previousObservations;
                foreach (var (id, totals) in previousCareers) population.Careers[id].Observed = totals;
                foreach (var (id, achievements) in previousAchievements) population.Careers[id].Achievements = achievements;
                foreach (var (id, life) in previousLife)
                {
                    population.Careers[id].NeedsRegear = life.NeedsRegear;
                    population.Careers[id].CurrentSurvivalStreak = life.Streak;
                    population.Careers[id].StoryDeaths = life.StoryDeaths;
                }
                population.AppliedStories = previousStories;
                dirty = previousDirty;
                throw;
            }
            return accepted;
        }
    }
}
