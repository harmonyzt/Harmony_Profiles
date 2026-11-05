using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Utils;

namespace PBT.Server;

public sealed class Population
{
    public int SchemaVersion { get; set; } = 12;
    public string TemplateFingerprint { get; set; } = "";
    public int CoverageVersion { get; set; }
    public Dictionary<int, string> AppliedStories { get; set; } = new();
    public Dictionary<string, long> SessionEndedUtc { get; set; } = new();
    public long LastSimulationUtc { get; set; }
    public long LastGenerationOrder { get; set; }
    public Dictionary<int, RaidObservation> RaidObservations { get; set; } = new();
    // Raid PMCs whose outcome after your raid is still to come, keyed by encounter alias.
    public Dictionary<int, PendingStory> PendingStories { get; set; } = new();
    public Dictionary<string, Career> Careers { get; set; } = new();
    public Dictionary<string, Dictionary<string, Friendship>> Friendships { get; set; } = new();
    public Dictionary<string, CheaterReport> CheaterReports { get; set; } = new();
    // Last death per player session, so a report can hand back what that raid cost.
    public Dictionary<string, RaidLoss> RaidLosses { get; set; } = new();
}

public sealed class CheaterReport
{
    public string CareerId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public long ReportedUtc { get; set; }
    public long DueUtc { get; set; }
    public JsonArray LostGear { get; set; } = new();
}

public sealed class RaidLoss
{
    public int KillerAccountId { get; set; }
    public long Utc { get; set; }
    public JsonArray Items { get; set; } = new();
}

public sealed class Career
{
    public string Id { get; set; } = "";
    public int AccountId { get; set; }
    public string Name { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string Side { get; set; } = "";
    public string Archetype { get; set; } = "";
    public string Edition { get; set; } = "";
    public string GameVersion { get; set; } = "";
    public int MemberCategory { get; set; }
    public int SelectedMemberCategory { get; set; }
    public bool Retired { get; set; }
    public Dictionary<string, long> LifetimeTotals { get; set; } = new();
    public int GearVersion { get; set; }
    public bool GearPending { get; set; }
    public long LastGearUpdateUtc { get; set; }
    public long NextGearAttemptUtc { get; set; }
    public long RegisteredUtc { get; set; }
    public double DailyRaidHours { get; set; }
    public double SurvivalChance { get; set; }
    public double RaidSeconds { get; set; }
    public double OnlineSeconds { get; set; }
    public double PendingRaidSeconds { get; set; }
    public int LifeVersion { get; set; }
    public double TargetRaidSeconds { get; set; }
    public int MissingRaids { get; set; }
    public int AwolRaids { get; set; }
    public int RunThroughRaids { get; set; }
    public int CurrentSurvivalStreak { get; set; }
    public int BestSurvivalStreak { get; set; }
    public bool NeedsRegear { get; set; }
    public long DiedUtc { get; set; }
    public bool EquipmentIsSimulated { get; set; }
    public long SimulatedLoadoutOrder { get; set; }
    public bool IsOnline { get; set; }
    public long LastActivityUtc { get; set; }
    public long NextActivityUtc { get; set; }
    public double PendingLiveSeconds { get; set; }
    public int Raids { get; set; }
    public int Survived { get; set; }
    public int PmcKills { get; set; }
    public int ScavKills { get; set; }
    public int Experience { get; set; }
    public int FeatureVersion { get; set; }
    public int PrestigeLevel { get; set; }
    public long LastPrestigeUtc { get; set; }
    public double PrestigeCycleStartSeconds { get; set; }
    public List<PrestigeRecord> PrestigeHistory { get; set; } = new();
    public Dictionary<string, double> SkillCarry { get; set; } = new();
    public Dictionary<string, int> ShowcaseAreas { get; set; } = new();
    public long StoryDeaths { get; set; }
    public long StoryHoldUntilUtc { get; set; }
    public int DevelopmentVersion { get; set; }
    public ObservedCareer Observed { get; set; } = new();
    public double WealthRubles { get; set; }
    public double HideoutInvestment { get; set; }
    // Zero levels are left out.
    public Dictionary<int, int> AreaLevels { get; set; } = new();
    public string CustomizationStashId { get; set; } = "";
    public Dictionary<string, long> Achievements { get; set; } = new();
    public int LastEquipmentAccount { get; set; }
    public long LastEquipmentOrder { get; set; }
    public long LastEquipmentGeneratedUtc { get; set; }
    public int LastGeneratedLevel { get; set; }
    public int LastGeneratedExperience { get; set; }
    public int LastGeneratedPrestige { get; set; }
    public JsonObject Customization { get; set; } = new();
    public bool VisibleLoadout { get; set; }
    public bool Cheater { get; set; }
    public long SessionEndUtc { get; set; }
    public long LastOfflineUtc { get; set; }
    public int DeathStreak { get; set; }
    public long TiltUntilUtc { get; set; }
    public int QuestsDone { get; set; }
    public long BannedUtc { get; set; }
    public List<GearEvent> GearHistory { get; set; } = new();
}

public sealed record GearEvent(long Utc, string Kind, string Text, double Rubles);

// The heavy per-career JSON, kept in Data/careers/<id>.json and loaded on demand.
public sealed class CareerAssets
{
    public int SchemaVersion { get; set; } = 1;
    public string CareerId { get; set; } = "";
    public JsonObject Equipment { get; set; } = new();
    public JsonArray ShowcaseItems { get; set; } = new();
    // Null until a prestige carries items over. Before that the stash is rolled from the native template when needed.
    public JsonObject? Stash { get; set; }
    [JsonIgnore] internal long Version;
    [JsonIgnore] internal long SavedVersion;
    internal void Changed() => Version++;
}

public sealed class Encounter
{
    public int SchemaVersion { get; set; } = 1;
    public int AccountId { get; set; }
    public string CareerId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string RuntimeProfileId { get; set; } = "";
    public long GeneratedUtc { get; set; }
    public long GenerationOrder { get; set; }
    public int GeneratedExperience { get; set; }
    public int GeneratedLevel { get; set; }
    public int GeneratedPrestige { get; set; }
    public JsonObject Equipment { get; set; } = new();
    public JsonObject Customization { get; set; } = new();
    public long KillerCapturedUtc { get; set; }
    public string StoryOutcome { get; set; } = "None";
    public long StoryDueUtc { get; set; }
    public bool KillerCaptured { get; set; }
    public string? RaidId { get; set; }
    public string Map { get; set; } = "";
    // Equipment lives only in the encounter file once trimmed.
    [JsonIgnore] public bool Trimmed { get; set; }
}

public sealed class PendingStory
{
    public string CareerId { get; set; } = "";
    public string Map { get; set; } = "";
    // Died, Survived or Transit.
    public string Outcome { get; set; } = "Survived";
    public string TransitTo { get; set; } = "";
    public bool Regear { get; set; }
    public bool Killer { get; set; }
    public long GenerationOrder { get; set; }
    public long ScheduledUtc { get; set; }
    public long DueUtc { get; set; }
}

public sealed class KillerReport : IRequestData
{
    public int ProtocolVersion { get; set; }
    public string RaidId { get; set; } = "";
    public string RuntimeProfileId { get; set; } = "";
    public int AccountId { get; set; }
    public JsonObject Equipment { get; set; } = new();
    public JsonObject Customization { get; set; } = new();
}

public sealed class Friendship
{
    public string RequestId { get; set; } = "";
    public long RequestedUtc { get; set; }
    public bool Accepted { get; set; }
    public bool Removed { get; set; }
    public bool Cancelled { get; set; }
}
