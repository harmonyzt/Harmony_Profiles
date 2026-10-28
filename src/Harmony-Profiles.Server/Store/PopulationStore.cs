using System.Reflection;
using SPTarkov.Server.Core.Generators.Bot;
using SPTarkov.Server.Core.Services.Bot;
using Path = System.IO.Path;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Spt.Bots;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;

namespace HarmonyProfiles.Server;

[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostLoad + 90000)]
public sealed partial class PopulationStore(
    BotTable bots, GlobalTable globals, TemplateTable templates, HideoutTable hideout, TradersTable traders,
    ProfileHelper profileHelper, NotificationSendHelper notifications, HttpResponseUtil responses,
    SaveServer saves, JsonUtil json, ModHelper modHelper, ItemHelper itemHelper, PresetHelper presetHelper, BotInventoryGenerator inventoryGenerator, BotInventoryContainerService inventoryContainers, ISptLogger<PopulationStore> logger) : IOnLoad, IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<int, Encounter> encounters = new();
    private Population population = new();
    private Dictionary<string, Career> byName = new();
    private Dictionary<int, Career> byAccount = new();
    private readonly HashSet<int> allocatedAccounts = new();
    private Timer? checkpoint;
    private Harmony? harmony;
    private Harmony? socialHarmony;
    internal bool SocialReady { get; private set; }
    private FileStream? ownership;
    private string directory = "";
    private bool dirty;
    private bool fikaPresent;
    private readonly object saveGate = new();
    private long saveSequence;
    private long writtenSequence;
    private HarmonyProfilesSettings settings = new();
    public bool Ready { get; private set; }
    internal static PopulationStore? Active { get; private set; }

    public Task OnLoadAsync(CancellationToken token)
    {
        lock (gate)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                directory = Path.Combine(modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly()), "Data");
                Directory.CreateDirectory(directory);
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(directory)!, "wwwroot"));
                // One writer per database, including when two servers point to the same mod folder.
                ownership = new FileStream(Path.Combine(directory, "population.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                var path = Path.Combine(directory, "population.json");
                population = File.Exists(path)
                    ? ReadPopulation(path) ?? throw new InvalidDataException("Empty population")
                    : new Population();
                settings = HarmonyProfilesSettings.Load(Path.Combine(Path.GetDirectoryName(directory)!, "config.json"));
                ValidatePopulation();
                IndexCareerFiles();
                allocatedAccounts.UnionWith(population.Careers.Values.Select(c => c.AccountId));
                var playerAccounts = saves.GetProfiles().Values.Where(p => p.ProfileInfo?.Aid is not null)
                    .Select(p => p.ProfileInfo!.Aid!.Value).ToHashSet();
                if (allocatedAccounts.Overlaps(playerAccounts))
                    throw new InvalidDataException("A population account collides with a real player account.");
                allocatedAccounts.UnionWith(playerAccounts);
                // Reserve persisted aliases before allocating new careers.
                ReserveEncounterAccounts();
                LoadPersonalities();
                LoadFeatures();
                InitializeIdentities();
                ImportNames();
                byAccount = population.Careers.Values.ToDictionary(x => x.AccountId);
                IndexIdentities();
                LoadEncounters();
                population.LastGenerationOrder = Math.Max(population.LastGenerationOrder,
                    encounters.Values.Select(e => e.GenerationOrder).DefaultIfEmpty().Max());
                RestoreLatestEquipment();
                foreach (var encounter in encounters.Values) TrimEncounter(encounter);
                LoadMilestones();
                // Receipts expire only after the corresponding alias is no longer accepted.
                foreach (int account in population.RaidObservations.Keys.Where(a => !encounters.ContainsKey(a)).ToArray())
                {
                    population.RaidObservations.Remove(account);
                    population.AppliedStories.Remove(account);
                }
                foreach (int account in population.AppliedStories.Keys.Where(a => !encounters.ContainsKey(a)).ToArray())
                    population.AppliedStories.Remove(account);
                string fingerprint = TemplateFingerprint();
                bool repairAssets = fingerprint != population.TemplateFingerprint;
                var preparing = new LoadProgress(logger, "Preparing careers", population.Careers.Count);
                foreach (var career in population.Careers.Values)
                {
                    preparing.Step();
                    EnsureAccountIdentity(career);
                    PreserveLifetime(career);
                    EnsureLife(career, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    // Roll now instead of starting everyone offline for the first 10-30 minutes.
                    career.IsOnline = RollOnline(career);
                    career.LastActivityUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    career.NextActivityUtc = career.LastActivityUtc + Random.Shared.Next(600, 1801);
                    EnsureDevelopment(career);
                    RepairAppearance(career);
                    if (repairAssets && assetFiles.Contains(career.Id))
                    {
                        RepairCareerAssets(career);
                        RepairShowcase(career);
                    }
                }
                preparing.Finish();
                population.TemplateFingerprint = fingerprint;
                EnsureCareerCoverage();
                SeedMissingLoadouts();
                Simulate(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                var developing = new LoadProgress(logger, "Developing careers", population.Careers.Count);
                foreach (var career in population.Careers.Values)
                {
                    developing.Step();
                    Develop(career);
                    TryPrestige(career, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                }
                developing.Finish();
                Active = this;
                fikaPresent = AppDomain.CurrentDomain.GetAssemblies()
                    .Any(a => a.GetType("FikaServer.Controllers.FikaDialogueController", throwOnError: false) is not null);
                harmony = new Harmony("com.local.harmony-profiles.server");
                ConfigureRequiredApbs(harmony);
                ServerPatches.Install(harmony);
                InstallApbsIntegration();
                socialHarmony = new Harmony("com.local.harmony-profiles.social");
                try
                {
                    SocialPatches.Install(socialHarmony);
                    SocialReady = true;
                }
                catch (Exception ex)
                {
                    socialHarmony.UnpatchSelf();
                    logger.Warning("[Harmony-Profiles] Social integration disabled; profiles and progression remain available. " + ex.Message);
                }
                SaveChangedAssets();
                SavePopulation();
                Ready = true;
                checkpoint = new Timer(_ => Checkpoint(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
                logger.Info($"[Harmony-Profiles] Ready: {population.Careers.Count} PMC identities, {encounters.Count} encounter profiles. Protocol 1.");
            }
            catch (Exception ex)
            {
                Ready = false;
                Active = null;
                harmony?.UnpatchSelf();
                apbsHarmony?.UnpatchSelf();
                socialHarmony?.UnpatchSelf();
                SocialReady = false;
                ownership?.Dispose();
                logger.Error($"[Harmony-Profiles] Disabled; native behavior retained. Existing data was not replaced. {ex}");
            }
        }
        return Task.CompletedTask;
    }

    private void ValidatePopulation()
    {
        if (population.SchemaVersion != 12)
            throw new InvalidDataException("Population data is from an older build and has no migration; delete the mod's Data folder.");
        if (population.TemplateFingerprint is null || population.CoverageVersion is < 0 or > 1 || population.Careers is null || population.Friendships is null || population.RaidObservations is null || population.AppliedStories is null || population.SessionEndedUtc is null || population.LastGenerationOrder is < 0 or >= 1_000_000_000_000)
            throw new InvalidDataException("Unsupported population schema; preserve Data and migrate explicitly.");
        foreach (var (account, observation) in population.RaidObservations)
        {
            if (observation is null || observation.AccountId != account || account is < 1_500_000_000 or >= 2_000_000_000
                || !IsId(observation.RuntimeProfileId) || !Guid.TryParseExact(observation.RaidId, "N", out _)
                || observation.ActiveSeconds is < 0 or > 21600 || observation.PmcKills is < 0 or > 512
                || observation.ScavKills is < 0 or > 512 || observation.UsecKills is < 0 or > 512
                || observation.BearKills is < 0 or > 512 || observation.UsecKills + observation.BearKills > observation.PmcKills)
                throw new InvalidDataException("Invalid raid observation receipt; preserve Data and restore a known-good backup.");
        }
        foreach (var (account, outcome) in population.AppliedStories)
            if (account is < 1_500_000_000 or >= 2_000_000_000 || outcome is not ("Died" or "DiedUncounted" or "Regeared" or "ObservedDeath" or "Superseded" or "NoLoadoutAvailable" or "ConfirmedDeath"))
                throw new InvalidDataException("Invalid story receipt; preserve Data and restore a known-good backup.");
        if (population.SessionEndedUtc.Any(s => !IsId(s.Key) || s.Value < 0))
            throw new InvalidDataException("Invalid saved raid-end state.");
        var accounts = new HashSet<int>();
        var names = new HashSet<string>();
        var publicNames = new HashSet<string>();
        foreach (var (id, c) in population.Careers)
        {
            if (id != c.Id || !IsId(id) || c.AccountId < 1_500_000_000 || c.AccountId >= 2_000_000_000
                || string.IsNullOrWhiteSpace(c.Name) || c.Side is not ("Usec" or "Bear")
                || !accounts.Add(c.AccountId) || !names.Add(NameKey(c.Side, SourceName(c)))
                || (population.SchemaVersion >= 7 && (string.IsNullOrWhiteSpace(c.SourceName) || !publicNames.Add(NameKey("", c.Name))))
                || c.Observed is null || c.Observed.Raids < 0 || c.Observed.Deaths < 0 || c.Observed.Deaths > c.Observed.Raids
                || c.FeatureVersion is < 0 or > 1 || c.PrestigeLevel is < 0 or > 100 || c.PrestigeHistory is null
                || c.PrestigeHistory.Count > 100 || c.PrestigeHistory.Any(p => p is null || p.Level < 1 || p.Level > c.PrestigeLevel
                    || !IsId(p.DefinitionId) || p.Utc < 0 || !double.IsFinite(p.LifetimeHours) || p.LifetimeHours < 0
                    || p.PreviousExperience < 0 || p.CarriedItems < 0)
                || c.PrestigeHistory.Select(p => p.Level).Distinct().Count() != c.PrestigeHistory.Count
                || !double.IsFinite(c.PrestigeCycleStartSeconds) || c.PrestigeCycleStartSeconds < 0
                || c.PrestigeCycleStartSeconds > c.RaidSeconds + c.Observed.ActiveSeconds
                || c.SkillCarry is null || c.SkillCarry.Any(s => !double.IsFinite(s.Value) || s.Value < 0 || s.Value > 5100)
                || c.ShowcaseAreas is null || c.ShowcaseAreas.Count > 128 || c.ShowcaseAreas.Any(s => !IsId(s.Key) || s.Value is not (16 or 24))
                || c.Edition is null || c.GameVersion is null
                || (c.Edition.Length > 0 && (!editions.Contains(c.Edition) || c.GameVersion.Length is < 1 or > 64))
                || c.MemberCategory is < 0 or > 2047 || c.SelectedMemberCategory is < 0 or > 2047
                || (c.SelectedMemberCategory & c.MemberCategory) != c.SelectedMemberCategory
                || c.StoryDeaths < 0
                || c.Achievements is null || c.Achievements.Any(a => !IsId(a.Key) || a.Value <= 0)
                || (c.LastEquipmentAccount != 0 && c.LastEquipmentAccount is < 1_500_000_000 or >= 2_000_000_000)
                || c.LastEquipmentOrder is < 0 or >= 1_000_000_000_000 || c.SimulatedLoadoutOrder is < 0 or >= 1_000_000_000_000 || c.LastEquipmentGeneratedUtc < 0
                || c.Observed.UsecKills < 0 || c.Observed.BearKills < 0
                || c.Observed.UsecKills + c.Observed.BearKills > c.Observed.PmcKills
                || c.Observed.PmcKills < 0 || c.Observed.ScavKills < 0 || c.Observed.ActiveSeconds < 0
                || c.Observed.PendingSimulationSeconds < 0 || c.Observed.PendingSimulationSeconds > c.Observed.ActiveSeconds
                || c.Observed.Raids > 1_000_000_000 || c.Observed.PmcKills > 1_000_000_000 || c.Observed.ScavKills > 1_000_000_000
                || c.Observed.ActiveSeconds > 1_000_000_000_000
                || c.Raids < 0 || c.Survived < 0 || c.Survived > c.Raids || c.PmcKills < 0 || c.ScavKills < 0
                || !double.IsFinite(c.RaidSeconds) || c.RaidSeconds < 0
                || !double.IsFinite(c.OnlineSeconds) || c.OnlineSeconds < c.RaidSeconds
                || !double.IsFinite(c.DailyRaidHours) || c.DailyRaidHours < 0 || c.DailyRaidHours > 24
                || !double.IsFinite(c.SurvivalChance) || c.SurvivalChance < 0 || c.SurvivalChance > 1
                || !double.IsFinite(c.PendingRaidSeconds) || c.PendingRaidSeconds < 0 || c.PendingRaidSeconds >= 3600
                || c.LifetimeTotals is null || c.LifetimeTotals.Count > 64 || c.LifetimeTotals.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 128 || p.Value < 0)
                || c.GearVersion is < 0 or > 1 || c.LastGearUpdateUtc < 0 || c.NextGearAttemptUtc < 0
                || c.LifeVersion is < 0 or > 2 || !double.IsFinite(c.TargetRaidSeconds) || c.TargetRaidSeconds < 0 || c.TargetRaidSeconds > 3600
                || c.MissingRaids < 0 || c.AwolRaids < 0 || c.RunThroughRaids < 0
                || (long)c.MissingRaids + c.AwolRaids + c.RunThroughRaids + c.Survived > c.Raids
                || c.CurrentSurvivalStreak < 0 || c.BestSurvivalStreak < c.CurrentSurvivalStreak
                || !double.IsFinite(c.PendingLiveSeconds) || c.PendingLiveSeconds < 0
                || c.Experience < 0 || c.Customization is null
                || c.DevelopmentVersion is < 0 or > 2 || !double.IsFinite(c.WealthRubles) || c.WealthRubles < 0
                || !double.IsFinite(c.HideoutInvestment) || c.HideoutInvestment < 0
                || c.AreaLevels is null || c.AreaLevels.Any(a => a.Key is < 0 or > 1000 || a.Value is < 1 or > 100))
                throw new InvalidDataException($"Invalid career {id}; restore a known-good population backup.");
        }
    }

    private void ImportNames()
    {
        foreach (var c in population.Careers.Values) c.Retired = true;
        var known = population.Careers.Values.ToDictionary(c => NameKey(c.Side, c.SourceName));
        var pools = new[] { "Usec", "Bear" }.Select(side =>
        {
            if (!bots.Types.TryGetValue(side.ToLowerInvariant(), out var type) || type is null)
                throw new InvalidDataException($"Missing {side} name pool");
            return (Side: side, Type: type, Names: type.FirstNames.Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }).ToArray();
        var progress = new LoadProgress(logger, "Creating careers", pools.Sum(p => p.Names.Length));
        foreach (var (side, type, names) in pools)
        {
            foreach (var name in names)
            {
                progress.Step();
                string key = NameKey(side, name);
                if (!known.TryGetValue(key, out var career))
                {
                    career = NewCareer(side, name, type);
                    population.Careers.Add(career.Id, career);
                    known.Add(key, career);
                }
                career.Retired = false;
            }
        }
        progress.Finish();
    }

    private Career NewCareer(string side, string name, BotType type)
    {
        var personality = PickPersonality();
        string archetype = personality.Id;
        int level = Random.Shared.Next(personality.MinimumLevel, personality.MaximumLevel + 1);
        int floor = profileHelper.GetExperience(level) ?? 0;
        int ceiling = profileHelper.GetExperience(level + 1) ?? floor;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var c = new Career
        {
            Id = NewId(), AccountId = AllocateAccount(), Name = AllocateNickname(name), SourceName = name, Side = side, Archetype = archetype,
            DailyRaidHours = Random.Shared.NextDouble() < .15 ? 0 : personality.MinimumDailyHours
                + Random.Shared.NextDouble() * (personality.MaximumDailyHours - personality.MinimumDailyHours),
            Experience = floor + (int)((ceiling - floor) * Random.Shared.NextDouble() * .9)
        };
        c.SurvivalChance = SurvivalFor(c);
        var a = type.BotAppearance;
        var body = Pick(a.Body);
        var bodyTemplate = templates.Customization[body];
        var bodyGlobals = globals.Configuration.Customization.Body.FirstOrDefault(x => x.Key == bodyTemplate.Name.Trim()).Value;
        var hands = bodyGlobals?.IsNotRandom == true ? bodyGlobals.Hands : Pick(a.Hands);
        c.Customization = new JsonObject
        {
            ["Head"] = Pick(PlayableHeads(a.Head)).ToString(), ["Body"] = body.ToString(), ["Feet"] = Pick(a.Feet).ToString(),
            ["Hands"] = hands.ToString(), ["Voice"] = Pick(a.Voice).ToString()
        };
        EnsureAccountIdentity(c);
        c.LifeVersion = 2;
        c.FeatureVersion = 1;
        // History follows from the level: the raids this account needed to earn its experience.
        AddRaids(c, RaidsToReach(c, c.Experience) * (.85 + Random.Shared.NextDouble() * .35) * RaidDuration(c, false));
        c.RegisteredUtc = now - AccountAge(c, now);
        EnsureLife(c, now);
        return c;
    }

    private static MongoId Pick(Dictionary<MongoId, double> values)
    {
        var valid = values.Where(x => double.IsFinite(x.Value) && x.Value > 0).ToArray();
        double total = valid.Sum(x => x.Value);
        if (valid.Length == 0 || !double.IsFinite(total)) throw new InvalidDataException("Empty appearance pool");
        double roll = Random.Shared.NextDouble() * total;
        foreach (var item in valid) { roll -= item.Value; if (roll <= 0) return item.Key; }
        return valid[^1].Key;
    }

    private void Simulate(long now)
    {
        if (population.LastSimulationUtc == 0) { population.LastSimulationUtc = now; return; }
        // Never move a saved timestamp backwards. Short restarts do not reset the hourly threshold.
        long elapsed = now - population.LastSimulationUtc;
        if (elapsed < settings.ProgressionThresholdSeconds) return;
        double days = Math.Min(elapsed, settings.MaximumOfflineDays * 86400L) / 86400d;
        foreach (var c in population.Careers.Values.Where(c => !c.Retired))
        {
            bool friend = population.Friendships.Values.Any(links =>
                links.TryGetValue(c.Id, out var link) && link.Accepted && !link.Removed);
            double seconds = days * c.DailyRaidHours * 3600 * (friend ? settings.FriendActivityMultiplier : 1);
            // Observed raid time is already shown in the career. Simulate only the remaining activity budget.
            seconds = Math.Max(0, seconds - c.Observed.PendingSimulationSeconds - c.PendingLiveSeconds);
            c.PendingLiveSeconds = 0;
            c.Observed.PendingSimulationSeconds = 0;
            if (IsObservedActive(c, now) || now < c.StoryHoldUntilUtc || Random.Shared.NextDouble() < (friend ? .15 : .25)) continue;
            ApplySimulation(c, seconds);
        }
        population.LastSimulationUtc = now;
        dirty = true;
    }

    private void AddRaids(Career c, double seconds, bool earnExperience = false)
    {
        c.RaidSeconds += seconds;
        c.OnlineSeconds += seconds * OnlineFactor(c);
        c.PendingRaidSeconds += seconds;
        if (c.TargetRaidSeconds <= 0) c.TargetRaidSeconds = RaidDuration(c, true);
        while (c.PendingRaidSeconds >= c.TargetRaidSeconds)
        {
            c.PendingRaidSeconds -= c.TargetRaidSeconds;
            c.Raids++;
            bool survived = Random.Shared.NextDouble() < c.SurvivalChance;
            double outcome = Random.Shared.NextDouble();
            if (survived && outcome >= .015)
            {
                c.Survived++;
                c.CurrentSurvivalStreak++;
                c.BestSurvivalStreak = Math.Max(c.BestSurvivalStreak, c.CurrentSurvivalStreak);
            }
            else
            {
                c.CurrentSurvivalStreak = 0;
                if (survived) c.RunThroughRaids++;
                else if (outcome < .012) c.MissingRaids++;
                else if (outcome < .016) c.AwolRaids++;
                // Only fresh gear clears this. Surviving a later simulated raid must not bring the dead loadout back.
                if (!survived) c.NeedsRegear = true;
            }
            AddRaidKills(c);
            if (earnExperience)
                c.Experience = (int)Math.Min(int.MaxValue, c.Experience
                    + RaidExperience(Level(c.Experience), survived && outcome >= .015) * (.75 + Random.Shared.NextDouble() * .5));
            c.TargetRaidSeconds = RaidDuration(c, true);
        }
    }

    public void RecordGenerated(BotBase bot, BotGenerationDetails details, MongoId session)
    {
        lock (gate)
        {
            if (!Ready || !details.IsPmc || details.IsPlayerScav || bot.Info?.Nickname is null
                || bot.Info.Side is null || bot.Id is null || bot.Inventory?.Items is null) return;
            var selected = ApbsIntegration.Current?.LevelConfirmed == true ? ApbsIntegration.Current.Career : null;
            gearSession = session;
            Career? career = selected is not null && selected.Side.Equals(bot.Info.Side, StringComparison.OrdinalIgnoreCase)
                && selected.Name == bot.Info.Nickname ? selected : null;
            string key = NameKey(bot.Info.Side, bot.Info.Nickname);
            if (career is null && !bySourceName.TryGetValue(key, out career) && !byName.TryGetValue(key, out career))
            {
                // Covers names added by mods after startup. Never guess a match by stripping characters.
                if (!bots.Types.TryGetValue(bot.Info.Side.ToLowerInvariant(), out var type) || type is null) return;
                career = NewCareer(bot.Info.Side, bot.Info.Nickname, type);
                career.Experience = bot.Info.Experience ?? career.Experience;
                population.Careers.Add(career.Id, career);
                byName.Add(NameKey(career.Side, career.Name), career);
                bySourceName.Add(NameKey(career.Side, career.SourceName), career);
                byAccount.Add(career.AccountId, career);
                EnsureDevelopment(career);
                Develop(career);
                dirty = true;
                SavePopulation();
            }
            bot.Info.Nickname = career!.Name;
            if (selected?.Id == career.Id)
                ApplyAccountIdentity(career, bot.Info);

            var source = JsonNode.Parse(json.Serialize(bot)!)!.AsObject();
            var inventory = source["Inventory"]!.AsObject();
            var equipment = EquipmentTree(inventory);
            if (!ValidEquipment(equipment))
                throw new InvalidDataException("Generated equipment graph is not safe for profile viewing.");
            var encounter = new Encounter
            {
                AccountId = AllocateAccount(), CareerId = career.Id, SessionId = session.ToString(),
                RuntimeProfileId = bot.Id.Value.ToString(), GeneratedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                GenerationOrder = checked(++population.LastGenerationOrder),
                GeneratedExperience = bot.Info.Experience ?? 0, GeneratedLevel = bot.Info.Level ?? 1,
                GeneratedPrestige = bot.Info.PrestigeLevel ?? 0,
                Equipment = equipment, Customization = source["Customization"]!.DeepClone().AsObject()
            };
            // Persist the alias before publishing it to a bot. Runtime bot IDs and all inventory IDs stay native.
            WriteEncounter(encounter);
            encounters.Add(encounter.AccountId, encounter);
            bot.Aid = encounter.AccountId;
            RememberEquipment(career, encounter);
            TrimEncounter(encounter);
            career.NeedsRegear = false;
            career.LastGeneratedLevel = bot.Info.Level ?? 1;
            career.LastGeneratedExperience = bot.Info.Experience ?? 0;
            career.LastGeneratedPrestige = bot.Info.PrestigeLevel ?? 0;
            if (career.LastGeneratedPrestige > career.PrestigeLevel)
            {
                career.PrestigeLevel = career.LastGeneratedPrestige;
                career.PrestigeCycleStartSeconds = TotalSeconds(career);
            }
            EnsureShowcase(career);
            EnsureLife(career, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            dirty = true;
        }
    }

    public List<SearchFriendResponse> Search(string query, MongoId session)
    {
        lock (gate)
        {
            if (!Ready || string.IsNullOrWhiteSpace(query) || query.Length > 64) return [];
            return population.Careers.Values.Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || c.SourceName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
                .ThenBy(c => c.Name).ThenBy(c => c.Side).Take(50)
                .Select(c => new SearchFriendResponse
                {
                    Id = new MongoId(c.Id), Aid = c.AccountId,
                    Info = new UserDialogDetails { Nickname = c.Name, Side = c.Side, Level = Level(c.Experience),
                        MemberCategory = (SPTarkov.Server.Core.Models.Enums.MemberCategory)c.MemberCategory,
                        SelectedMemberCategory = (SPTarkov.Server.Core.Models.Enums.MemberCategory)c.SelectedMemberCategory }
                }).ToList();
        }
    }

    public GetOtherProfileResponse? View(int accountId, MongoId session)
    {
        lock (gate)
        {
            if (!Ready) return null;
            Encounter? encounter = null;
            if (!byAccount.TryGetValue(accountId, out var career))
            {
                if (!encounters.TryGetValue(accountId, out encounter) || !CanAccess(encounter, session)
                    || !population.Careers.TryGetValue(encounter.CareerId, out career)) return null;
            }
            if (encounter is null) RefreshSimulatedLoadout(career, session);
            EnsureShowcase(career);
            // Drops rack guns the current loadout now carries.
            RepairShowcase(career);
            QueueShowcase(career);
            var equipment = encounter is not null
                ? (EncounterDied(encounter) ? NakedEquipment(EncounterEquipment(encounter)) : EncounterEquipment(encounter))
                : career.NeedsRegear ? NakedEquipment(CareerEquipment(career)) : CareerEquipment(career);
            var (hideoutState, areaStashes, hideoutItems) = BuildHideout(career);
            var appearance = encounter?.Customization ?? career.Customization;
            var customization = new JsonObject();
            foreach (var (key, value) in appearance) customization[char.ToLowerInvariant(key[0]) + key[1..]] = value?.DeepClone();
            var counters = new JsonArray();
            void Counter(long value, params string[] key)
            {
                if (key[0] is not ("HarmonyProfilesSynthetic" or "CurrentWinStreak")) value = Lifetime(career, string.Join("/", key), value);
                counters.Add(new JsonObject { ["Key"] = new JsonArray(key.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()), ["Value"] = value });
            }
            Counter(1, "HarmonyProfilesSynthetic");
            Counter(career.MissingRaids, "ExitStatus", "MissingInAction", "Pmc");
            Counter(career.AwolRaids, "ExitStatus", "Left", "Pmc");
            Counter(career.RunThroughRaids, "ExitStatus", "Runner", "Pmc");
            Counter(career.CurrentSurvivalStreak, "CurrentWinStreak");
            Counter(career.BestSurvivalStreak, "LongestWinStreak");
            Counter(career.Raids + career.Observed.Raids, "Sessions", "Pmc");
            Counter(career.Survived, "ExitStatus", "Survived", "Pmc");
            Counter(career.Raids - career.Survived - career.MissingRaids - career.AwolRaids - career.RunThroughRaids + career.Observed.Deaths + career.StoryDeaths, "Deaths");
            Counter(career.Raids - career.Survived - career.MissingRaids - career.AwolRaids - career.RunThroughRaids + career.Observed.Deaths + career.StoryDeaths, "ExitStatus", "Killed", "Pmc");
            Counter((long)career.PmcKills + career.ScavKills + career.Observed.PmcKills + career.Observed.ScavKills, "Kills");
            Counter(career.PmcKills + career.Observed.PmcKills, "KilledPmc");
            Counter(career.ScavKills + career.Observed.ScavKills, "KilledSavage");
            var response = new JsonObject
            {
                ["id"] = career.Id, ["aid"] = accountId,
                ["info"] = new JsonObject { ["nickname"] = career.Name, ["side"] = career.Side,
                    ["prestigeLevel"] = encounter?.GeneratedPrestige ?? career.PrestigeLevel, ["experience"] = encounter?.GeneratedExperience ?? career.Experience, ["memberCategory"] = career.MemberCategory, ["selectedMemberCategory"] = career.SelectedMemberCategory, ["gameVersion"] = career.GameVersion, ["bannedState"] = false,
                    ["bannedUntil"] = 0, ["registrationDate"] = career.RegisteredUtc },
                ["customization"] = customization, ["skills"] = CareerSkills(career),
                ["equipment"] = equipment.DeepClone(),
                ["achievements"] = PublicAchievements(career), ["favoriteItems"] = PublicShowcase(career),
                ["pmcStats"] = new JsonObject { ["eft"] = new JsonObject { ["totalInGameTime"] = Lifetime(career, "OnlineTime", (long)OnlineSeconds(career)),
                    ["overAllCounters"] = new JsonObject { ["Items"] = counters } } },
                ["scavStats"] = new JsonObject { ["eft"] = new JsonObject { ["totalInGameTime"] = 0,
                    ["overAllCounters"] = new JsonObject { ["Items"] = new JsonArray() } } },
                ["hideout"] = hideoutState,
                ["customizationStash"] = career.CustomizationStashId,
                ["hideoutAreaStashes"] = areaStashes,
                ["items"] = hideoutItems
            };
            return json.Deserialize<GetOtherProfileResponse>(response.ToJsonString());
        }
    }

    public bool Capture(KillerReport report, MongoId session)
    {
        lock (gate)
        {
            if (!Ready || report.ProtocolVersion != 1 || report.RaidId.Length is < 1 or > 64
                || !encounters.TryGetValue(report.AccountId, out var e) || !CanAccess(e, session)
                || e.RuntimeProfileId != report.RuntimeProfileId || e.KillerCaptured) return false;
            if (!ValidEquipment(report.Equipment)) return false;
            // Customization remains the server-generated set. Only live equipment is client-authored.
            var updated = new Encounter
            {
                AccountId = e.AccountId, CareerId = e.CareerId, SessionId = e.SessionId,
                RuntimeProfileId = e.RuntimeProfileId, GeneratedUtc = e.GeneratedUtc,
                GenerationOrder = e.GenerationOrder,
                GeneratedExperience = e.GeneratedExperience, GeneratedLevel = e.GeneratedLevel, GeneratedPrestige = e.GeneratedPrestige,
                Equipment = report.Equipment.DeepClone().AsObject(), Customization = e.Customization,
                KillerCaptured = true, RaidId = report.RaidId
            };
            ScheduleStory(updated, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            WriteEncounter(updated);
            encounters[e.AccountId] = updated;
            // An older killer report must not replace the loadout from a newer generated encounter.
            if (population.Careers.TryGetValue(e.CareerId, out var career) && career.LastEquipmentAccount == e.AccountId)
                RememberEquipment(career, updated);
            TrimEncounter(updated);
            return true;
        }
    }

    private static JsonObject EquipmentTree(JsonObject inventory)
    {
        string root = inventory["equipment"]!.GetValue<string>();
        var items = inventory["items"]!.AsArray();
        var included = new HashSet<string> { root };
        bool changed;
        do
        {
            changed = false;
            foreach (var item in items.OfType<JsonObject>())
            {
                string? id = item["_id"]?.GetValue<string>();
                string? parent = item["parentId"]?.GetValue<string>();
                if (id is not null && parent is not null && included.Contains(parent) && included.Add(id)) changed = true;
            }
        } while (changed);
        // Native bot inventories contain stash/quest/sorting/customization roots as well as equipment.
        // Validate and publish only the equipment subtree; leave the generated inventory untouched.
        return new JsonObject { ["Id"] = root, ["Items"] = new JsonArray(items.OfType<JsonObject>()
            .Where(item => included.Contains(item["_id"]!.GetValue<string>())
                && (item["_id"]!.GetValue<string>() == root
                    ? string.IsNullOrEmpty(item["parentId"]?.GetValue<string>())
                    : item["parentId"] is JsonValue parent && included.Contains(parent.GetValue<string>())))
            .Select(item => item.DeepClone()).ToArray()) };
    }

    private bool ValidEquipment(JsonObject equipment)
    {
        if (equipment["Id"] is not JsonValue rootValue || !rootValue.TryGetValue<string>(out var root)
            || !IsId(root) || equipment["Items"] is not JsonArray items || items.Count is < 1 or > 1024) return false;
        var parents = new Dictionary<string, string?>();
        foreach (var item in items)
        {
            if (item is not JsonObject obj || obj["_id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id)
                || !IsId(id) || obj["_tpl"] is not JsonValue tplValue || !tplValue.TryGetValue<string>(out var tpl)
                || !IsId(tpl) || !templates.Items.ContainsKey(new MongoId(tpl)) || parents.ContainsKey(id)) return false;
            string? parent = obj["parentId"]?.GetValue<string>();
            parents.Add(id, parent);
        }
        if (!parents.TryGetValue(root!, out var rootParent) || !string.IsNullOrEmpty(rootParent)) return false;
        foreach (var id in parents.Keys)
        {
            var visited = new HashSet<string>();
            string? current = id;
            while (current != root)
            {
                if (current is null || !visited.Add(current) || !parents.TryGetValue(current, out current)) return false;
            }
        }
        return true;
    }

    private int Level(int xp) => profileHelper.GetLevelFromExperience(xp);

    private int AllocateAccount()
    {
        int id;
        do { id = RandomNumberGenerator.GetInt32(1_500_000_000, 2_000_000_000); } while (!allocatedAccounts.Add(id));
        return id;
    }

    private void ReserveEncounterAccounts()
    {
        string path = Path.Combine(directory, "encounters");
        Directory.CreateDirectory(path);
        foreach (var file in Directory.EnumerateFiles(path, "*.json"))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out int account)
                || account < 1_500_000_000 || account >= 2_000_000_000 || !allocatedAccounts.Add(account))
                throw new InvalidDataException($"Encounter account collision or invalid filename: {file}");
        }
    }

    private void LoadEncounters()
    {
        string path = Path.Combine(directory, "encounters");
        Directory.CreateDirectory(path);
        foreach (string file in Directory.EnumerateFiles(path, "*.json"))
        {
            var e = JsonSerializer.Deserialize<Encounter>(File.ReadAllText(file))
                ?? throw new InvalidDataException($"Empty encounter {file}");

            if (e.SchemaVersion != 1 || e.GenerationOrder is < 0 or >= 1_000_000_000_000 || !population.Careers.ContainsKey(e.CareerId)
                || byAccount.ContainsKey(e.AccountId) || !IsId(e.RuntimeProfileId)
                || !IsId(e.SessionId))
                throw new InvalidDataException($"Invalid encounter {file}");
            if (Path.GetFileNameWithoutExtension(file) != e.AccountId.ToString())
                throw new InvalidDataException($"Encounter filename/account mismatch: {file}");
            if (!ValidEquipment(e.Equipment))
            {
                e.Equipment = RepairEquipment(e.Equipment);
                WriteEncounter(e);
                logger.Warning("[Harmony-Profiles] Repaired encounter equipment after template removal: " + e.AccountId);
            }
            long retention = e.KillerCaptured ? 30L * 86400 : 7L * 86400;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - e.GeneratedUtc > retention)
            {
                File.Delete(file);
                File.Delete(file + ".bak");
                continue;
            }
            encounters.Add(e.AccountId, e);
        }
    }

    private void Checkpoint()
    {
        (long Sequence, byte[] Utf8)? snapshot = null;
        List<AssetSnapshot> assets = [];
        List<GearRequest> gear;
        lock (gate)
        {
            if (!Ready) return;
            try
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                ApplyStories(now);
                AdvanceOnline(now);
                Simulate(now);
                ProcessProfileEquipment(now);
                assets = SnapshotChangedAssets();
                if (dirty) snapshot = SnapshotPopulation();
            }
            catch (Exception ex) { logger.Error($"[Harmony-Profiles] Checkpoint failed; retaining in-memory changes for retry. {ex.Message}"); }
            try { gear = TakeGearRequests(DateTimeOffset.UtcNow.ToUnixTimeSeconds()); }
            catch (Exception ex) { Report(ex); gear = []; }
        }
        // Disk and APBS work run outside the gate so bot generation and profile requests never wait on them.
        try
        {
            foreach (var asset in assets) WriteAssets(asset);
            lock (gate) foreach (var asset in assets) Saved(asset);
        }
        catch (Exception ex) { logger.Error($"[Harmony-Profiles] Career file save failed; retaining in-memory changes for retry. {ex.Message}"); }
        if (snapshot is { } pending)
        {
            try { WritePopulation(pending); }
            catch (Exception ex)
            {
                lock (gate) dirty = true;
                logger.Error($"[Harmony-Profiles] Checkpoint save failed; retaining in-memory changes for retry. {ex.Message}");
            }
        }
        foreach (var request in gear) GenerateRequestedGear(request);
    }

    // Bytes and streams keep the large population file out of UTF-16 strings.
    private static Population? ReadPopulation(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<Population>(stream);
    }

    private (long Sequence, byte[] Utf8) SnapshotPopulation()
    {
        dirty = false;
        return (++saveSequence, JsonSerializer.SerializeToUtf8Bytes(population));
    }

    // Snapshots are taken under the gate, so a higher sequence is always newer. Never let an older one land last.
    private void WritePopulation((long Sequence, byte[] Utf8) snapshot)
    {
        lock (saveGate)
        {
            if (snapshot.Sequence <= writtenSequence) return;
            AtomicFile.Write(Path.Combine(directory, "population.json"), snapshot.Utf8);
            writtenSequence = snapshot.Sequence;
        }
    }

    private void SavePopulation()
    {
        try { WritePopulation(SnapshotPopulation()); }
        catch { dirty = true; throw; }
    }

    // Under Fika the headless generates every bot, so its session never matches a player's.
    // The runtime profile ID checks still prove the reporter saw that bot in raid.
    private bool EncounterDied(Encounter e) =>
        population.RaidObservations.TryGetValue(e.AccountId, out var observation) && observation.Died
        || population.AppliedStories.GetValueOrDefault(e.AccountId) is "Died" or "DiedUncounted" or "ConfirmedDeath";

    private bool CanAccess(Encounter e, MongoId session) => fikaPresent || e.SessionId == session.ToString();

    // Written on every PMC spawn, so skip the fsync. Losing one to a power cut only drops that alias.
    private void WriteEncounter(Encounter e)
    {
        if (e.Trimmed) throw new InvalidOperationException("A trimmed encounter has no equipment to write.");
        AtomicFile.Write(Path.Combine(directory, "encounters", e.AccountId + ".json"), JsonSerializer.Serialize(e), durable: false);
    }
    private static string NameKey(string side, string name) => side.ToUpperInvariant() + "\n" + name.Normalize(NormalizationForm.FormC).ToUpperInvariant();
    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
    private static bool IsId(string? value) => value is { Length: 24 } && value.All(Uri.IsHexDigit);
    private static JsonObject EmptyEquipment(string? id = null)
    {
        id ??= NewId();
        return new JsonObject { ["Id"] = id, ["Items"] = new JsonArray(new JsonObject
            { ["_id"] = id, ["_tpl"] = "55d7217a4bdc2d86028b456d" }) };
    }

    public void Report(Exception ex) => logger.Error($"[Harmony-Profiles] Operation failed; native fallback retained. {ex}");

    public void Dispose()
    {
        lock (gate)
        {
            checkpoint?.Dispose();
            Ready = false;
            Active = null;
            harmony?.UnpatchSelf();
            apbsHarmony?.UnpatchSelf();
            socialHarmony?.UnpatchSelf();
            SocialReady = false;
            try
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                ApplyStories(now);
                AdvanceOnline(now);
                Simulate(now);
                SaveChangedAssets();
                if (dirty) SavePopulation();
            }
            catch (Exception ex) { logger.Error($"[Harmony-Profiles] Final save failed. {ex}"); }
            ownership?.Dispose();
        }
    }
}
