using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;

namespace PBT.Server;

public sealed partial class PopulationStore
{
    private void ScheduleStory(Encounter encounter, long now)
    {
        encounter.KillerCapturedUtc = now;
        if (!policy.EnablePostRaidStories) return;
        double roll = Stable(encounter.AccountId + ":post-raid");
        encounter.StoryOutcome = roll < policy.PostRaidDeathChance ? "Died"
            : roll < policy.PostRaidDeathChance + policy.PostRaidRegearChance ? "Regeared" : "None";
        encounter.StoryDueUtc = now + policy.StoryMinimumDelaySeconds
            + (long)(Stable(encounter.AccountId + ":story-delay") * (policy.StoryMaximumDelaySeconds - policy.StoryMinimumDelaySeconds));
    }

    private void ApplyStories(long now)
    {
        if (!policy.EnablePostRaidStories) return;
        foreach (var e in encounters.Values.Where(e => e.StoryOutcome != "None" && e.KillerCapturedUtc > 0 && e.StoryDueUtc <= now
            && !population.AppliedStories.ContainsKey(e.AccountId)))
        {
            var c = population.Careers[e.CareerId];
            if (!population.SessionEndedUtc.TryGetValue(e.SessionId, out long ended) || ended < e.KillerCapturedUtc || IsObservedActive(c, now)) continue;
            string result;
            if (population.RaidObservations.TryGetValue(e.AccountId, out var observation) && observation.Died) result = "ObservedDeath";
            else if (c.LastEquipmentOrder > e.GenerationOrder || c.EquipmentIsSimulated) result = "Superseded";
            else if (e.StoryOutcome == "Died")
            {
                MarkDead(c, now);
                if (observation is not null) { c.StoryDeaths++; result = "Died"; }
                else result = "DiedUncounted";
            }
            else if (TryRegear(c, story: true)) result = "Regeared";
            else if (now - e.StoryDueUtc < 1800) continue;
            else result = "NoLoadoutAvailable";
            if (result is "Died" or "DiedUncounted" or "Regeared") c.StoryHoldUntilUtc = now + policy.StoryVisibilitySeconds;
            population.AppliedStories[e.AccountId] = result;
            dirty = true;
        }
    }
}
