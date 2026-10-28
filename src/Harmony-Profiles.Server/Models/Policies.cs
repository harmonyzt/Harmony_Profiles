using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using System.Reflection;
using SPTarkov.Server.Core.Models.Utils;

namespace HarmonyProfiles.Server;

public sealed class PersonalityTemplate
{
    public string Id { get; set; } = "Casual";
    public int Weight { get; set; } = 40;
    public int MinimumHours { get; set; } = 100;
    public int MaximumHours { get; set; } = 2500;
    public int MinimumLevel { get; set; } = 15;
    public int MaximumLevel { get; set; } = 50;
    public double MinimumDailyHours { get; set; } = .15;
    public double MaximumDailyHours { get; set; } = 2.65;
    public double MinimumSurvival { get; set; } = .25;
    public double MaximumSurvival { get; set; } = .5;
    public double MinimumRaidMinutes { get; set; } = 8;
    public double MaximumRaidMinutes { get; set; } = 42;
    public string? ProgressTo { get; set; }
    public double TransitionHours { get; set; } = 200;
}

public sealed class ProgressionPrior
{
    public int MaximumLevel { get; set; }
    public double RaidMinutes { get; set; }
    public double PmcKills { get; set; }
    public double ScavKills { get; set; }
}

public sealed class SimulationPolicy
{
    public List<ProgressionPrior> ProgressionPriors { get; set; } =
    [
        new() { MaximumLevel = 14, RaidMinutes = 11.44, PmcKills = 1.17, ScavKills = 2.17 },
        new() { MaximumLevel = 29, RaidMinutes = 14.45, PmcKills = 1.61, ScavKills = 2.78 },
        new() { MaximumLevel = 44, RaidMinutes = 16.28, PmcKills = 1.98, ScavKills = 2.56 },
        new() { MaximumLevel = 79, RaidMinutes = 17.18, PmcKills = 2.28, ScavKills = 3.68 }
    ];
    public bool EnableApbsCareerIntegration { get; set; } = true;
    public HideoutProgressionSettings Hideout { get; set; } = new();
    public bool EnablePrestige { get; set; } = true;
    public double MinimumPrestigeCycleHours { get; set; } = 500;
    public bool EnableShowcase { get; set; } = true;
    public bool EnablePostRaidStories { get; set; } = true;
    public double PostRaidDeathChance { get; set; } = .01;
    public double PostRaidRegearChance { get; set; } = .02;
    public int StoryMinimumDelaySeconds { get; set; } = 120;
    public int StoryMaximumDelaySeconds { get; set; } = 600;
    public int StoryVisibilitySeconds { get; set; } = 900;

    public void Validate()
    {
        if (Hideout is null) throw new InvalidDataException("Hideout policy is missing.");
        Hideout.Validate();
        if (ProgressionPriors is null || ProgressionPriors.Count is < 1 or > 79
            || ProgressionPriors.Any(p => p is null || p.MaximumLevel is < 1 or > 79
                || !double.IsFinite(p.RaidMinutes) || p.RaidMinutes is < 5 or > 45
                || !double.IsFinite(p.PmcKills) || p.PmcKills is < 0 or > 20
                || !double.IsFinite(p.ScavKills) || p.ScavKills is < 0 or > 20)
            || !ProgressionPriors.Select(p => p.MaximumLevel).SequenceEqual(ProgressionPriors.Select(p => p.MaximumLevel).Distinct().Order())
            || ProgressionPriors[^1].MaximumLevel != 79)
            throw new InvalidDataException("Progression priors require increasing level bands ending at 79, 5-45 minute raids, and 0-20 kills per raid.");
        if (!double.IsFinite(MinimumPrestigeCycleHours) || MinimumPrestigeCycleHours is < 10 or > 10000
            || !double.IsFinite(PostRaidDeathChance) || !double.IsFinite(PostRaidRegearChance)
            || PostRaidDeathChance < 0 || PostRaidRegearChance < 0 || PostRaidDeathChance + PostRaidRegearChance > 1
            || StoryMinimumDelaySeconds < 30 || StoryMaximumDelaySeconds < StoryMinimumDelaySeconds || StoryMaximumDelaySeconds > 86400 || StoryVisibilitySeconds is < 60 or > 86400)
            throw new InvalidDataException("Simulation policy is outside the supported ranges.");
    }
}

public sealed class HideoutProgressionSettings
{
    public double ConstructionSpeed { get; set; } = 1;
    public double MaterialCostMultiplier { get; set; } = 1;
    public double BuiltChanceMultiplier { get; set; } = 1;

    public void Validate()
    {
        if (!double.IsFinite(ConstructionSpeed) || ConstructionSpeed is < .25 or > 4
            || !double.IsFinite(MaterialCostMultiplier) || MaterialCostMultiplier is < .25 or > 4
            || !double.IsFinite(BuiltChanceMultiplier) || BuiltChanceMultiplier is < .25 or > 2)
            throw new InvalidDataException("Hideout speed/cost must be 0.25-4 and built chance must be 0.25-2.");
    }
}

public sealed record PrestigeRecord(int Level, string DefinitionId, long Utc, double LifetimeHours, int PreviousExperience, int CarriedItems);
