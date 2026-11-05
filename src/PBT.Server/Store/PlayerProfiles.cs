namespace PBT.Server
{
    public sealed partial class PopulationStore
    {
        // Whole names only, so Bob's page does not list Bobby's raids.
        private static bool MentionsName(string text, string name)
        {
            for (int i = text.IndexOf(name, StringComparison.Ordinal); i >= 0; i = text.IndexOf(name, i + 1, StringComparison.Ordinal))
            {
                int end = i + name.Length;
                if ((i == 0 || !IsNameChar(text[i - 1])) && (end == text.Length || !IsNameChar(text[end]))) return true;
            }

            return false;
        }

        private static bool IsNameChar(char ch)
        {
            return char.IsLetterOrDigit(ch) || ch is '_' or '-';
        }

        public PlayerProfile? GetPlayerProfile(string id)
        {
            lock (gate)
            {
                if (!Ready || !population.Careers.TryGetValue(id, out Career? c) || c.Retired) return null;
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                (string state, string status, _, long since) = Describe(c, now, ObservedActiveCareers(now));

                // Never below what the in-game profile has already shown, like Lifetime() but read-only.
                long Shown(string key, long computed)
                {
                    return Math.Max(c.LifetimeTotals.GetValueOrDefault(key), Math.Max(0, computed));
                }

                long raids = Shown("Sessions/Pmc", c.Raids + c.Observed.Raids);
                long survived = Shown("ExitStatus/Survived/Pmc", c.Survived);
                long killed = Shown("ExitStatus/Killed/Pmc", c.Raids - c.Survived - c.MissingRaids - c.AwolRaids - c.RunThroughRaids + c.Observed.Deaths + c.StoryDeaths);
                long pmcKills = Shown("KilledPmc", c.PmcKills + c.Observed.PmcKills);
                List<PlayerLink> squad = squads.TryGetValue(c.Id, out string[]? members)
                    ? members.Where(m => m != c.Id && population.Careers.ContainsKey(m)).Select(m => new PlayerLink(m, population.Careers[m].Name)).ToList()
                    : [];
                List<WorldEvent> events = worldLog.Where(e => MentionsName(e.Text, c.Name)).Take(12).ToList();
                double kit = c.NeedsRegear ? 0 : KitValue(CareerEquipment(c));
                UpdateQuests(c, false);
                (List<string> recentQuests, List<string> activeQuests) = QuestSummary(c);
                return new PlayerProfile(c.Id, c.Name, c.Side, c.Archetype, Level(c.Experience), c.PrestigeLevel, c.Experience, Notable(c), state, status,
                    since > 0 ? now - since : 0, c.RegisteredUtc, Shown("OnlineTime", (long)OnlineSeconds(c)), raids, survived, killed,
                    Shown("ExitStatus/MissingInAction/Pmc", c.MissingRaids), Shown("ExitStatus/Left/Pmc", c.AwolRaids), Shown("ExitStatus/Runner/Pmc", c.RunThroughRaids), pmcKills,
                    Shown("KilledSavage", c.ScavKills + c.Observed.ScavKills), Math.Round((double)pmcKills / Math.Max(1, killed), 2), raids > 0 ? (double)survived / raids : 0,
                    c.CurrentSurvivalStreak, c.BestSurvivalStreak, c.Achievements.Count, c.AreaLevels.Values.Sum(), c.WealthRubles, kit, squad, events, c.GearHistory.ToList(),
                    c.QuestsDone, QuestsInReach(c), recentQuests, activeQuests, ScheduleOf(c).Name, Mood(c));
            }
        }
    }
}
