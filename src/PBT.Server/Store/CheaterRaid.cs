using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;

namespace PBT.Server;

public sealed class CheaterRosterRequest : IRequestData
{
    public string RaidId { get; set; } = "";
}

public sealed class CheaterSeenRequest : IRequestData
{
    public string RaidId { get; set; } = "";
    public int AccountId { get; set; }

    // Spawned, Died, Banned or Extracted.
    public string State { get; set; } = "";
    public int Looted { get; set; }
}

// One cheater as the client gets it: its account alias, type and the cheats it runs.
public sealed record RaidCheater(int AccountId, string Type, List<string> Cheats, double AimAccuracy, double ReactionTime, double SpeedMultiplier);

// One cheater of the current or last raid, for the editor.
public sealed class CheaterSighting
{
    public int AccountId { get; init; }
    public string CareerId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Side { get; init; } = "";
    public int Level { get; init; }
    public string Style { get; init; } = "";
    public string Type { get; init; } = "";
    public List<string> Cheats { get; init; } = [];
    public bool Debug { get; init; }
    public string State { get; set; } = "Generated";
    public long SpawnedUtc { get; set; }
    public long UpdatedUtc { get; set; }
    public int Looted { get; set; }

    public CheaterSighting Copy() => (CheaterSighting)MemberwiseClone();
}

public sealed record CheaterOverview(
    int Active,
    int Blatant,
    int Closet,
    int Undercover,
    Dictionary<string, int> ByType,
    string Map,
    long RaidStartedUtc,
    List<CheaterSighting> Raid,
    Dictionary<string, CheaterPersonality> Effective
);

public sealed partial class PopulationStore
{
    // The raid the client last asked about. Kept after the raid so the editor can still show who was in it.
    private string rosterRaid = "";
    private string rosterMap = "";
    private long rosterStartedUtc;
    private readonly Dictionary<int, CheaterSighting> sightings = new();

    private string StyleName(Career c) => ProfileStyle(c).ToString();

    private (string Type, List<string> Cheats) CheaterRoll(Career c) =>
        CheaterRules.Roll(c.Id, policy.Cheaters.Play, Stable);

    // Account aliases of this raid's cheaters with the type and cheats the server rolled, so the client runs exactly that.
    public (bool Behaviour, List<RaidCheater> Cheaters, CheaterPlaySettings Play, Dictionary<string, CheaterPersonality> Table) RaidCheaters(
        MongoId session,
        string raidId
    )
    {
        lock (gate)
        {
            var play = policy.Cheaters.Play;
            var table = CheaterRules.EffectiveAll(play);
            if (!Ready || !policy.Cheaters.Enabled)
                return (false, [], play.Clone(), table);
            if (raidId.Length > 0 && raidId != rosterRaid)
            {
                rosterRaid = raidId;
                rosterMap = "";
                rosterStartedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                sightings.Clear();
            }

            var list = new List<RaidCheater>();
            foreach (var e in encounters.Values)
            {
                if (!CanAccess(e, session) || !population.Careers.TryGetValue(e.CareerId, out var c) || !RaidCheater(c))
                    continue;
                var (type, cheats) = CheaterRoll(c);
                var p = table[type];
                list.Add(new RaidCheater(e.AccountId, type, cheats, p.AimAccuracy, p.ReactionTime, p.SpeedMultiplier));
                if (raidId.Length == 0 || sightings.ContainsKey(e.AccountId))
                    continue;
                if (rosterMap.Length == 0 && e.Map.Length > 0)
                    rosterMap = MapLabel(e.Map);
                sightings[e.AccountId] = new CheaterSighting
                {
                    AccountId = e.AccountId,
                    CareerId = c.Id,
                    Name = c.Name,
                    Side = c.Side,
                    Level = Level(c.Experience),
                    Style = IsCheater(c) ? StyleName(c) : "Borrowed",
                    Type = type,
                    Cheats = cheats,
                    Debug = !IsCheater(c),
                    UpdatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                };
            }

            return (policy.Cheaters.Behaviour, list, play.Clone(), table);
        }
    }

    // The client saw a cheater spawn, die, get banned or extract.
    public void CheaterSeen(CheaterSeenRequest request)
    {
        lock (gate)
        {
            if (request.RaidId != rosterRaid || !sightings.TryGetValue(request.AccountId, out var s))
                return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (request.State is "Spawned" or "Died" or "Banned" or "Extracted")
                s.State = request.State;
            if (request.State == "Spawned" && s.SpawnedUtc == 0)
                s.SpawnedUtc = now;
            s.Looted = Math.Max(s.Looted, request.Looted);
            s.UpdatedUtc = now;
        }
    }

    public CheaterOverview GetCheaterOverview()
    {
        lock (gate)
        {
            var play = policy.Cheaters.Play;
            var effective = CheaterRules.EffectiveAll(play);
            if (!Ready)
                return new CheaterOverview(0, 0, 0, 0, [], "", 0, [], effective);
            var active = population.Careers.Values.Where(c => !c.Retired && IsCheater(c)).ToList();
            var byType = CheaterRules.Types.ToDictionary(t => t, _ => 0);
            foreach (var c in active)
                byType[CheaterRoll(c).Type]++;
            var raid = sightings
                .Values.OrderBy(s => s.State == "Generated" ? 1 : 0)
                .ThenBy(s => s.SpawnedUtc == 0 ? long.MaxValue : s.SpawnedUtc)
                .ThenBy(s => s.Name)
                .Select(s => s.Copy())
                .ToList();
            return new CheaterOverview(
                active.Count,
                active.Count(Blatant),
                active.Count(Closet),
                active.Count(c => ProfileStyle(c) == CheaterProfile.Undercover),
                byType,
                rosterMap,
                rosterStartedUtc,
                raid,
                effective
            );
        }
    }
}
