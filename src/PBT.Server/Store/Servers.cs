using System.Text.Json;

namespace PBT.Server;

public sealed record WorldServerStatus(string Name, string Region, int Players, int Capacity, double Load, string Status, int Ping);

// What the client needs to fake a connection to the server it matched into.
public sealed record MatchedServer(string Name, string Region, int Ping, int Jitter, double Loss, bool Bad, bool Simulate, bool LossEvents);

// The game servers careers play on. Made once per Data folder, so every install gets its own good and flaky ones.
public sealed partial class PopulationStore
{
    private sealed class GameServer
    {
        public string Name { get; set; } = "";
        public string Region { get; set; } = "";
        // Share of the region's players this server takes.
        public double Weight { get; set; } = 1;
        // How rarely it has connection trouble, 0 to 1.
        public double Stability { get; set; } = .95;
        // Small per-server ping offset, so two EU servers don't read the same.
        public int PingOffset { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public long DegradedUntil;
    }

    public static readonly string[] Regions = ["EU", "US", "Asia"];
    private static readonly Dictionary<string, double> RegionShare = new() { ["EU"] = .45, ["US"] = .35, ["Asia"] = .2 };
    private List<GameServer>? servers;
    private string ServersPath => System.IO.Path.Combine(directory, "servers.json");

    private List<GameServer> Servers()
    {
        if (servers is not null) return servers;
        try { servers = File.Exists(ServersPath) ? JsonSerializer.Deserialize<List<GameServer>>(File.ReadAllText(ServersPath)) : null; }
        catch { }
        if (servers is { Count: > 0 }) return servers;
        GameServer Make(string name, string region, double weight, double low, double high) => new()
        {
            Name = name, Region = region, Weight = weight,
            Stability = Math.Round(low + Random.Shared.NextDouble() * (high - low), 3), PingOffset = Random.Shared.Next(-10, 16)
        };
        servers =
        [
            Make("EU", "EU", 1.2, .93, .99), Make("EU-2", "EU", 1, .9, .98), Make("EU-3", "EU", .8, .82, .95),
            Make("US-1", "US", 1.1, .9, .98), Make("US-2", "US", .9, .84, .96), Make("Asia", "Asia", 1, .8, .93)
        ];
        SaveServers();
        return servers;
    }

    private void SaveServers()
    {
        try { AtomicFile.Write(ServersPath, JsonSerializer.Serialize(servers, new JsonSerializerOptions { WriteIndented = true }), durable: false, backup: false); }
        catch (Exception ex) { logger.Warning("[PBT] Could not save servers: " + ex.Message); }
    }

    private static string RegionOf(string location) => Regions.FirstOrDefault(r => r.Equals(location, StringComparison.OrdinalIgnoreCase)) ?? "EU";

    // Squads share a server, so it follows the party leader.
    private string CareerServer(string id, Activity a)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var lobby in lobbies.Values)
            if (lobby.Server.Length > 0 && now - lobby.Utc < 1800 && lobby.Careers.Contains(id)) return lobby.Server;
        string leader = a.Party.Length > 0 ? a.Party.Min(StringComparer.Ordinal)! : id;
        double roll = Stable(leader + ":region"), region = 0;
        string home = "EU";
        foreach (var (name, share) in RegionShare) { region += share; if (roll < region) { home = name; break; } }
        var list = Servers().Where(s => s.Region == home).ToList();
        double pick = Stable(leader + ":server") * list.Sum(s => s.Weight);
        foreach (var s in list) { pick -= s.Weight; if (pick < 0) return s.Name; }
        return list[^1].Name;
    }

    private Dictionary<string, int> ServerCounts()
    {
        var counts = new Dictionary<string, int>();
        foreach (var (id, a) in activities)
        {
            if (a.State is not ("Queue" or "Raid" or "Transit") || !population.Careers.TryGetValue(id, out var c) || c.Retired) continue;
            string name = CareerServer(id, a);
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }
        return counts;
    }

    // Sized off the account count, so a normal evening runs them around three quarters full.
    private int Capacity(GameServer s)
    {
        int accounts = population.Careers.Values.Count(c => !c.Retired);
        double share = RegionShare.GetValueOrDefault(s.Region) * s.Weight / Servers().Where(o => o.Region == s.Region).Sum(o => o.Weight);
        return Math.Max(40, (int)Math.Ceiling(accounts * share * .7 / 10) * 10);
    }

    private static double Load(int players, int capacity) => capacity <= 0 ? 0 : Math.Round((double)players / capacity, 3);

    private int BasePing(GameServer s)
    {
        string home = RegionOf(policy.World.Location);
        int ping = (home, s.Region) switch
        {
            _ when home == s.Region => 120,
            ("EU", "US") or ("US", "EU") => 175,
            ("US", "Asia") or ("Asia", "US") => 195,
            _ => 260
        };
        return ping + s.PingOffset;
    }

    // Caller holds the gate. Servers have bad spells now and then, the flaky ones more often.
    private void TickServers(long now)
    {
        foreach (var s in Servers())
        {
            if (s.DegradedUntil > 0 && now >= s.DegradedUntil)
            {
                s.DegradedUntil = 0;
                Log("online", $"{s.Name} server is stable again");
                continue;
            }
            if (s.DegradedUntil > 0 || Random.Shared.NextDouble() >= (1 - s.Stability) * 5 / 900) continue;
            s.DegradedUntil = now + Random.Shared.Next(5 * 60, 20 * 60);
            Log("mia", $"{s.Name} server is having connection issues");
        }
    }

    private List<WorldServerStatus> ServerStatuses(long now)
    {
        var counts = ServerCounts();
        return Servers().Select(s =>
        {
            int players = counts.GetValueOrDefault(s.Name), capacity = Capacity(s);
            double load = Load(players, capacity);
            string status = s.DegradedUntil > now ? "Unstable" : load >= .95 ? "Full" : load >= .75 ? "Busy" : "Online";
            return new WorldServerStatus(s.Name, s.Region, players, capacity, Math.Min(1, load), status, BasePing(s));
        }).ToList();
    }

    // Caller holds the gate. Mostly a server near you with room, now and then a far or struggling one.
    private MatchedServer PickServer(long now)
    {
        string home = RegionOf(policy.World.Location);
        var counts = ServerCounts();
        var options = Servers().Select(s =>
        {
            double load = Load(counts.GetValueOrDefault(s.Name), Capacity(s));
            double weight = (s.Region == home ? 1 : .05) * Math.Max(.05, 1.05 - load) * s.Weight;
            return (Server: s, Load: load, Weight: weight);
        }).ToList();
        double roll = Random.Shared.NextDouble() * options.Sum(o => o.Weight);
        var pick = options[^1];
        foreach (var o in options) { roll -= o.Weight; if (roll < 0) { pick = o; break; } }
        var server = pick.Server;
        bool unstable = server.DegradedUntil > now, full = pick.Load >= .95, far = server.Region != home;
        int ping = BasePing(server) + (unstable ? Random.Shared.Next(50, 121) : 0) + (full ? Random.Shared.Next(15, 41) : 0);
        int jitter = unstable ? Random.Shared.Next(25, 61) : far || full ? Random.Shared.Next(12, 26) : Random.Shared.Next(4, 12);
        double loss = unstable ? .35 + Random.Shared.NextDouble() * .3 : full ? .15 : far ? .08 : 0;
        return new(server.Name, server.Region, ping, jitter, Math.Round(loss, 2), unstable || full || far,
            policy.World.SimulateConnection, policy.World.ConnectionLossEvents);
    }
}
