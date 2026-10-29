using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;
using System.Reflection;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace PBT.Server;

public sealed class ActivityReport : IRequestData
{
    public int ProtocolVersion { get; set; }
    public string RaidId { get; set; } = "";
    public List<RaidObservation> Bots { get; set; } = [];
}

public sealed class KillerLookup : IRequestData
{
    public string AccountId { get; set; } = "";
    public string RuntimeProfileId { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string Side { get; set; } = "";
}

public sealed class HideoutAccessRequest : IRequestData
{
    public string AccountId { get; set; } = "";
}

public sealed record HideoutAccessDecision(int ProtocolVersion, bool Managed, bool CanEnter, string Reason);

public sealed record EditorSnapshot(string PersonalitiesJson, string PersonalitiesRevision, string PolicyJson,
    string PolicyRevision, string SettingsJson, string SettingsRevision, int Careers, int Online, int PendingStories, int Prestiged, string GenerationMode, long ApbsMatched, long ApbsFallback,
    EditorInsights Insights);

// Dashboard figures. Level bands are 1-10, 11-20 ... 71-79.
public sealed record EditorInsights(int[] LevelBands, int Bear, int Usec, Dictionary<string, int> Personalities,
    List<EditorCareerRow> TopCareers, int RacksFilled, int RackOwners);

public sealed record EditorCareerRow(string Name, string Side, string Personality, int Level, int Prestige, int Raids, double KillDeath, bool Online);
