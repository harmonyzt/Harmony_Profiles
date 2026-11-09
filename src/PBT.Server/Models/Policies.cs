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
        new()
        {
            MaximumLevel = 14,
            SurvivedMinutes = 22,
            DeathMinutes = 9,
            PmcKills = .35,
            ScavKills = .9
        },
        new()
        {
            MaximumLevel = 29,
            SurvivedMinutes = 25,
            DeathMinutes = 11,
            PmcKills = .55,
            ScavKills = 1.1
        },
        new()
        {
            MaximumLevel = 44,
            SurvivedMinutes = 28,
            DeathMinutes = 12,
            PmcKills = .7,
            ScavKills = 1.2
        },
        new()
        {
            MaximumLevel = 79,
            SurvivedMinutes = 30,
            DeathMinutes = 13,
            PmcKills = .85,
            ScavKills = 1.3
        }
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
    // Broke accounts wear cheaper kits and rich ones better, on top of the level's APBS tier.
    public bool WealthGearTiers { get; set; } = true;
    public int RackTierBoostMinimum { get; set; } = 1;

    public int RackTierBoostMaximum { get; set; } = 3;

    // Share of generated profile loadouts rolled as night raid kits, so some accounts carry NVGs.
    public double NightGearChance { get; set; } = .3;
    public bool GearProgression { get; set; } = true;
    public double GearUpgradeChance { get; set; } = .35;

    public double InsuranceReturnChance { get; set; } = .3;

    // Time a dead account spends in the menu before it rebuys. Capped at 20 minutes.
    public double RegearMinimumMinutes { get; set; } = 5;
    public double RegearMaximumMinutes { get; set; } = 20;

    // In-raid spawns of a selected career wear the loadout its profile shows instead of a fresh APBS roll.
    public bool RetainProfileGearInRaid { get; set; } = true;
    public bool RealisticSupplies { get; set; } = true;
    public bool EnablePostRaidStories { get; set; } = true;
    public double PostRaidDeathChance { get; set; } = .35;

    public double PostRaidRegearChance { get; set; } = .3;

    // The rest of your raid's PMCs. Lower, since a raid can hold a dozen of them.
    public double RaidDeathChance { get; set; } = .12;
    public double RaidRegearChance { get; set; } = .15;
    public int StoryMinimumDelaySeconds { get; set; } = 120;
    public int StoryMaximumDelaySeconds { get; set; } = 600;
    public int StoryVisibilitySeconds { get; set; } = 900;
    public BotMessageSettings BotMessages { get; set; } = new();
    public WorldSettings World { get; set; } = new();
    public CheaterSettings Cheaters { get; set; } = new();
    public FriendGiftSettings FriendGifts { get; set; } = new();

    public void Validate()
    {
        if (FriendGifts is null) throw new InvalidDataException("Friend gift policy is missing.");
        FriendGifts.Validate();
        if (Cheaters is null) throw new InvalidDataException("Cheater policy is missing.");
        Cheaters.Validate();
        if (World is null) throw new InvalidDataException("World policy is missing.");
        World.Validate();
        if (Hideout is null) throw new InvalidDataException("Hideout policy is missing.");
        Hideout.Validate();
        if (BotMessages is null) throw new InvalidDataException("Bot message policy is missing.");
        BotMessages.Validate();
        if (NotableNames is null) throw new InvalidDataException("Notable name policy is missing.");
        NotableNames.Validate();
        if (InitialPrestige is null || !double.IsFinite(InitialPrestige.Share) || InitialPrestige.Share is < 0 or > 1 || !double.IsFinite(InitialPrestige.Repeat)
            || InitialPrestige.Repeat is < 0 or > .95)
            throw new InvalidDataException("Starting prestige share must be 0-1 and the repeat chance 0-0.95.");
        if (ProgressionPriors is null || ProgressionPriors.Count is < 1 or > 79 || ProgressionPriors.Any(p =>
                p is null || p.MaximumLevel is < 1 or > 79 || !double.IsFinite(p.SurvivedMinutes) || p.SurvivedMinutes is < 3 or > 60 || !double.IsFinite(p.DeathMinutes)
                || p.DeathMinutes is < 3 or > 60 || !double.IsFinite(p.PmcKills) || p.PmcKills is < 0 or > 20 || !double.IsFinite(p.ScavKills) || p.ScavKills is < 0 or > 20)
            || !ProgressionPriors.Select(p => p.MaximumLevel).SequenceEqual(ProgressionPriors.Select(p => p.MaximumLevel).Distinct().Order())
            || ProgressionPriors[^1].MaximumLevel != 79)
            throw new InvalidDataException("Progression priors require increasing level bands ending at 79, 3-60 minute raids, and 0-20 kills per raid.");
        var xp = RaidExperience;
        if (xp is null || !double.IsFinite(xp.SurvivedBase) || xp.SurvivedBase is < 100 or > 100000 || !double.IsFinite(xp.SurvivedPerLevel)
            || xp.SurvivedPerLevel is < 0 or > 10000 || !double.IsFinite(xp.SurvivedPerLevelSquared) || xp.SurvivedPerLevelSquared is < 0 or > 1000
            || !double.IsFinite(xp.DeathShare) || xp.DeathShare is < 0 or > 1 || !double.IsFinite(OnlineTimeMinimum) || !double.IsFinite(OnlineTimeMaximum) || OnlineTimeMinimum < 1
            || OnlineTimeMaximum < OnlineTimeMinimum || OnlineTimeMaximum > 5)
            throw new InvalidDataException("Raid experience needs a 100-100000 base and a 0-1 death share; online time factors must be 1-5.");
        if (Achievements is null || new[] { Achievements.Common, Achievements.Rare, Achievements.Legendary }.Any(v => !double.IsFinite(v) || v is < 0 or > 1))
            throw new InvalidDataException("Achievement shares must be between 0 and 1.");
        if (RackTierBoostMinimum is < 0 or > 6 || RackTierBoostMaximum < RackTierBoostMinimum || RackTierBoostMaximum > 6 || !double.IsFinite(NightGearChance)
            || NightGearChance is < 0 or > 1 || !double.IsFinite(GearUpgradeChance) || GearUpgradeChance is < 0 or > 1 || !double.IsFinite(InsuranceReturnChance)
            || InsuranceReturnChance is < 0 or > 1 || !double.IsFinite(RegearMinimumMinutes) || !double.IsFinite(RegearMaximumMinutes) || RegearMinimumMinutes < 0
            || RegearMaximumMinutes < RegearMinimumMinutes || RegearMaximumMinutes > 20)
            throw new InvalidDataException("Rack tier boost must be a 0-6 range; night gear, upgrade and insurance chances 0-1; regear 0-20 minutes.");
        if (!double.IsFinite(MinimumPrestigeCycleHours) || MinimumPrestigeCycleHours is < 10 or > 10000 || !double.IsFinite(PostRaidDeathChance)
            || !double.IsFinite(PostRaidRegearChance) || PostRaidDeathChance < 0 || PostRaidRegearChance < 0 || PostRaidDeathChance + PostRaidRegearChance > 1
            || !double.IsFinite(RaidDeathChance) || !double.IsFinite(RaidRegearChance) || RaidDeathChance < 0 || RaidRegearChance < 0 || RaidDeathChance + RaidRegearChance > 1
            || StoryMinimumDelaySeconds < 30 || StoryMaximumDelaySeconds < StoryMinimumDelaySeconds || StoryMaximumDelaySeconds > 86400
            || StoryVisibilitySeconds is < 60 or > 86400)
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

public sealed class WorldSettings
{
    public double TransitChance { get; set; } = .15;

    // Accounts that log off after a raid instead of queueing again. Deaths log off a bit more.
    public double LogOffChance { get; set; } = .25;

    // Popularity of a map whose headline boss is guaranteed (a weekly boss rotation sets it to 100%).
    public double WeeklyBossBoost { get; set; } = 1.6;
    public double StreamerMapBoost { get; set; } = 2.5;
    public double NameMapBoost { get; set; } = 4;

    public double QueueTimeMultiplier { get; set; } = 1;

    // Fewer careers online late at night (your local time), more on evenings and weekends.
    public bool PeakHours { get; set; } = true;
    public PeakHourSettings Peak { get; set; } = new();

    // The client shows Matching / Looking for server before a raid, capped at this many seconds.
    public bool SimulateMatching { get; set; } = true;

    public int MaximumMatchingSeconds { get; set; } = 45;

    // Matching waits until enough careers near your level have queued for the map, instead of a timer.
    public bool WaitForPlayers { get; set; }

    // Matching never finishes sooner than this, in either mode.
    public int MinimumMatchingSeconds { get; set; }

    // A real wait gives up after this long and fills the server from players in the menu.
    public int MaximumWaitSeconds { get; set; } = 300;

    // Where you play from (EU, US or Asia). Sets your ping to each server and which ones you match into.
    public string Location { get; set; } = "EU";
    public bool SimulateConnection { get; set; } = true;
    public bool ConnectionLossEvents { get; set; }

    // Packet loss and the start-of-raid hitch now and then snap you back to where you just were.
    public bool RubberBanding { get; set; }

    public void Validate()
    {
        if (!double.IsFinite(TransitChance) || TransitChance is < 0 or > 1 || !double.IsFinite(LogOffChance) || LogOffChance is < 0 or > 1 || !double.IsFinite(WeeklyBossBoost)
            || WeeklyBossBoost is < 1 or > 5 || !double.IsFinite(StreamerMapBoost) || StreamerMapBoost is < 1 or > 10 || !double.IsFinite(NameMapBoost)
            || NameMapBoost is < 1 or > 10 || !double.IsFinite(QueueTimeMultiplier) || QueueTimeMultiplier is < .1 or > 10 || MaximumMatchingSeconds is < 0 or > 600
            || MinimumMatchingSeconds is < 0 or > 600 || MaximumWaitSeconds is < 10 or > 1800)
            throw new InvalidDataException("World chances must be 0-1, boosts 1-5 (streamer and name boosts 1-10), queue multiplier 0.1-10, matching 0-600 seconds and the longest wait 10-1800 seconds.");
        if (!PopulationStore.Regions.Contains(Location)) throw new InvalidDataException("Location must be EU, US or Asia.");
        if (Peak is null) throw new InvalidDataException("Peak hour policy is missing.");
        Peak.Validate();
    }
}

// Who is online when. Each schedule is a kind of player with its own hourly weights; shares decide how many
// accounts follow it. Online rate multiplier stays the overall volume control, these settings move it around the clock.
public sealed class PeakHourSettings
{
    // 0 plays the same all day, 1 is the default curves, higher exaggerates the peaks and troughs.
    public double Strength { get; set; } = 1;

    // Moves every schedule forward (+) or back (-) in hours, for players whose clock is not the default region.
    public int ShiftHours { get; set; }

    // Extra multiplier for Saturday and Sunday on top of each schedule's own weekend factor.
    public double WeekendBoost { get; set; } = 1;

    public List<PeakSchedule> Schedules { get; set; } =
    [
        new()
        {
            Name = "Evenings", Share = 40, Weekday = 1, Weekend = 1.25,
            Hours = Curve((0, .5), (2, .1), (8, .05), (16, .5), (19, 2.2), (22, 2), (23, 1.2))
        },
        new()
        {
            Name = "Night owl", Share = 15, Weekday = 1, Weekend = 1.1,
            Hours = Curve((0, 2.2), (3, 1.8), (6, .2), (12, .1), (19, .6), (22, 1.6))
        },
        new()
        {
            Name = "Afternoons", Share = 15, Weekday = 1, Weekend = 1.1,
            Hours = Curve((0, .1), (9, .2), (13, 1.8), (17, 2), (20, .8), (23, .2))
        },
        new()
        {
            Name = "Weekends", Share = 15, Weekday = .35, Weekend = 2.6,
            Hours = Curve((0, .8), (3, .2), (9, .4), (13, 1.5), (18, 2), (23, 1.2))
        },
        new()
        {
            Name = "Mornings", Share = 5, Weekday = 1, Weekend = 1,
            Hours = Curve((0, .05), (6, .6), (8, 2), (12, 1.6), (15, .4), (20, .1))
        },
        new()
        {
            Name = "Anytime", Share = 10, Weekday = 1, Weekend = 1.1,
            Hours = Curve((0, .8), (6, .5), (12, 1), (18, 1.3))
        }
    ];

    // Weights per hour, interpolated between points and wrapping past midnight. The first point is hour 0.
    public static double[] Curve(params (int Hour, double Weight)[] points)
    {
        var hours = new double[24];
        for (int h = 0; h < 24; h++)
        {
            int i = points.Length - 1;
            while (points[i].Hour > h) i--;
            var (h0, w0) = points[i];
            var (h1, w1) = i + 1 < points.Length ? points[i + 1] : (24, points[0].Weight);
            hours[h] = w0 + (w1 - w0) * (h - h0) / (h1 - h0);
        }

        return hours;
    }

    public void Validate()
    {
        if (!double.IsFinite(Strength) || Strength is < 0 or > 4 || ShiftHours is < -12 or > 12 || !double.IsFinite(WeekendBoost) || WeekendBoost is < .25 or > 4)
            throw new InvalidDataException("Peak strength must be 0-4, the shift -12 to 12 hours and the weekend boost 0.25-4.");
        if (Schedules is null || Schedules.Count is < 1 or > 12
            || Schedules.Any(s => s is null || string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 40 || s.Share is < 0 or > 1000
                || !double.IsFinite(s.Weekday) || s.Weekday is < 0 or > 5 || !double.IsFinite(s.Weekend) || s.Weekend is < 0 or > 5
                || (s.Weekday == 0 && s.Weekend == 0) || s.Hours is not { Length: 24 } || s.Hours.Any(h => !double.IsFinite(h) || h is < 0 or > 100)))
            throw new InvalidDataException("Peak schedules need a name, a 0-1000 share, 0-5 weekday and weekend factors and 24 hourly weights from 0 to 100.");
        if (Schedules.Sum(s => s.Share) <= 0 || Schedules.All(s => s.Hours.Sum() <= 0))
            throw new InvalidDataException("At least one peak schedule needs a share and some daily hours.");
    }
}

public sealed class PeakSchedule
{
    public string Name { get; set; } = "";
    public int Share { get; set; } = 10;
    public double Weekday { get; set; } = 1;
    public double Weekend { get; set; } = 1;
    public double[] Hours { get; set; } = PeakHourSettings.Curve((0, 1));
}

public sealed class CheaterHelmet
{
    public string Tpl { get; set; } = "";
    public int Weight { get; set; } = 10;
}

// Rare blatant cheaters with ordinary names. Turning this off makes them play like everyone else again.
public sealed class CheaterSettings
{
    public bool Enabled { get; set; } = true;
    public double Share { get; set; } = .01;
    public double Survival { get; set; } = .92;
    public double KillMultiplier { get; set; } = 4;
    public int TierBoost { get; set; } = 4;
    public double HelmetChance { get; set; } = .6;

    // Cheaters wear their PBT kit in raid even with the APBS takeover off.
    public bool AlwaysPbtGear { get; set; } = true;

    public List<CheaterHelmet> Helmets { get; set; } =
    [
        new()
        {
            Tpl = "5c0e874186f7745dc7616606",
            Weight = 30
        }, // Maska (Killa)
        new()
        {
            Tpl = "5c091a4e0db834001d5addc8",
            Weight = 10
        }, // Maska OD
        new()
        {
            Tpl = "60a7ad2a2198820d95707a2e",
            Weight = 10
        }, // Tagilla UBEY
        new()
        {
            Tpl = "60a7ad3a0c5cb24b0134664a",
            Weight = 10
        }, // Tagilla Gorilla
        new()
        {
            Tpl = "678f84bb9e85556ca60f0362",
            Weight = 8
        }, // Tagilla ZABEY
        new()
        {
            Tpl = "5aa7e276e5b5b000171d0647",
            Weight = 12
        }, // Altyn
        new()
        {
            Tpl = "5f60c74e3b85f6263c145586",
            Weight = 10
        }, // Rys-T
        new()
        {
            Tpl = "5ca20ee186f774799474abc2",
            Weight = 10
        } // Vulkan-5
    ];

    public double BanChance { get; set; } = .75;
    public double BanMinimumHours { get; set; } = 1;
    public double BanMaximumHours { get; set; } = 6;
    public bool ReturnLostGear { get; set; } = true;
    public int RewardRubles { get; set; } = 150000;

    public void Validate()
    {
        if (!double.IsFinite(Share) || Share is < 0 or > .2 || !double.IsFinite(Survival) || Survival is < 0 or > 1 || !double.IsFinite(KillMultiplier)
            || KillMultiplier is < 1 or > 20 || TierBoost is < 0 or > 6 || !double.IsFinite(HelmetChance) || HelmetChance is < 0 or > 1 || !double.IsFinite(BanChance)
            || BanChance is < 0 or > 1 || !double.IsFinite(BanMinimumHours) || BanMinimumHours is < 0 or > 720 || !double.IsFinite(BanMaximumHours)
            || BanMaximumHours < BanMinimumHours || BanMaximumHours > 720 || RewardRubles is < 0 or > 50_000_000 || Helmets is null || Helmets.Count > 100
            || Helmets.Any(h => h is null || h.Tpl is null || h.Tpl.Length != 24 || h.Weight is < 0 or > 1000))
            throw new InvalidDataException("Cheaters need a 0-0.2 share, 0-1 chances, 1-20 kill multiplier, 0-6 tier boost, 0-720 ban hours and 24-character helmet ids.");
    }
}

// Friends mail a spare kit to a player who is low level and broke, or keeps dying. Never to a prestiged player.
public sealed class FriendGiftSettings
{
    public bool Enabled { get; set; } = true;

    // Rolled after each raid that qualifies.
    public double Chance { get; set; } = .2;
    public int MaximumLevel { get; set; } = 35;
    public int MaximumRubles { get; set; } = 1_000_000;

    // Deaths in a row that qualify on their own, whatever the level and money.
    public int DeathStreak { get; set; } = 5;
    public double CooldownHours { get; set; } = 12;

    public void Validate()
    {
        if (!double.IsFinite(Chance) || Chance is < 0 or > 1 || MaximumLevel is < 1 or > 80 || MaximumRubles is < 0 or > 1_000_000_000 || DeathStreak is < 1 or > 100
            || !double.IsFinite(CooldownHours) || CooldownHours is < 0 or > 720)
            throw new InvalidDataException("Friend gifts need a 0-1 chance, level 1-80, 0-1e9 roubles, a 1-100 death streak and a 0-720 hour cooldown.");
    }
}

public sealed class BotMessageSettings
{
    public bool Enabled { get; set; } = true;

    // Chance per notable event. A squad wipe counts three times.
    public double Chance { get; set; } = .05;
    public bool ReplaceNativeMessages { get; set; } = true;
    public int MinimumDelaySeconds { get; set; } = 30;
    public int MaximumDelaySeconds { get; set; } = 240;

    public void Validate()
    {
        if (!double.IsFinite(Chance) || Chance is < 0 or > 1 || MinimumDelaySeconds is < 0 or > 3600 || MaximumDelaySeconds < MinimumDelaySeconds || MaximumDelaySeconds > 3600)
            throw new InvalidDataException("Bot messages need a 0-1 chance and a 0-3600 second delay range.");
    }
}

public sealed class NotableNameSettings
{
    public bool Enabled { get; set; } = true;

    // Moves the hidden skill roll this share of the way towards the best possible player.
    public double SkillBonus { get; set; } = .3;
    public int TierBoostMinimum { get; set; } = 1;
    public int TierBoostMaximum { get; set; } = 2;
    public List<string> ExtraNames { get; set; } = new();

    public void Validate()
    {
        if (ExtraNames is null || ExtraNames.Count > 500 || ExtraNames.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 32))
            throw new InvalidDataException("Streamer names need 1-32 characters each, 500 at most.");
        if (!double.IsFinite(SkillBonus) || SkillBonus is < 0 or > 1 || TierBoostMinimum is < 0 or > 6 || TierBoostMaximum < TierBoostMinimum || TierBoostMaximum > 6)
            throw new InvalidDataException("Notable names need a 0-1 skill bonus and a 0-6 tier boost range.");
    }
}

public sealed class HideoutProgressionSettings
{
    public double ConstructionSpeed { get; set; } = 1;

    public double MaterialCostMultiplier { get; set; } = 1;

    // 2 because most accounts otherwise stall behind the gym's broken wall.
    public double BuiltChanceMultiplier { get; set; } = 2;

    // Share of accounts that barely touch the hideout, whatever their level.
    public double NeglectedShare { get; set; } = .1;

    // Every account has a fully built hideout somewhere in this level range. Neglecters finish 15 levels later.
    public int CompleteFromLevel { get; set; } = 45;
    public int CompleteByLevel { get; set; } = 60;

    public void Validate()
    {
        if (CompleteFromLevel is < 1 or > 79 || CompleteByLevel < CompleteFromLevel || CompleteByLevel > 79)
            throw new InvalidDataException("Hideout completion levels must be 1-79, the start at most the end.");
        if (!double.IsFinite(ConstructionSpeed) || ConstructionSpeed is < .25 or > 4 || !double.IsFinite(MaterialCostMultiplier) || MaterialCostMultiplier is < .25 or > 4
            || !double.IsFinite(BuiltChanceMultiplier) || BuiltChanceMultiplier is < .25 or > 4 || !double.IsFinite(NeglectedShare) || NeglectedShare is < 0 or > .5)
            throw new InvalidDataException("Hideout speed/cost/built chance must be 0.25-4 and neglected share 0-0.5.");
    }
}

public sealed record PrestigeRecord(int Level, string DefinitionId, long Utc, double LifetimeHours, int PreviousExperience, int CarriedItems);
