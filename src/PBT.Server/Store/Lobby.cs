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
            // Solo maps never put strangers in your instance.
            var queued = FindMap(map)?.Solo == true ? [] : population.Careers.Values
                .Where(c => !c.Retired && activities.TryGetValue(c.Id, out var a) && a.State == "Queue" && a.Map == key && !a.Held)
                .Select(c => c.Id).ToHashSet();
            // They wait in the queue with you until the raid loads.
            foreach (string id in queued) activities[id].Until = Math.Max(activities[id].Until, now + seconds + looking + 120);
            lobbies[session.ToString()] = new(key, queued, now);
            squadNext.Remove(session.ToString());
            lastMatch = seconds > 0 ? new(key, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), seconds, looking) : null;
            return (seconds, looking, queued.Count);
        }
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
