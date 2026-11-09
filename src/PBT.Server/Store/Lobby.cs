using SPTarkov.Server.Core.Models.Common;

namespace PBT.Server;

// The careers queueing for your map when you match. Raid spawns pick them first, and squadmates of a picked career right after it.
public sealed partial class PopulationStore
{
    private sealed record Lobby(string Map, HashSet<string> Careers, long Utc, string Server);

    private sealed record MatchTimeline(string Map, long StartedMs, int Seconds, int Looking);

    private readonly Dictionary<string, Lobby> lobbies = new();
    private readonly Dictionary<string, HashSet<string>> squadNext = new();
    private MatchTimeline? lastMatch;

    // A real queue: the server fills as careers near your level queue for the map.
    private sealed class PendingMatch
    {
        public string Map = "";
        public int Target;
        public long StartedUtc;
        public HashSet<string> Careers = new();
        public MatchedServer Server = null!;
    }

    private readonly Dictionary<string, PendingMatch> pendingMatches = new();
    private readonly HashSet<string> poolNotices = new();

    public (int Seconds, int Looking, int Matched, MatchedServer? Server, bool Wait, int Target) Matching(string map, MongoId session)
    {
        lock (gate)
        {
            if (!Ready) return (0, 0, 0, null, false, 0);
            string key = MapKey(map);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var world = policy.World;
            pendingMatches.Remove(session.ToString());
            if (world.SimulateMatching && world.WaitForPlayers && FindMap(map) is { Solo: false } waitMap)
            {
                var pending = new PendingMatch
                {
                    Map = key,
                    Target = Math.Max(1, Random.Shared.Next(waitMap.MinPlayers, waitMap.MaxPlayers + 1) - 1),
                    StartedUtc = now,
                    Server = PickServer(now)
                };
                pendingMatches[session.ToString()] = pending;
                squadNext.Remove(session.ToString());
                lobbies.Remove(session.ToString());
                poolNotices.RemoveWhere(n => n.StartsWith(session.ToString(), StringComparison.Ordinal));
                FillServer(waitMap, session, now, pending.Careers, pending.Target, fromMenu: false);
                Hold(pending, now);
                lastMatch = new(key, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), world.MaximumWaitSeconds, 0);
                return (0, 0, pending.Careers.Count, pending.Server, true, pending.Target);
            }

            // The minimum wins over the cap, so a set minimum is always honoured.
            int seconds = world.SimulateMatching
                ? Math.Max(world.MinimumMatchingSeconds, Math.Min(world.MaximumMatchingSeconds, (int)(QueueSeconds(map) * (.7 + Random.Shared.NextDouble() * .6))))
                : 0;
            int looking = seconds > 0 ? Random.Shared.Next(5, 16) : 0;
            var server = PickServer(now);
            var queued = FindMap(map) is { } found ? FillServer(found, session, now) : [];
            foreach (string id in queued) activities[id].Until = Math.Max(activities[id].Until, now + seconds + looking + 120);
            lobbies[session.ToString()] = new(key, queued, now, server.Name);
            squadNext.Remove(session.ToString());
            poolNotices.RemoveWhere(n => n.StartsWith(session.ToString(), StringComparison.Ordinal));
            if (queued.Count > 0) logger.Info($"[PBT] Matched {key} on {server.Name}: {queued.Count} PBT careers in the lobby. They spawn first, wearing their PBT gear.");
            lastMatch = seconds > 0 ? new(key, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), seconds, looking) : null;
            return (seconds, looking, queued.Count, server, false, 0);
        }
    }

    // Polled by the client every couple of seconds while it shows Matching.
    public (int Found, int Target, bool Done, int Looking, long ElapsedSeconds) PollMatching(MongoId session)
    {
        lock (gate)
        {
            string sid = session.ToString();
            if (!Ready || !pendingMatches.TryGetValue(sid, out var pending) || FindMap(pending.Map) is not { } map) return (0, 0, true, 0, 0);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long elapsed = now - pending.StartedUtc;
            FillServer(map, session, now, pending.Careers, pending.Target, fromMenu: false);
            bool full = pending.Careers.Count >= pending.Target;
            bool timedOut = elapsed >= policy.World.MaximumWaitSeconds;
            if (timedOut && !full) FillServer(map, session, now, pending.Careers, pending.Target, fromMenu: true);
            Hold(pending, now);
            if (!(full || timedOut) || elapsed < policy.World.MinimumMatchingSeconds) return (pending.Careers.Count, pending.Target, false, 0, elapsed);
            pendingMatches.Remove(sid);
            int looking = Random.Shared.Next(5, 16);
            foreach (string id in pending.Careers)
                if (activities.TryGetValue(id, out var a))
                    a.Until = Math.Max(a.Until, now + looking + 120);
            lobbies[sid] = new(pending.Map, pending.Careers, now, pending.Server.Name);
            logger.Info($"[PBT] Matched {pending.Map} on {pending.Server.Name} after {elapsed}s: {pending.Careers.Count} PBT careers in the lobby"
                        + (timedOut && !full ? " (gave up waiting, filled from the menu)." : "."));
            lastMatch = new(pending.Map, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - elapsed * 1000, (int)elapsed, looking);
            return (pending.Careers.Count, pending.Target, true, looking, elapsed);
        }
    }

    // Matched careers stay in the queue with you instead of starting their own raid.
    private void Hold(PendingMatch pending, long now)
    {
        foreach (string id in pending.Careers)
            if (activities.TryGetValue(id, out var a) && a.State == "Queue")
                a.Until = Math.Max(a.Until, now + 120);
    }

    // Like EFT, a server of MinPlayers..MaxPlayers. Its players are the careers spawns draw from.
    private HashSet<string> FillServer(WorldMap map, MongoId session, long now, HashSet<string>? into = null, int? target = null, bool fromMenu = true)
    {
        if (map.Solo) return into ?? [];
        int size = target ?? Math.Max(0, Random.Shared.Next(map.MinPlayers, map.MaxPlayers + 1) - 1);
        int level = 1;
        try
        {
            level = saves.GetProfile(session).CharacterData?.PmcData?.Info?.Level ?? 1;
        }
        catch
        {
        }

        bool Fits(Career c) => Math.Abs(Level(c.Experience) - level) <= 20;
        var server = into ?? new HashSet<string>();
        // Waiting for players only takes careers near your level; the timer mode takes anyone queued.
        var queued = population.Careers.Values.Where(c => !c.Retired && !server.Contains(c.Id) && activities.TryGetValue(c.Id, out var a) && a.State == "Queue"
                                                          && a.Map == map.Id && !a.Held && (fromMenu || into is null || Fits(c)))
            .OrderByDescending(Fits).ThenBy(_ => Random.Shared.Next()).ToList();

        // Parties stay whole, even if that overfills the server by a seat or two.
        void Take(Career c)
        {
            if (!server.Add(c.Id)) return;
            foreach (string id in activities[c.Id].Party)
                if (id != c.Id && activities.TryGetValue(id, out var mate) && mate.State == "Queue" && mate.Map == map.Id)
                    server.Add(id);
        }

        foreach (var c in queued)
        {
            if (server.Count >= size) break;
            Take(c);
        }

        if (server.Count >= size || !fromMenu) return server;
        // A quiet queue pulls in players from the menu who were about to pick this map anyway.
        var idle = population.Careers.Values
            .Where(c => !c.Retired && c.IsOnline && !server.Contains(c.Id) && Fits(c) && activities.TryGetValue(c.Id, out var a) && a.State == "Menu" && !a.Held && !a.WantsOffline
                        && a.Doing is not ("AFK" or "Waiting for squad") && !WaitingForGear(c, now))
            .Select(c => (Career: c, Weight: MapWeight(c, map, Level(c.Experience)) * Random.Shared.NextDouble())).Where(x => x.Weight > 0).OrderByDescending(x => x.Weight)
            .Select(x => x.Career).ToList();
        foreach (var c in idle)
        {
            if (server.Count >= size) break;
            if (server.Contains(c.Id)) continue;
            var party = new List<Career>
            {
                c
            };
            if (squads.TryGetValue(c.Id, out var members))
                foreach (string id in members)
                    if (id != c.Id && Available(id, out var mate, out var mateActivity) && mateActivity.Doing != "AFK" && Fits(mate))
                        party.Add(mate);
            if (party.Any(m => Level(m.Experience) < map.MinimumLevel || Level(m.Experience) > map.MaximumLevel)) party = [c];
            string[] ids = party.Count > 1 ? party.Select(m => m.Id).ToArray() : [];
            foreach (var m in party)
            {
                RebuyBeforeRaid(m, now);
                Set(ActivityOf(m), "Queue", map.Id, now, now, party: ids);
                server.Add(m.Id);
            }

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

    // Caller holds the gate. Logs each pool running dry once per raid.
    private void PoolNotice(string session, string kind, string message)
    {
        if (poolNotices.Add(session + ":" + kind)) logger.Info("[PBT] " + message);
    }

    // Caller holds the gate.
    private double MatchBoost(string session, string map, string id) =>
        lobbies.TryGetValue(session, out var lobby) && lobby.Map == map && lobby.Careers.Contains(id) && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - lobby.Utc < 1800 ? 10 : 1;

    // Bots leave the generation cache in order, so squadmates generated back to back tend to spawn in the same group.
    private void QueueSquadmates(string session, Career picked, HashSet<string> used)
    {
        if (!squadNext.TryGetValue(session, out var next)) squadNext[session] = next = new();
        next.Remove(picked.Id);
        if (squads.TryGetValue(picked.Id, out var members))
            foreach (string id in members)
                if (!used.Contains(id))
                    next.Add(id);
    }

    private void ClearLobby(string session)
    {
        pendingMatches.Remove(session);
        lobbies.Remove(session);
        squadNext.Remove(session);
        poolNotices.RemoveWhere(n => n.StartsWith(session, StringComparison.Ordinal));
    }
}
