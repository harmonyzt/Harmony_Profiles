namespace PBT.Server;

// What every online career is doing right now: main menu, queueing, in raid or in transit.
// Online careers earn their raids here one at a time, so profiles, maps and the activity log agree.
public sealed partial class PopulationStore
{
    private sealed class Activity
    {
        public string State = "Offline";
        public string Map = "";
        public string Target = "";
        public long Since;
        public long Until;
        // In your raid or waiting on a post-raid story. Stories and observations move it, not the world tick.
        public bool Held;
        public bool WantsOffline;
        // What a career in the menu is busy with, e.g. Selling on the flea or AFK.
        public string Doing = "";
        // Careers queueing and playing together, including this one.
        public string[] Party = [];
    }

    // Solo maps run a private instance for you and your team, with no random PMCs.
    private sealed record WorldMap(string Id, string Name, double Weight, string[] Transits, int MinimumLevel, int MaximumLevel, bool WeeklyBoss, bool Solo, int MinPlayers, int MaxPlayers);

    private static readonly Dictionary<string, double> MapPopularity = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bigmap"] = 16, ["woods"] = 13, ["interchange"] = 11, ["tarkovstreets"] = 11, ["shoreline"] = 9, ["rezervbase"] = 9,
        ["factory4_day"] = 8, ["lighthouse"] = 7, ["sandbox"] = 7, ["sandbox_high"] = 6, ["laboratory"] = 5, ["factory4_night"] = 3, ["labyrinth"] = 2
    };
    private static readonly HashSet<string> HiddenMaps = new(StringComparer.OrdinalIgnoreCase) { "hideout", "develop", "privatearea", "suburbs", "terminal", "town" };
    // Same map in the game's selector, but separate lobbies with their own players.
    private static readonly Dictionary<string, string> SplitMapNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["factory4_day"] = "Factory (Day)", ["factory4_night"] = "Factory (Night)", ["sandbox"] = "Ground Zero", ["sandbox_high"] = "Ground Zero 21+"
    };
    private static readonly HashSet<string> SoloMaps = new(StringComparer.OrdinalIgnoreCase) { "icebreaker" };
    private static readonly HashSet<string> StreamerMaps = new(StringComparer.OrdinalIgnoreCase) { "laboratory", "tarkovstreets", "bigmap" };
    // Name fragments that tie a player to a map. "lab" alone would match too many unrelated names.
    private static readonly Dictionary<string, string[]> MapNameWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["factory4_day"] = ["factory"], ["factory4_night"] = ["factory"], ["bigmap"] = ["customs"], ["woods"] = ["woods"],
        ["interchange"] = ["interchange"], ["tarkovstreets"] = ["streets"], ["shoreline"] = ["shoreline"], ["rezervbase"] = ["reserve", "rezerv"],
        ["lighthouse"] = ["lighthouse"], ["sandbox"] = ["groundzero"], ["sandbox_high"] = ["groundzero"], ["laboratory"] = ["labs", "laboratory"],
        ["labyrinth"] = ["labyrinth"], ["icebreaker"] = ["icebreaker"]
    };
    // Bosses that are normally a gamble. One of them at 100% is a weekly boss.
    private static readonly HashSet<string> HeadlineBosses = new(StringComparer.OrdinalIgnoreCase)
        { "bossBully", "bossKilla", "bossKojaniy", "bossGluhar", "bossSanitar", "bossTagilla", "bossKnight", "bossBoar", "bossKolontay", "bossPartisan" };

    private const int LogSize = 300;
    private const int HistoryPoints = 90;
    private readonly Dictionary<string, Activity> activities = new();
    private List<WorldMap> worldMaps = [];
    private long mapsRefreshedUtc;
    private readonly LinkedList<WorldEvent> worldLog = new();
    private long worldEventId;
    private readonly Dictionary<string, Queue<int>> mapHistory = new();
    private long historyUtc;
    private Timer? worldTimer;
    private long worldErrorUtc;

    private Activity ActivityOf(Career c)
    {
        if (!activities.TryGetValue(c.Id, out var a)) activities.Add(c.Id, a = new());
        return a;
    }

    private static string MapKey(string? map) => (map ?? "").Trim().ToLowerInvariant();

    private WorldMap? FindMap(string? id) => worldMaps.FirstOrDefault(m => m.Id == MapKey(id));

    private string MapLabel(string? id) => FindMap(id)?.Name ?? MapName(id);

    private void RefreshMaps(long now)
    {
        if (worldMaps.Count > 0 && now - mapsRefreshedUtc < 600) return;
        mapsRefreshedUtc = now;
        Dictionary<string, string> names;
        try { names = locales.GetLocaleDb(); }
        catch { names = new(); }
        var next = new List<WorldMap>();
        foreach (var location in locations.GetDictionary().Values)
        {
            var b = location?.Base;
            if (b is null || string.IsNullOrEmpty(b.Id) || b.Locked == true || HiddenMaps.Contains(b.Id)) continue;
            string id = MapKey(b.Id);
            // Night Factory is disabled in the database but picked through Factory's time selector.
            if (!b.Enabled && id != "factory4_night") continue;
            if (next.Any(m => m.Id == id)) continue;
            bool solo = SoloMaps.Contains(id) || (b.BotMax == 0 && (b.Waves is null || b.Waves.Count == 0));
            bool weekly = !solo && b.BossLocationSpawn?.Any(s => s.BossChance >= 100 && s.BossName is { } boss && HeadlineBosses.Contains(boss)) == true;
            string name = SplitMapNames.GetValueOrDefault(id) ?? names.GetValueOrDefault(b.Id) ?? names.GetValueOrDefault(b.IdField + " Name") ?? b.Name ?? b.Id;
            var transits = (b.Transits ?? []).Where(t => t.IsActive != false && !string.IsNullOrEmpty(t.Location))
                .Select(t => MapKey(t.Location)).Where(t => t != id).Distinct().ToArray();
            int minimum = b.RequiredPlayerLevelMin ?? 0, maximum = b.RequiredPlayerLevelMax is > 0 ? b.RequiredPlayerLevelMax.Value : 100;
            // The game sends level 21+ to the high Ground Zero, but its database entry has no lower gate.
            if (id == "sandbox_high") minimum = Math.Max(minimum, 21);
            int minPlayers = Math.Max(1, b.MinPlayers ?? 8), maxPlayers = Math.Max(minPlayers, b.MaxPlayers ?? minPlayers);
            next.Add(new(id, name, MapPopularity.GetValueOrDefault(id, solo ? 3 : 5), transits, minimum, maximum, weekly, solo, minPlayers, maxPlayers));
        }
        if (next.Count > 0) worldMaps = next;
    }

    private double MapWeight(Career c, WorldMap map, int level)
    {
        if (level < map.MinimumLevel || level > map.MaximumLevel) return 0;
        double weight = map.Weight * (map.WeeklyBoss ? policy.World.WeeklyBossBoost : 1);
        // Labs needs a keycard, so low levels almost never go.
        if (map.Id == "laboratory" && level < 20) weight *= .1;
        if (Notable(c) && StreamerMaps.Contains(map.Id)) weight *= policy.World.StreamerMapBoost;
        if (NamedForMap(c, map.Id)) weight *= policy.World.NameMapBoost;
        weight *= c.Archetype switch
        {
            "Timmy" => map.Id is "factory4_day" or "bigmap" or "woods" or "sandbox" ? 1.4 : map.Id == "laboratory" ? .15 : 1,
            "Aggressive" => map.Id is "factory4_day" or "factory4_night" or "interchange" or "tarkovstreets" ? 1.4 : 1,
            "Survivor" => map.Id is "woods" or "shoreline" or "lighthouse" or "rezervbase" ? 1.3 : 1,
            _ => 1
        };
        // Everyone has a couple of favourite maps.
        return weight * (.6 + Stable(c.Id + ":map:" + map.Id) * .8);
    }

    private WorldMap? PickMap(Career c)
    {
        int level = Level(c.Experience);
        var weights = worldMaps.Select(m => (Map: m, Weight: MapWeight(c, m, level))).Where(x => x.Weight > 0).ToArray();
        double total = weights.Sum(x => x.Weight);
        if (weights.Length == 0 || total <= 0) return null;
        double roll = Random.Shared.NextDouble() * total;
        foreach (var (map, weight) in weights) { roll -= weight; if (roll <= 0) return map; }
        return weights[^1].Map;
    }

    // Same rules as picking a map, so a transit never lands a low level on Labs or past a level cap.
    private IEnumerable<WorldMap> TransitTargets(Career c, string? from)
    {
        int level = Level(c.Experience);
        return (FindMap(from)?.Transits ?? []).Select(FindMap).OfType<WorldMap>()
            .Where(m => level >= m.MinimumLevel && level <= m.MaximumLevel && !(m.Id == "laboratory" && level < 20));
    }

    private Dictionary<string, int> PlayingCounts() =>
        activities.Values.Where(a => a.State is "Raid" or "Queue").GroupBy(a => a.Map).ToDictionary(g => g.Key, g => g.Count());

    private HashSet<string> ObservedActiveCareers(long now) => population.RaidObservations.Values
        .Where(o => !o.Finished && now - o.LastReportUtc < 180 && encounters.ContainsKey(o.AccountId))
        .Select(o => encounters[o.AccountId].CareerId).ToHashSet();

    // Busy maps fill a lobby fast. Quiet ones keep you waiting.
    private int QueueSeconds(string map, Dictionary<string, int>? counts = null)
    {
        // No lobby to fill on a solo map, so the match is found almost at once.
        if (FindMap(map)?.Solo == true) return (int)Math.Clamp(5 * policy.World.QueueTimeMultiplier, 3, 600);
        var playing = counts ?? PlayingCounts();
        double average = worldMaps.Count == 0 ? 1 : (double)playing.Where(p => worldMaps.Any(m => m.Id == p.Key)).Sum(p => p.Value) / worldMaps.Count;
        double ratio = (playing.GetValueOrDefault(MapKey(map)) + 1) / (average + 1);
        double seconds = 45 / Math.Pow(ratio, .6) * policy.World.QueueTimeMultiplier;
        return (int)Math.Clamp(seconds, 8, 600);
    }

    private void Log(string kind, string text, string map = "")
    {
        worldLog.AddFirst(new WorldEvent(++worldEventId, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), kind, text, map));
        while (worldLog.Count > LogSize) worldLog.RemoveLast();
    }

    private void InitializeWorld(long now)
    {
        RefreshMaps(now);
        var held = population.PendingStories.Values.GroupBy(s => s.CareerId)
            .ToDictionary(g => g.Key, g => g.MaxBy(s => s.DueUtc)!, StringComparer.Ordinal);
        foreach (var c in population.Careers.Values.Where(c => !c.Retired))
        {
            var a = ActivityOf(c);
            if (held.TryGetValue(c.Id, out var story))
            {
                c.IsOnline = true;
                Set(a, "Raid", story.Map, now, story.DueUtc + 60, held: true);
                continue;
            }
            if (!c.IsOnline) continue;
            // Start mid-session instead of everyone in the menu at once.
            double roll = Random.Shared.NextDouble();
            var map = PickMap(c);
            if (map is null || roll < .2) EnterMenu(c, a, now);
            else if (roll < .32) Set(a, "Queue", map.Id, now, now + (long)(Random.Shared.NextDouble() * QueueSeconds(map.Id)) + 5);
            else
            {
                long length = RaidLength(c);
                long into = (long)(Random.Shared.NextDouble() * length);
                Set(a, "Raid", map.Id, now - into, now + Math.Max(30, length - into));
            }
        }
        FormSquads();
        worldTimer = new Timer(_ => TickWorld(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private static void Set(Activity a, string state, string map, long since, long until, bool held = false, string target = "", string[]? party = null)
    {
        a.Doing = "";
        a.Party = party ?? [];
        a.State = state;
        a.Map = map;
        a.Since = since;
        a.Until = until;
        a.Held = held;
        a.Target = target;
    }

    private long RaidLength(Career c)
    {
        if (c.TargetRaidSeconds <= 0) c.TargetRaidSeconds = RaidDuration(c, true);
        return (long)Math.Max(120, c.TargetRaidSeconds - c.PendingRaidSeconds + 1);
    }

    private void TickWorld()
    {
        lock (gate)
        {
            if (!Ready) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            try { WorldTick(now); }
            catch (Exception ex)
            {
                if (now - worldErrorUtc < 300) return;
                worldErrorUtc = now;
                logger.Error($"[PBT] World tick failed; retrying. {ex}");
            }
        }
    }

    // Caller holds the gate.
    private void WorldTick(long now)
    {
        RefreshMaps(now);
        TickServers(now);
        HashSet<string>? observed = null, storied = null;
        foreach (var c in population.Careers.Values)
        {
            if (c.Retired) continue;
            var a = ActivityOf(c);
            if (a.State == "Offline" || now < a.Until) continue;
            if (a.Held)
            {
                observed ??= ObservedActiveCareers(now);
                storied ??= population.PendingStories.Values.Select(s => s.CareerId).ToHashSet();
                // A raid that never reported back (no client plugin, crash) releases the career eventually.
                if (!observed.Contains(c.Id) && !storied.Contains(c.Id)) ToMenu(c, a, now);
                else a.Until = now + 60;
                continue;
            }
            switch (a.State)
            {
                case "Menu":
                    if (a.WantsOffline || !c.IsOnline) { GoOffline(c, a, now); break; }
                    // Dead and naked: wait for the regear, and some give up for the day.
                    if (WaitingForGear(c, now))
                    {
                        // Checked every few minutes, so this is about a third giving up per hour of waiting.
                        if (Random.Shared.NextDouble() < .015) GoOffline(c, a, now);
                        else { a.Until = now + Random.Shared.Next(60, 181); a.Doing = "Gearing up"; }
                        break;
                    }
                    // Another errand now and then before the next raid.
                    if (a.Doing is not ("AFK" or "Waiting for squad") && Random.Shared.NextDouble() < .25) { NextErrand(c, a, now); break; }
                    if (TryQueueSquad(c, a, now)) break;
                    if (PickMap(c) is not { } map) { a.Until = now + 120; break; }
                    Set(a, "Queue", map.Id, now, now + QueueSeconds(map.Id));
                    break;
                case "Queue":
                    StartRaid(c, a, now);
                    break;
                case "Transit":
                    Set(a, "Raid", a.Target, now, now + RaidLength(c));
                    break;
                case "Raid":
                    FinishRaid(c, a, now);
                    break;
            }
        }
        if (now - historyUtc >= 60)
        {
            historyUtc = now;
            var counts = PlayingCounts();
            foreach (var map in worldMaps)
            {
                if (!mapHistory.TryGetValue(map.Id, out var points)) mapHistory.Add(map.Id, points = new());
                points.Enqueue(counts.GetValueOrDefault(map.Id));
                while (points.Count > HistoryPoints) points.Dequeue();
            }
        }
    }

    private void FinishRaid(Career c, Activity a, long now)
    {
        int raids = c.Raids, survived = c.Survived, missing = c.MissingRaids, awol = c.AwolRaids, runner = c.RunThroughRaids, kills = c.PmcKills;
        int level = Level(c.Experience), prestige = c.PrestigeLevel;
        double seconds = Math.Max(0, c.TargetRaidSeconds - c.PendingRaidSeconds) + .01;
        ApplySimulation(c, seconds);
        LogProgress(c, level, prestige);
        if (Level(c.Experience) != level || c.PrestigeLevel != prestige) UpdateQuests(c, log: true);
        c.PendingLiveSeconds += seconds;
        c.Achievements = EarnedMilestones(c, now);
        worldDirty = true;
        string map = MapLabel(a.Map), gained = c.PmcKills > kills ? $" with {c.PmcKills - kills} PMC kill{(c.PmcKills - kills == 1 ? "" : "s")}" : "";
        if (c.Raids == raids) { ToMenu(c, a, now); return; }
        if (c.Survived > survived)
        {
            AfterSurvival(c);
            var target = TransitTargets(c, a.Map).ToArray();
            if (target.Length > 0 && Random.Shared.NextDouble() < policy.World.TransitChance)
            {
                var next = target[Random.Shared.Next(target.Length)];
                Log("transit", $"{c.Name} transited from {map} to {next.Name}{gained}", next.Id);
                Set(a, "Transit", a.Map, now, now + Random.Shared.Next(20, 91), target: next.Id);
                return;
            }
            Log("extract", $"{c.Name} extracted from {map}{gained}", a.Map);
        }
        else if (c.MissingRaids > missing) Log("mia", $"{c.Name} went missing in action on {map}", a.Map);
        else if (c.AwolRaids > awol) Log("mia", $"{c.Name} left the raid on {map}", a.Map);
        else if (c.RunThroughRaids > runner) Log("extract", $"{c.Name} ran through {map}", a.Map);
        else
        {
            Log("death", Killed(c, a.Map, map) + gained, a.Map);
            ToMenu(c, a, now, died: true);
            return;
        }
        ToMenu(c, a, now);
    }

    // Every level would flood the log, so only round levels and prestiges.
    private void LogProgress(Career c, int before, int prestige)
    {
        int after = Level(c.Experience);
        if (c.PrestigeLevel > prestige) Log("level", $"{c.Name} reached prestige {c.PrestigeLevel}");
        else if (after > before && (after / 10 > before / 10 || after == 79)) Log("level", $"{c.Name} reached level {after}");
    }

    // Half the deaths name another career on the same map. The log only, no stats change.
    private string Killed(Career c, string mapId, string map)
    {
        if (FindMap(mapId)?.Solo == true) return $"{c.Name} died on {map}";
        var others = population.Careers.Values.Where(o => o.Id != c.Id && activities.TryGetValue(o.Id, out var oa)
            && oa.State == "Raid" && oa.Map == mapId).Take(50).ToArray();
        if (others.Length > 0 && Random.Shared.NextDouble() < .5)
            return $"{c.Name} was killed by {others[Random.Shared.Next(others.Length)].Name} on {map}";
        return $"{c.Name} was killed on {map}";
    }

    private void ToMenu(Career c, Activity a, long now, bool died = false)
    {
        bool wanted = a.WantsOffline;
        EnterMenu(c, a, now, died);
        bool quit = died && AfterDeath(c, now);
        // Near the end of a session one more raid is rarely worth it. Now and then something comes up anyway.
        a.WantsOffline = wanted || quit || now >= c.SessionEndUtc - 900 && !Tilted(c)
            || Random.Shared.NextDouble() < policy.World.LogOffChance * .12;
        c.IsOnline = true;
    }

    private void GoOnline(Career c, Activity a, long now, bool withSquad = true)
    {
        c.IsOnline = true;
        if (c.SessionEndUtc <= now) c.SessionEndUtc = RollSessionEnd(c, now);
        // A night's sleep resets the mood.
        if (now - c.LastOfflineUtc > 4 * 3600) { c.DeathStreak = 0; c.TiltUntilUtc = 0; }
        EnterMenu(c, a, now);
        a.WantsOffline = false;
        Log("online", $"{c.Name} came online");
        if (withSquad) BringSquadOnline(c, now);
    }

    private void GoOffline(Career c, Activity a, long now)
    {
        c.IsOnline = false;
        c.SessionEndUtc = Math.Min(c.SessionEndUtc, now);
        c.LastOfflineUtc = now;
        Set(a, "Offline", "", now, 0);
        a.WantsOffline = false;
        Log("offline", $"{c.Name} logged off");
    }

    // A career picked for your raid stays there until its story or observation moves it on.
    private void HoldInRaid(Career c, string map, long until)
    {
        var a = ActivityOf(c);
        c.IsOnline = true;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (c.SessionEndUtc <= now) c.SessionEndUtc = Math.Max(until, RollSessionEnd(c, now));
        Set(a, "Raid", MapKey(map), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), until, held: true);
    }

    // Preference when APBS spawns a PMC into your raid on this map.
    private double RaidWeight(Career c, string map)
    {
        if (!activities.TryGetValue(c.Id, out var a)) return 1;
        if (a.Held) return 0;
        double weight = a.State switch
        {
            "Queue" => a.Map == map ? 8 : .3,
            "Transit" => a.Target == map ? 8 : .3,
            "Raid" => a.Map == map ? 4 : .3,
            "Menu" => 2,
            _ => 1
        };
        if (Notable(c) && StreamerMaps.Contains(map)) weight *= policy.World.StreamerMapBoost;
        if (NamedForMap(c, map)) weight *= policy.World.NameMapBoost;
        return weight;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> letterNames = new();

    private static bool NamedForMap(Career c, string map)
    {
        if (!MapNameWords.TryGetValue(map, out var words) || string.IsNullOrEmpty(c.Name)) return false;
        // Letters only, so Ground_Zero and ground-zero still match.
        string name = letterNames.GetOrAdd(c.Name, n => new string(n.Where(char.IsLetter).ToArray()));
        return words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private (string State, string Status, string Map, long Since) Describe(Career c, long now, HashSet<string> observed)
    {
        var a = activities.GetValueOrDefault(c.Id);
        if (a is null || c.Retired || a.State == "Offline")
            return now - c.DiedUtc < 1800 && c.NeedsRegear ? ("Died", "", "", c.DiedUtc) : ("Offline", "", "", a?.Since ?? 0);
        // Status is the detail next to the state label in the editor.
        string map = MapLabel(a.Map);
        return a.State switch
        {
            "Queue" => ("Queue", map, a.Map, a.Since),
            "Raid" when a.Held && observed.Contains(c.Id) => ("Raid", map + " · your raid", a.Map, a.Since),
            "Raid" => ("Raid", map, a.Map, a.Since),
            "Transit" => ("Transit", "to " + MapLabel(a.Target), a.Target, a.Since),
            _ when WaitingForGear(c, now) => ("Died", "Gearing up again", "", c.DiedUtc),
            _ when Tilted(c) && a.Doing.Length == 0 => ("Menu", "Tilted", "", a.Since),
            _ when a.Doing == "AFK" => ("Afk", "", "", a.Since),
            _ => ("Menu", a.Doing.Length > 0 ? a.Doing : "Main menu", "", a.Since)
        };
    }

    public WorldSnapshot GetWorld(int events = 40, bool servers = true)
    {
        lock (gate)
        {
            if (!Ready) throw new InvalidOperationException("PBT has not finished loading.");
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var live = activities.Where(p => population.Careers.TryGetValue(p.Key, out var c) && !c.Retired).Select(p => p.Value).ToList();
            int playing = Math.Max(1, live.Count(a => a.State is "Raid" or "Queue"));
            var raids = live.Where(a => a.State == "Raid").GroupBy(a => a.Map).ToDictionary(g => g.Key, g => g.Count());
            var queues = live.Where(a => a.State == "Queue").GroupBy(a => a.Map).ToDictionary(g => g.Key, g => g.Count());
            var counts = PlayingCounts();
            var maps = worldMaps.Select(m =>
            {
                int raid = raids.GetValueOrDefault(m.Id), queue = queues.GetValueOrDefault(m.Id);
                return new WorldMapStatus(m.Id, m.Name, raid, queue, QueueSeconds(m.Id, counts), (raid + queue) / (double)playing, m.WeeklyBoss, m.Solo,
                    mapHistory.TryGetValue(m.Id, out var points) ? points.ToArray() : []);
            }).OrderByDescending(m => m.InRaid + m.Queueing).ThenBy(m => m.Name).ToList();
            return new(live.Count(a => a.State != "Offline"), live.Count(a => a.State == "Raid"), live.Count(a => a.State == "Queue"),
                live.Count(a => a.State == "Menu"), live.Count(a => a.State == "Transit"), population.PendingStories.Count, maps,
                worldLog.Take(Math.Clamp(events, 0, LogSize)).ToList(), servers ? ServerStatuses(now) : []);
        }
    }

    public List<WorldPlayerRow> GetPlayers()
    {
        lock (gate)
        {
            if (!Ready) throw new InvalidOperationException("PBT has not finished loading.");
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var observed = ObservedActiveCareers(now);
            return population.Careers.Values.Where(c => !c.Retired).Select(c => PlayerRow(c, now, observed)).ToList();
        }
    }

    private WorldPlayerRow PlayerRow(Career c, long now, HashSet<string> observed)
    {
        var (state, status, map, since) = Describe(c, now, observed);
        long deaths = Math.Max(1, c.Raids - c.Survived - c.MissingRaids - c.AwolRaids - c.RunThroughRaids + c.Observed.Deaths + c.StoryDeaths);
        return new WorldPlayerRow(c.Name, c.Side, c.Archetype, Level(c.Experience), c.PrestigeLevel, state, status, map,
            since > 0 ? now - since : 0, (int)(c.Raids + c.Observed.Raids), Math.Round((double)(c.PmcKills + c.Observed.PmcKills) / deaths, 2), Notable(c),
            SquadNames(c), activities.TryGetValue(c.Id, out var a) && a.State is "Queue" or "Raid" or "Transit" ? TeamNames(c, a) : [], c.Id);
    }

    public Dictionary<string, object> MapIndicators()
    {
        var world = GetWorld(0, servers: false);
        return world.Maps.ToDictionary(m => m.Id, m => (object)new { m.InRaid, m.Queueing, m.Share, m.QueueSeconds, m.WeeklyBoss, m.Solo });
    }
}
