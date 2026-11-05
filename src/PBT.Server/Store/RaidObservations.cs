namespace PBT.Server
{
    public sealed partial class PopulationStore
    {
        public int Observe(ActivityReport report, MongoId session)
        {
            lock (gate)
            {
                if (!Ready || report.ProtocolVersion != 1 || !Guid.TryParseExact(report.RaidId, "N", out _) || report.Bots is null || report.Bots.Count is < 1 or > 512) return 0;
                Dictionary<int, RaidObservation> previousObservations = population.RaidObservations;
                Dictionary<int, RaidObservation> next = new Dictionary<int, RaidObservation>(previousObservations);
                Dictionary<string, ObservedCareer> previousCareers = new Dictionary<string, ObservedCareer>();
                Dictionary<string, ObservedCareer> updates = new Dictionary<string, ObservedCareer>();
                Dictionary<string, Dictionary<string, long>> previousAchievements = new Dictionary<string, Dictionary<string, long>>();
                Dictionary<string, (bool NeedsRegear, int Streak, long StoryDeaths)> previousLife = new Dictionary<string, (bool NeedsRegear, int Streak, long StoryDeaths)>();
                HashSet<string> newDeaths = new HashSet<string>();
                Dictionary<int, string> confirmedStoryDeaths = new Dictionary<int, string>();
                Dictionary<int, string> previousStories = population.AppliedStories;
                HashSet<int> seen = new HashSet<int>();
                List<(Encounter Encounter, RaidObservation Observation)> finished = new List<(Encounter Encounter, RaidObservation Observation)>();
                int accepted = 0;
                bool changed = false;
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                foreach (var incoming in report.Bots)
                {
                    if (incoming is null || !seen.Add(incoming.AccountId) || !encounters.TryGetValue(incoming.AccountId, out Encounter? encounter) || !CanAccess(encounter, session)
                        || encounter.RuntimeProfileId != incoming.RuntimeProfileId || !population.Careers.TryGetValue(encounter.CareerId, out Career? career)
                        || incoming.ActiveSeconds < 0 || incoming.ActiveSeconds > 21600 || incoming.ActiveSeconds > Math.Max(0, now - encounter.GeneratedUtc) + 60
                        || incoming.PmcKills is < 0 or > 512 || incoming.ScavKills is < 0 or > 512 || incoming.UsecKills is < 0 or > 512 || incoming.BearKills is < 0 or > 512
                        || incoming.UsecKills + incoming.BearKills > incoming.PmcKills) continue;
                    next.TryGetValue(incoming.AccountId, out RaidObservation? old);
                    // A cached alias belongs to at most one observed raid. Never rebind it to a later raid.
                    if (old is not null && old.RaidId != report.RaidId) continue;
                    RaidObservation merged = incoming with
                    {
                        RaidId = report.RaidId,
                        ActiveSeconds = Math.Max(incoming.ActiveSeconds, old?.ActiveSeconds ?? 0),
                        Died = incoming.Died || old?.Died == true,
                        Finished = incoming.Finished || old?.Finished == true,
                        LastReportUtc = old?.LastReportUtc ?? now,
                        PmcKills = Math.Max(incoming.PmcKills, old?.PmcKills ?? 0),
                        ScavKills = Math.Max(incoming.ScavKills, old?.ScavKills ?? 0),
                        UsecKills = Math.Max(incoming.UsecKills, old?.UsecKills ?? 0),
                        BearKills = Math.Max(incoming.BearKills, old?.BearKills ?? 0),
                        KilledByPlayer = incoming.KilledByPlayer || old?.KilledByPlayer == true,
                        KilledPlayer = incoming.KilledPlayer || old?.KilledPlayer == true,
                        NearExtract = incoming.NearExtract || old?.NearExtract == true,
                        LootValue = Math.Clamp(Math.Max(incoming.LootValue, old?.LootValue ?? 0), 0, 1_000_000_000)
                    };
                    if (merged.UsecKills + merged.BearKills > merged.PmcKills) continue;
                    accepted++;
                    if (merged == old) continue;
                    merged = merged with
                    {
                        LastReportUtc = now
                    };
                    if (merged.Finished && old?.Finished != true) finished.Add((encounter, merged));
                    if (merged.Died && old?.Died != true)
                    {
                        newDeaths.Add(career.Id);
                        if (population.AppliedStories.TryGetValue(incoming.AccountId, out string? story) && story is "Died" or "DiedUncounted")
                            confirmedStoryDeaths[incoming.AccountId] = career.Id;
                    }

                    if (!updates.TryGetValue(career.Id, out ObservedCareer? totals))
                    {
                        previousCareers.Add(career.Id, career.Observed);
                        updates.Add(career.Id, totals = career.Observed with
                        {
                        });
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
                foreach ((string id, ObservedCareer totals) in updates)
                {
                    Career career = population.Careers[id];
                    previousAchievements.Add(id, career.Achievements);
                    previousLife.Add(id, (career.NeedsRegear, career.CurrentSurvivalStreak, career.StoryDeaths));
                    if (newDeaths.Contains(id))
                    {
                        MarkDead(career, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        career.CurrentSurvivalStreak = 0;
                    }

                    career.Observed = totals;
                }

                population.AppliedStories = new Dictionary<int, string>(previousStories);
                foreach ((int account, string id) in confirmedStoryDeaths)
                {
                    if (previousStories[account] == "Died") population.Careers[id].StoryDeaths = Math.Max(0, population.Careers[id].StoryDeaths - 1);
                    population.AppliedStories[account] = "ConfirmedDeath";
                }

                try
                {
                    // Receipts and totals share one snapshot, so retries cannot double-count.
                    foreach (string id in updates.Keys)
                    {
                        population.Careers[id].Achievements = EarnedMilestones(population.Careers[id], now);
                    }

                    dirty = true;
                }
                catch
                {
                    population.RaidObservations = previousObservations;
                    foreach ((string id, ObservedCareer totals) in previousCareers)
                    {
                        population.Careers[id].Observed = totals;
                    }

                    foreach ((string id, Dictionary<string, long> achievements) in previousAchievements)
                    {
                        population.Careers[id].Achievements = achievements;
                    }

                    foreach ((string id, (bool NeedsRegear, int Streak, long StoryDeaths) life) in previousLife)
                    {
                        population.Careers[id].NeedsRegear = life.NeedsRegear;
                        population.Careers[id].CurrentSurvivalStreak = life.Streak;
                        population.Careers[id].StoryDeaths = life.StoryDeaths;
                    }

                    population.AppliedStories = previousStories;
                    dirty = previousDirty;
                    throw;
                }

                foreach ((Encounter encounter, RaidObservation observation) in finished)
                {
                    try
                    {
                        AfterRaid(encounter, observation, report.Map, false, now);
                    }
                    catch (Exception ex)
                    {
                        Report(ex);
                    }
                }

                return accepted;
            }
        }
    }
}
