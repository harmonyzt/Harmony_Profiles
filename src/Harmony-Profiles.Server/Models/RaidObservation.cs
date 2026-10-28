using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;

namespace HarmonyProfiles.Server;

public sealed record RaidObservation
{
    public int AccountId { get; set; }
    public string RuntimeProfileId { get; set; } = "";
    public string RaidId { get; set; } = "";
    public int ActiveSeconds { get; set; }
    public bool Died { get; set; }
    public bool Finished { get; set; }
    public long LastReportUtc { get; set; }
    public int PmcKills { get; set; }
    public int ScavKills { get; set; }
    public int UsecKills { get; set; }
    public int BearKills { get; set; }
}

public sealed record ObservedCareer
{
    public long Raids { get; set; }
    public long Deaths { get; set; }
    public long PmcKills { get; set; }
    public long ScavKills { get; set; }
    public long UsecKills { get; set; }
    public long BearKills { get; set; }
    public long ActiveSeconds { get; set; }
    public long PendingSimulationSeconds { get; set; }
}
