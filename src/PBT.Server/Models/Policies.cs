using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using System.Reflection;
using SPTarkov.Server.Core.Models.Utils;

namespace PBT.Server;

public sealed class PersonalityTemplate
{
    public string Id { get; set; } = "Casual";
    public int Weight { get; set; } = 40;
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
    public double SurvivedMinutes { get; set; }
    public double DeathMinutes { get; set; }
    // Kills per raid for an average-skill account. Skill and personality scale these.
    public double PmcKills { get; set; }
    public double ScavKills { get; set; }
}

// Survived raid XP is Base + PerLevel * level + PerLevelSquared * level^2. Deaths earn DeathShare of that.
public sealed class RaidExperienceSettings
{
    public double SurvivedBase { get; set; } = 2000;
    public double SurvivedPerLevel { get; set; } = 150;
    public double SurvivedPerLevelSquared { get; set; } = 5;
    public double DeathShare { get; set; } = .3;
}

// Highest share of max-level, average-skill accounts that own an achievement of each rarity.
public sealed class AchievementSettings
{
    public double Common { get; set; } = .9;
    public double Rare { get; set; } = .45;
    public double Legendary { get; set; } = .12;
}

public sealed class SimulationPolicy
{
    public List<ProgressionPrior> ProgressionPriors { get; set; } =
    [
        new() { MaximumLevel = 14, SurvivedMinutes = 22, DeathMinutes = 9, PmcKills = .35, ScavKills = .9 },
        new() { MaximumLevel = 29, SurvivedMinutes = 25, DeathMinutes = 11, PmcKills = .55, ScavKills = 1.1 },
        new() { MaximumLevel = 44, SurvivedMinutes = 28, DeathMinutes = 12, PmcKills = .7, ScavKills = 1.2 },
        new() { MaximumLevel = 79, SurvivedMinutes = 30, DeathMinutes = 13, PmcKills = .85, ScavKills = 1.3 }
    ];
    public RaidExperienceSettings RaidExperience { get; set; } = new();
    // Online time is raid time times a per-account factor in this range, for menus, hideout and flea.
    public double OnlineTimeMinimum { get; set; } = 1.15;
    public double OnlineTimeMaximum { get; set; } = 1.5;
    public AchievementSettings Achievements { get; set; } = new();
    public bool EnableApbsCareerIntegration { get; set; } = true;
    public HideoutProgressionSettings Hideout { get; set; } = new();
    public NotableNameSettings NotableNames { get; set; } = new();
    public bool EnablePrestige { get; set; } = true;
    public double MinimumPrestigeCycleHours { get; set; } = 150;
    public InitialPrestigeSettings InitialPrestige { get; set; } = new();
    public bool EnableShowcase { get; set; } = true;
    // Rack guns come from loadouts this many APBS tiers above the account's own, capped at the top tier.
    public int RackTierBoostMinimum { get; set; } = 1;
    public int RackTierBoostMaximum { get; set; } = 3;
    // Share of generated profile loadouts rolled as night raid kits, so some accounts carry NVGs.
    public double NightGearChance { get; set; } = .3;
    // In-raid spawns of a selected career wear the loadout its profile shows instead of a fresh APBS roll.
    public bool RetainProfileGearInRaid { get; set; } = true;
    public bool EnablePostRaidStories { get; set; } = true;
    public double PostRaidDeathChance { get; set; } = .35;
    public double PostRaidRegearChance { get; set; } = .3;
    public int StoryMinimumDelaySeconds { get; set; } = 120;
    public int StoryMaximumDelaySeconds { get; set; } = 600;
    public int StoryVisibilitySeconds { get; set; } = 900;

    public void Validate()
    {
        if (Hideout is null) throw new InvalidDataException("Hideout policy is missing.");
        Hideout.Validate();
        if (NotableNames is null) throw new InvalidDataException("Notable name policy is missing.");
        NotableNames.Validate();
        if (InitialPrestige is null || !double.IsFinite(InitialPrestige.Share) || InitialPrestige.Share is < 0 or > 1
            || !double.IsFinite(InitialPrestige.Repeat) || InitialPrestige.Repeat is < 0 or > .95)
            throw new InvalidDataException("Starting prestige share must be 0-1 and the repeat chance 0-0.95.");
        if (ProgressionPriors is null || ProgressionPriors.Count is < 1 or > 79
            || ProgressionPriors.Any(p => p is null || p.MaximumLevel is < 1 or > 79
                || !double.IsFinite(p.SurvivedMinutes) || p.SurvivedMinutes is < 3 or > 60
                || !double.IsFinite(p.DeathMinutes) || p.DeathMinutes is < 3 or > 60
                || !double.IsFinite(p.PmcKills) || p.PmcKills is < 0 or > 20
                || !double.IsFinite(p.ScavKills) || p.ScavKills is < 0 or > 20)
            || !ProgressionPriors.Select(p => p.MaximumLevel).SequenceEqual(ProgressionPriors.Select(p => p.MaximumLevel).Distinct().Order())
            || ProgressionPriors[^1].MaximumLevel != 79)
            throw new InvalidDataException("Progression priors require increasing level bands ending at 79, 3-60 minute raids, and 0-20 kills per raid.");
        var xp = RaidExperience;
        if (xp is null || !double.IsFinite(xp.SurvivedBase) || xp.SurvivedBase is < 100 or > 100000
            || !double.IsFinite(xp.SurvivedPerLevel) || xp.SurvivedPerLevel is < 0 or > 10000
            || !double.IsFinite(xp.SurvivedPerLevelSquared) || xp.SurvivedPerLevelSquared is < 0 or > 1000
            || !double.IsFinite(xp.DeathShare) || xp.DeathShare is < 0 or > 1
            || !double.IsFinite(OnlineTimeMinimum) || !double.IsFinite(OnlineTimeMaximum)
            || OnlineTimeMinimum < 1 || OnlineTimeMaximum < OnlineTimeMinimum || OnlineTimeMaximum > 5)
            throw new InvalidDataException("Raid experience needs a 100-100000 base and a 0-1 death share; online time factors must be 1-5.");
        if (Achievements is null || new[] { Achievements.Common, Achievements.Rare, Achievements.Legendary }.Any(v => !double.IsFinite(v) || v is < 0 or > 1))
            throw new InvalidDataException("Achievement shares must be between 0 and 1.");
        if (RackTierBoostMinimum is < 0 or > 6 || RackTierBoostMaximum < RackTierBoostMinimum || RackTierBoostMaximum > 6
            || !double.IsFinite(NightGearChance) || NightGearChance is < 0 or > 1)
            throw new InvalidDataException("Rack tier boost must be a 0-6 range and the night gear chance 0-1.");
        if (!double.IsFinite(MinimumPrestigeCycleHours) || MinimumPrestigeCycleHours is < 10 or > 10000
            || !double.IsFinite(PostRaidDeathChance) || !double.IsFinite(PostRaidRegearChance)
            || PostRaidDeathChance < 0 || PostRaidRegearChance < 0 || PostRaidDeathChance + PostRaidRegearChance > 1
            || StoryMinimumDelaySeconds < 30 || StoryMaximumDelaySeconds < StoryMinimumDelaySeconds || StoryMaximumDelaySeconds > 86400 || StoryVisibilitySeconds is < 60 or > 86400)
            throw new InvalidDataException("Simulation policy is outside the supported ranges.");
    }
}

public sealed class InitialPrestigeSettings
{
    // Share of new non-Timmy accounts that have prestiged at least once.
    public double Share { get; set; } = .15;
    // Chance of each further prestige after the first.
    public double Repeat { get; set; } = .45;
}

public sealed class NotableNameSettings
{
    public bool Enabled { get; set; } = true;
    // Moves the hidden skill roll this share of the way towards the best possible player.
    public double SkillBonus { get; set; } = .3;
    public int TierBoostMinimum { get; set; } = 1;
    public int TierBoostMaximum { get; set; } = 2;

    public void Validate()
    {
        if (!double.IsFinite(SkillBonus) || SkillBonus is < 0 or > 1 || TierBoostMinimum is < 0 or > 6
            || TierBoostMaximum < TierBoostMinimum || TierBoostMaximum > 6)
            throw new InvalidDataException("Notable names need a 0-1 skill bonus and a 0-6 tier boost range.");
    }
}

public sealed class HideoutProgressionSettings
{
    public double ConstructionSpeed { get; set; } = 1;
    public double MaterialCostMultiplier { get; set; } = 1;
    public double BuiltChanceMultiplier { get; set; } = 1;
    // Share of accounts that barely touch the hideout, whatever their level.
    public double NeglectedShare { get; set; } = .1;

    public void Validate()
    {
        if (!double.IsFinite(ConstructionSpeed) || ConstructionSpeed is < .25 or > 4
            || !double.IsFinite(MaterialCostMultiplier) || MaterialCostMultiplier is < .25 or > 4
            || !double.IsFinite(BuiltChanceMultiplier) || BuiltChanceMultiplier is < .25 or > 2
            || !double.IsFinite(NeglectedShare) || NeglectedShare is < 0 or > .5)
            throw new InvalidDataException("Hideout speed/cost must be 0.25-4, built chance 0.25-2 and neglected share 0-0.5.");
    }
}

public sealed record PrestigeRecord(int Level, string DefinitionId, long Utc, double LifetimeHours, int PreviousExperience, int CarriedItems);
