using SPTarkov.Server.Core.Models.Common;

namespace PBT.Server;

// The careers queueing for your map when you match. Raid spawns pick them first, and squadmates of a picked career right after it.
public sealed partial class PopulationStore
{
    private sealed record Lobby(string Map, HashSet<string> Careers, long Utc);
    private sealed record MatchTimeline(string Map, long StartedMs, int Seconds, int Looking);

    private readonly Dictionary<string, Lobby> lobbies = new();
    private readonly Dictionary<string, HashSet<string>> squadNext = new();
    private MatchTimeline? lastMatch;

    public (int Seconds, int Looking, int Matched) Matching(string map, MongoId session)
    {
        lock (gate)
        {
            if (!Ready) return (0, 0, 0);
            string key = MapKey(map);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int seconds = policy.World.SimulateMatching
                ? Math.Min(policy.World.MaximumMatchingSeconds, (int)(QueueSeconds(map) * (.7 + Random.Shared.NextDouble() * .6))) : 0;
            int looking = seconds > 0 ? Random.Shared.Next(5, 16) : 0;
            var queued = FillServer(FindMap(map), session, now);
            // They wait in the queue with you until the raid loads.
            foreach (string id in queued) activities[id].Until = Math.Max(activities[id].Until, now + seconds + looking + 120);
            lobbies[session.ToString()] = new(key, queued, now);
            squadNext.Remove(session.ToString());
            lastMatch = seconds > 0 ? new(key, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), seconds, looking) : null;
            return (seconds, looking, queued.Count);
        }
    }

    // Like EFT, you match into a server of MinPlayers..MaxPlayers. Its players are the careers spawns draw from,
    // so a raid meets the people it queued with. PMC count is still up to the bot spawner.
    private HashSet<string> FillServer(WorldMap? map, MongoId session, long now)
    {
        // Solo maps never put strangers in your instance.
        if (map is null || map.Solo) return [];
        int size = Math.Max(0, Random.Shared.Next(map.MinPlayers, map.MaxPlayers + 1) - 1);
        int level = 1;
        try { level = saves.GetProfile(session).CharacterData?.PmcData?.Info?.Level ?? 1; }
        catch { }
        bool Fits(Career c) => Math.Abs(Level(c.Experience) - level) <= 20;
        var queued = population.Careers.Values
            .Where(c => !c.Retired && activities.TryGetValue(c.Id, out var a) && a.State == "Queue" && a.Map == map.Id && !a.Held)
            .OrderByDescending(Fits).ThenBy(_ => Random.Shared.Next()).ToList();
        var server = new HashSet<string>();
        // Parties stay whole, even if that overfills the server by a seat or two.
        void Take(Career c)
        {
            if (!server.Add(c.Id)) return;
            foreach (string id in activities[c.Id].Party)
                if (id != c.Id && activities.TryGetValue(id, out var mate) && mate.State == "Queue" && mate.Map == map.Id) server.Add(id);
        }
        foreach (var c in queued) { if (server.Count >= size) break; Take(c); }
        if (server.Count >= size) return server;
        // A quiet queue pulls in players from the menu who were about to pick this map anyway.
        var idle = population.Careers.Values.Where(c => !c.Retired && c.IsOnline && !server.Contains(c.Id) && Fits(c)
                && activities.TryGetValue(c.Id, out var a) && a.State == "Menu" && !a.Held && !a.WantsOffline
                && a.Doing is not ("AFK" or "Waiting for squad") && !WaitingForGear(c, now))
            .Select(c => (Career: c, Weight: MapWeight(c, map, Level(c.Experience)) * Random.Shared.NextDouble()))
            .Where(x => x.Weight > 0).OrderByDescending(x => x.Weight).Select(x => x.Career).ToList();
        foreach (var c in idle)
        {
            if (server.Count >= size) break;
            if (server.Contains(c.Id)) continue;
            var party = new List<Career> { c };
            if (squads.TryGetValue(c.Id, out var members))
                foreach (string id in members)
                    if (id != c.Id && Available(id, out var mate, out var mateActivity) && mateActivity.Doing != "AFK" && Fits(mate)) party.Add(mate);
            if (party.Any(m => Level(m.Experience) < map.MinimumLevel || Level(m.Experience) > map.MaximumLevel)) party = [c];
            string[] ids = party.Count > 1 ? party.Select(m => m.Id).ToArray() : [];
            foreach (var m in party) { Set(ActivityOf(m), "Queue", map.Id, now, now, party: ids); server.Add(m.Id); }
            if (party.Count > 1) Log("squad", $"{JoinNames(party.Select(m => m.Name).ToList())} queued for {map.Name} as a squad", map.Id);
        }
        worldDirty = true;
        return server;
    }

    // A Fika client asks where the host's matching is, so its screen follows the same queue.
    public (int Seconds, int Looking, long ElapsedMs) HostMatching(string map)
    {
        lock (gate)
        {
            var match = lastMatch;
            if (match is null || match.Map != MapKey(map)) return (0, 0, 0);
            long elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - match.StartedMs;
            return elapsed > (match.Seconds + match.Looking + 5) * 1000L ? (0, 0, 0) : (match.Seconds, match.Looking, elapsed);
        }
    }

    public int RaidCareers(MongoId session)
    {
        lock (gate) return reservedCareers.TryGetValue(session.ToString(), out var used) ? used.Count : 0;
    }

    // Caller holds the gate.
    private double MatchBoost(string session, string map, string id) =>
        lobbies.TryGetValue(session, out var lobby) && lobby.Map == map && lobby.Careers.Contains(id)
        && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - lobby.Utc < 1800 ? 10 : 1;

    // Bots leave the generation cache in order, so squadmates generated back to back tend to spawn in the same group.
    private void QueueSquadmates(string session, Career picked, HashSet<string> used)
    {
        if (!squadNext.TryGetValue(session, out var next)) squadNext[session] = next = new();
        next.Remove(picked.Id);
        if (squads.TryGetValue(picked.Id, out var members))
            foreach (string id in members) if (!used.Contains(id)) next.Add(id);
    }

    private void ClearLobby(string session)
    {
        lobbies.Remove(session);
        squadNext.Remove(session);
    }
}
