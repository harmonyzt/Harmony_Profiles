namespace PBT.Server;

// Downtime between raids, and friends who play together in squads of up to four.
public sealed partial class PopulationStore
{
    private static readonly (string Doing, int Minimum, int Maximum, double Weight)[] Errands =
    [
        ("Sorting the stash", 60, 240, 3), ("Gearing up", 90, 360, 3), ("Trading", 60, 300, 2), ("Selling on the flea", 90, 360, 2),
        ("In the hideout", 60, 240, 2), ("Checking tasks", 45, 180, 1.5), ("AFK", 600, 2400, .6)
    ];

    private readonly Dictionary<string, string[]> squads = new();

    // About a third of careers have a steady squad, from stable rolls so restarts keep it.
    // Squads keep to one side so they can spawn as one group in your raid.
    private void FormSquads()
    {
        squads.Clear();
        foreach (var side in population.Careers.Values.Where(c => !c.Retired && Stable(c.Id + ":squad") < .3).GroupBy(c => c.Side.ToLowerInvariant()))
        {
            var pool = side.OrderBy(c => Stable(c.Id + ":squad-order")).ToList();
            for (int i = 0; pool.Count - i >= 2;)
            {
                int size = Math.Min(pool.Count - i, 2 + (int)(Stable(pool[i].Id + ":squad-size") * 3));
                var members = pool.GetRange(i, size).Select(c => c.Id).ToArray();
                foreach (string id in members) squads[id] = members;
                i += size;
            }
        }
    }

    private string[] SquadNames(Career c) => squads.TryGetValue(c.Id, out var members)
        ? members.Where(id => id != c.Id && population.Careers.ContainsKey(id)).Select(id => population.Careers[id].Name).ToArray() : [];

    private string[] TeamNames(Career c, Activity a) => a.Party.Length < 2 ? []
        : a.Party.Where(id => id != c.Id && population.Careers.ContainsKey(id)).Select(id => population.Careers[id].Name).ToArray();

    private void EnterMenu(Career c, Activity a, long now, bool died = false)
    {
        Set(a, "Menu", "", now, now);
        if (died || WaitingForGear(c, now))
        {
            a.Doing = "Gearing up";
            a.Until = now + Random.Shared.Next(180, 601);
            return;
        }
        NextErrand(c, a, now);
    }

    private void NextErrand(Career c, Activity a, long now)
    {
        double afk = c.Archetype switch { "Casual" => 1.6, "Aggressive" => .5, _ => 1 };
        double total = Errands.Sum(e => e.Doing == "AFK" ? e.Weight * afk : e.Weight), roll = Random.Shared.NextDouble() * total;
        var pick = Errands[0];
        foreach (var errand in Errands)
        {
            roll -= errand.Doing == "AFK" ? errand.Weight * afk : errand.Weight;
            if (roll <= 0) { pick = errand; break; }
        }
        a.Doing = pick.Doing;
        a.Until = now + Random.Shared.Next(pick.Minimum, pick.Maximum + 1);
    }

    private bool Available(string id, out Career mate, out Activity mateActivity)
    {
        mateActivity = null!;
        if (!population.Careers.TryGetValue(id, out mate!) || mate.Retired || !mate.IsOnline || WaitingForGear(mate, DateTimeOffset.UtcNow.ToUnixTimeSeconds())) return false;
        mateActivity = ActivityOf(mate);
        return mateActivity.State == "Menu" && !mateActivity.Held && !mateActivity.WantsOffline;
    }

    private bool TryQueueSquad(Career c, Activity a, long now)
    {
        if (!squads.TryGetValue(c.Id, out var members)) return false;
        var ready = new List<(Career Career, Activity Activity)> { (c, a) };
        long waitUntil = 0;
        foreach (string id in members)
        {
            if (id == c.Id || !Available(id, out var mate, out var mateActivity) || mateActivity.Doing == "AFK") continue;
            if (mateActivity.Until > now + 300) continue;
            ready.Add((mate, mateActivity));
            waitUntil = Math.Max(waitUntil, mateActivity.Until);
        }
        if (ready.Count < 2) return false;
        if (waitUntil > now && a.Doing != "Waiting for squad")
        {
            a.Doing = "Waiting for squad";
            a.Until = waitUntil + 5;
            return true;
        }
        int[] levels = ready.Select(p => Level(p.Career.Experience)).ToArray();
        var map = PickMap(c);
        if (map is null || levels.Any(level => level < map.MinimumLevel || level > map.MaximumLevel)) return false;
        string[] party = ready.Select(p => p.Career.Id).ToArray();
        long until = now + QueueSeconds(map.Id);
        foreach (var (_, activity) in ready) Set(activity, "Queue", map.Id, now, until, party: party);
        Log("squad", $"{JoinNames(ready.Select(p => p.Career.Name).ToList())} queued for {map.Name} as a squad", map.Id);
        return true;
    }

    private void StartRaid(Career c, Activity a, long now)
    {
        long until = now + RaidLength(c);
        string[] party = a.Party;
        Set(a, "Raid", a.Map, now, until, party: party);
        foreach (string id in party)
        {
            if (id == c.Id || !activities.TryGetValue(id, out var mate) || mate.State != "Queue" || mate.Map != a.Map) continue;
            Set(mate, "Raid", a.Map, now, until, party: party);
        }
    }

    private void BringSquadOnline(Career c, long now)
    {
        if (!squads.TryGetValue(c.Id, out var members)) return;
        foreach (string id in members)
        {
            if (id == c.Id || !population.Careers.TryGetValue(id, out var mate) || mate.Retired || mate.DailyRaidHours <= 0) continue;
            var mateActivity = ActivityOf(mate);
            if (mateActivity.State == "Offline" && Random.Shared.NextDouble() < .4) GoOnline(mate, mateActivity, now, withSquad: false);
        }
    }

    private static string JoinNames(List<string> names) =>
        names.Count <= 1 ? string.Concat(names) : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
}
