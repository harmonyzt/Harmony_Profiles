using SPTarkov.Server.Core.Models.Common;

namespace PBT.Server;

public sealed partial class PopulationStore
{
    private double DeathFactor(Career c) =>
        (Notable(c) ? .7 : 1)
        * c.Archetype switch
        {
            "Timmy" => 1.35,
            "Aggressive" => 1.2,
            "Survivor" => .6,
            _ => 1,
        };

    // Every PMC in your raid gets one. The dead only choose between logging off and regearing.
    private void AfterRaid(Encounter e, RaidObservation? observation, string map, bool killer, long now)
    {
        if (!population.Careers.TryGetValue(e.CareerId, out var c))
            return;
        map = MapKey(string.IsNullOrEmpty(map) ? e.Map : map);
        var a = ActivityOf(c);
        if (observation?.Died == true)
        {
            if (population.PendingStories.Remove(e.AccountId))
                population.AppliedStories[e.AccountId] = "ObservedDeath";
            Log(
                "death",
                observation.KilledByPlayer
                    ? $"{c.Name} was killed by a player on {MapLabel(map)}"
                    : $"{c.Name} died on {MapLabel(map)}",
                map
            );
            ToMenu(c, a, now, died: true);
            return;
        }

        if (
            !policy.EnablePostRaidStories
            || population.PendingStories.ContainsKey(e.AccountId)
            || population.AppliedStories.ContainsKey(e.AccountId)
        )
        {
            if (!population.PendingStories.ContainsKey(e.AccountId) && a.Held)
                ToMenu(c, a, now);
            return;
        }

        double death = Math.Min(.95, (killer ? policy.PostRaidDeathChance : policy.RaidDeathChance) * DeathFactor(c));
        double regear = killer ? policy.PostRaidRegearChance : policy.RaidRegearChance;
        double roll = Stable(e.AccountId + ":post-raid");
        var story = new PendingStory
        {
            CareerId = c.Id,
            Map = map,
            Killer = killer,
            GenerationOrder = e.GenerationOrder,
            ScheduledUtc = now,
            Outcome = roll < death ? "Died" : "Survived",
            Regear = roll >= death && roll < death + regear,
            DueUtc =
                now
                + policy.StoryMinimumDelaySeconds
                + (long)(
                    Stable(e.AccountId + ":story-delay") * (policy.StoryMaximumDelaySeconds - policy.StoryMinimumDelaySeconds)
                ),
        };
        var transits = TransitTargets(c, map).Select(m => m.Id).ToArray();
        if (story.Outcome == "Survived" && transits.Length > 0 && Stable(e.AccountId + ":transit") < policy.World.TransitChance)
        {
            story.Outcome = "Transit";
            story.TransitTo = transits[(int)(Stable(e.AccountId + ":transit-to") * transits.Length) % transits.Length];
        }

        population.PendingStories[e.AccountId] = story;
        HoldInRaid(c, map, story.DueUtc + 60);
        dirty = true;
    }

    private void ApplyStories(long now)
    {
        foreach (var (account, story) in population.PendingStories.Where(p => p.Value.DueUtc <= now).ToArray())
        {
            if (!population.Careers.TryGetValue(story.CareerId, out var c))
            {
                population.PendingStories.Remove(account);
                continue;
            }

            population.RaidObservations.TryGetValue(account, out var observation);
            // A killer story waits until your raid has actually ended.
            if (
                story.Killer
                && observation?.Finished != true
                && encounters.TryGetValue(account, out var e)
                && (!population.SessionEndedUtc.TryGetValue(e.SessionId, out long ended) || ended < story.ScheduledUtc)
            )
                continue;
            try
            {
                string result;
                var a = ActivityOf(c);
                string map = MapLabel(story.Map);
                if (observation?.Died == true)
                    result = "ObservedDeath";
                else if (c.LastEquipmentOrder > story.GenerationOrder || IsObservedActive(c, now))
                    result = "Superseded";
                else if (story.Outcome == "Died")
                {
                    MarkDead(c, now);
                    if (observation is not null)
                    {
                        c.StoryDeaths++;
                        result = "Died";
                    }
                    else
                        result = "DiedUncounted";

                    Log("death", Killed(c, story.Map, map), story.Map);
                }
                else if (story.Regear && !storyRegeared.Remove(account))
                {
                    if (now - story.DueUtc < 1800)
                    {
                        // The kit is generated after the gate is released, and the story lands on the next pass.
                        c.GearPending = true;
                        storyRegears[account] = c;
                        continue;
                    }

                    storyRegears.Remove(account);
                    result = "NoLoadoutAvailable";
                }
                else
                {
                    result =
                        story.Regear ? "Regeared"
                        : story.Outcome == "Transit" ? "Transited"
                        : "Survived";
                    if (story.Outcome == "Transit")
                        Log("transit", $"{c.Name} transited from {map} to {MapLabel(story.TransitTo)}", story.TransitTo);
                    else
                        Log(
                            "extract",
                            $"{c.Name} extracted from {map}" + (story.Regear ? " and bought a new kit" : ""),
                            story.Map
                        );
                }

                if (result is "Died" or "DiedUncounted" or "Regeared")
                    c.StoryHoldUntilUtc = now + policy.StoryVisibilitySeconds;
                population.PendingStories.Remove(account);
                population.AppliedStories[account] = result;
                if (result == "Transited")
                    Set(a, "Transit", story.Map, now, now + Random.Shared.Next(20, 91), target: story.TransitTo);
                else if (result != "Superseded")
                    ToMenu(c, a, now, died: result is "Died" or "DiedUncounted");
                dirty = true;
            }
            catch (Exception ex)
            {
                // One broken story must not block the rest of the checkpoint.
                story.DueUtc = now + 300;
                Report(ex);
            }
        }
    }
}
