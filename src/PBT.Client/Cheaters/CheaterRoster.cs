using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.Communications;
using EFT.HealthSystem;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using UnityEngine;

namespace PBT.Client.Cheaters;

[Flags]
internal enum Cheat
{
    None = 0,
    Aimbot = 1,
    Godmode = 2,
    Speedhack = 4,
    Spinbot = 8,
    BunnyHop = 16,
    FightTeleport = 32,
    Fly = 64,
    Vacuum = 128,
    Stalker = 256,
    VoiceSpam = 512,
    Esp = 1024,
    All = 2047,
}

// What a cheater is after. It decides which cheats the cheater can roll and how it plays.
internal enum CheaterType
{
    Random,
    Closet,
    Looter,
    Hunter,
    Rage,
    Troll,
}

// How a cheater type aims. Turn speed in degrees a second, reaction in seconds, the cone (degrees) its aimbot snaps within,
// then per-shot hit and headshot chances, trigger hold and pause in seconds, and the rest it takes after a kill.
internal readonly record struct AimProfile(
    float Turn,
    float ReactMin,
    float ReactMax,
    float Snap,
    float Hit,
    float Head,
    float BurstMin,
    float BurstMax,
    float PauseMin,
    float PauseMax,
    float RestMin,
    float RestMax
);

// How a cheater type hunts: how far its ESP hunts, kills it goes looking for in a raid, seconds it roams between hunts,
// chance a scav is worth chasing, and seconds without getting anywhere before it gives up on a target.
internal readonly record struct HuntProfile(
    float Range,
    int KillsMin,
    int KillsMax,
    float GapMin,
    float GapMax,
    float Scavs,
    float GiveUp
);

// Which bots in this raid are cheaters, their type and the cheats they rolled. The movement itself lives in CheaterBrain, which needs BigBrain.
internal static class CheaterRoster
{
    private static readonly (Cheat Cheat, string Key)[] Chances =
    [
        (Cheat.Aimbot, "Aimbot"),
        (Cheat.Esp, "Esp"),
        (Cheat.Godmode, "Godmode"),
        (Cheat.Speedhack, "Speedhack"),
        (Cheat.Spinbot, "Spinbot"),
        (Cheat.BunnyHop, "BunnyHop"),
        (Cheat.FightTeleport, "FightTeleport"),
        (Cheat.Fly, "Fly"),
        (Cheat.Vacuum, "Vacuum"),
        (Cheat.Stalker, "Stalker"),
        (Cheat.VoiceSpam, "VoiceSpam"),
    ];

    // Per type: cheats it always has, and the ones it may roll.
    private static readonly Dictionary<CheaterType, (Cheat Always, Cheat May)> Kits = new()
    {
        [CheaterType.Closet] = (Cheat.None, Cheat.Aimbot | Cheat.Esp),
        [CheaterType.Looter] = (Cheat.None, Cheat.Vacuum | Cheat.Esp | Cheat.Aimbot | Cheat.Speedhack),
        [CheaterType.Hunter] = (
            Cheat.Esp | Cheat.Aimbot,
            Cheat.Speedhack | Cheat.BunnyHop | Cheat.FightTeleport | Cheat.Godmode | Cheat.VoiceSpam
        ),
        [CheaterType.Rage] = (Cheat.Aimbot, Cheat.All & ~(Cheat.Vacuum | Cheat.Stalker)),
        [CheaterType.Troll] = (
            Cheat.Stalker | Cheat.VoiceSpam,
            Cheat.Godmode | Cheat.FightTeleport | Cheat.Speedhack | Cheat.Spinbot | Cheat.Aimbot
        ),
    };

    // Closet cheaters aim about as fast as a good player and miss plenty. Rage cheaters snap hard but still have to turn and react.
    private static readonly Dictionary<CheaterType, AimProfile> Profiles = new()
    {
        [CheaterType.Closet] = new(300, .35f, .6f, 4, .5f, .25f, .12f, .3f, .35f, .7f, 30, 60),
        [CheaterType.Looter] = new(360, .35f, .6f, 6, .55f, .3f, .15f, .35f, .3f, .6f, 30, 60),
        [CheaterType.Troll] = new(450, .3f, .5f, 10, .65f, .4f, .2f, .45f, .25f, .5f, 15, 30),
        [CheaterType.Hunter] = new(540, .25f, .45f, 12, .75f, .5f, .25f, .5f, .2f, .45f, 12, 25),
        [CheaterType.Rage] = new(900, .15f, .3f, 25, .85f, .7f, .4f, .8f, .15f, .35f, 4, 10),
    };

    // Looters never hunt. Closet cheaters only set up on someone close. Hunters go for a handful of players with long roams in between. Rage keeps going.
    private static readonly Dictionary<CheaterType, HuntProfile> Hunts = new()
    {
        [CheaterType.Closet] = new(80, 2, 4, 60, 150, .2f, 40),
        [CheaterType.Looter] = new(0, 0, 0, 0, 0, 0, 30),
        [CheaterType.Troll] = new(150, 1, 2, 90, 180, 0, 60),
        [CheaterType.Hunter] = new(400, 3, 6, 45, 120, .25f, 90),
        [CheaterType.Rage] = new(250, 8, 14, 15, 45, 1, 60),
    };

    private static ConfigEntry<bool>? debugLog;
    private static ConfigEntry<Cheat>? forced;
    private static ConfigEntry<CheaterType>? forcedType;
    private static ConfigEntry<int>? spawnedCheaters;
    private static readonly HashSet<int> accounts = new();
    private static readonly Dictionary<int, (CheaterType Type, Cheat Cheats)> rolled = new();
    private static readonly Dictionary<int, float> nextLine = new();
    private static readonly Dictionary<int, float> nextEsp = new();

    // Suspicion per account. Blatant play builds it up and the in-raid anticheat bans on it.
    private static readonly Dictionary<int, float> heat = new();
    private static readonly Dictionary<Player, float> lastHit = new();
    internal static float RaidStart;
    private static readonly HashSet<Player> speeders = new();
    private static readonly HashSet<Player> driven = new();
    private static readonly Dictionary<Player, Player> aims = new();
    private static readonly List<Player> alive = new();
    private static readonly AccessTools.FieldRef<MovementContext, Player> contextPlayer = AccessTools.FieldRefAccess<
        MovementContext,
        Player
    >("_player");
    private static JObject play = new();
    private static bool behaviour,
        fetching;
    private static float nextRefresh,
        nextBanRoll;
    private static int picked;
    internal static bool BrainInstalled;

    internal static bool Behaviour => behaviour && BrainInstalled;
    internal static float TargetsYou => play.Value<float?>("TargetsYou") ?? .35f;
    internal static float Rivalry => play.Value<float?>("Rivalry") ?? .5f;

    // 1 at the default accuracy of 0.7.
    private static float Accuracy => Mathf.Clamp01(play.Value<float?>("AimAccuracy") ?? .7f) / .7f;
    internal static float ReactionScale => Mathf.Clamp(play.Value<float?>("ReactionTime") ?? 1f, .5f, 3f);

    internal static HuntProfile HuntOf(Player player) =>
        Hunts.TryGetValue(TypeOf(player), out var hunt) ? hunt : Hunts[CheaterType.Closet];

    internal static AimProfile AimOf(Player player) =>
        Profiles.TryGetValue(TypeOf(player), out var aim) ? aim : Profiles[CheaterType.Closet];

    private static float SpeedMultiplier => Mathf.Clamp(play.Value<float?>("SpeedMultiplier") ?? 2.2f, 1, 6);

    internal static void Install(Harmony harmony, ConfigFile config)
    {
        debugLog = config.Bind(
            "Cheaters (debug)",
            "Log cheater behaviour",
            false,
            "Write what each cheater bot is doing to the BepInEx console."
        );
        forcedType = config.Bind(
            "Cheaters (debug)",
            "Force type",
            CheaterType.Random,
            "Every cheater is this type instead of one rolled from the server's shares."
        );
        forced = config.Bind(
            "Cheaters (debug)",
            "Force cheat",
            Cheat.None,
            "Every cheater gets this cheat on top of what it rolled, whatever its type. All gives every cheat. The chances themselves are on the server's Cheaters page."
        );
        spawnedCheaters = config.Bind(
            "Cheaters (debug)",
            "First spawned PMCs that cheat",
            0,
            new ConfigDescription(
                "Turns this many of the first PBT PMCs that actually spawn into cheaters, so you meet one quickly. They keep their normal kit. 0 is off.",
                new AcceptableValueRange<int>(0, 10)
            )
        );

        var damage = AccessTools.Method(typeof(Player), nameof(Player.ApplyDamageInfo));
        if (damage is null)
            Plugin.Report("Cheater godmode unavailable.");
        else
            harmony.Patch(damage, prefix: new HarmonyMethod(typeof(CheaterRoster), nameof(Damage)));

        var shot = AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.InitiateShot));
        if (shot is null)
            Plugin.Report("Cheater aimbot unavailable.");
        else
            harmony.Patch(shot, prefix: new HarmonyMethod(typeof(CheaterRoster), nameof(Aimbot)));

        var motion = AccessTools.Method(typeof(MovementContext), nameof(MovementContext.DirectApplyMotion));
        if (motion is null)
            Plugin.Report("Cheater speedhack unavailable.");
        else
            harmony.Patch(
                motion,
                prefix: new HarmonyMethod(typeof(CheaterRoster), nameof(Speedhack)),
                postfix: new HarmonyMethod(typeof(CheaterRoster), nameof(SpeedhackDone))
            );
    }

    internal static void RaidStarted()
    {
        accounts.Clear();
        rolled.Clear();
        nextLine.Clear();
        nextEsp.Clear();
        heat.Clear();
        lastHit.Clear();
        RaidStart = Time.time;
        speeders.Clear();
        driven.Clear();
        aims.Clear();
        CheaterMind.Clear();
        nextRefresh = 0;
        nextBanRoll = 0;
        picked = 0;
    }

    internal static void Log(string message)
    {
        if (debugLog?.Value == true)
            Plugin.Trace("[Cheater] " + message);
    }

    private static int Account(Player? player) =>
        player?.Profile?.AccountId is { } id && int.TryParse(id, out int account) ? account : 0;

    internal static bool IsCheater(Player? player) => player is not null && accounts.Contains(Account(player));

    private static (CheaterType Type, Cheat Cheats) Roll(Player player)
    {
        int account = Account(player);
        if (rolled.TryGetValue(account, out var known))
            return known;

        var type = forcedType?.Value is { } f and not CheaterType.Random ? f : RollType(account);
        var (always, may) = Kits[type];
        var cheats = always;
        int salt = 10;
        foreach (var (cheat, key) in Chances)
            if ((may & cheat) != 0 && Hash(account, salt++) < (play.Value<float?>(key) ?? 0))
                cheats |= cheat;
        // A closet cheater that rolled nothing still runs one of the two.
        if (type == CheaterType.Closet && cheats == Cheat.None)
            cheats = Hash(account, 30) < .5f ? Cheat.Aimbot : Cheat.Esp;
        rolled[account] = (type, cheats);
        Log($"{player.Profile.Nickname} is a {type} cheater with {cheats}.");
        return (type, cheats);
    }

    private static CheaterType RollType(int account)
    {
        var types = play["Types"] as JObject;
        (CheaterType Type, float Share)[] shares =
        [
            (CheaterType.Closet, types?.Value<float?>("Closet") ?? .4f),
            (CheaterType.Looter, types?.Value<float?>("Looter") ?? .25f),
            (CheaterType.Hunter, types?.Value<float?>("Hunter") ?? .2f),
            (CheaterType.Rage, types?.Value<float?>("Rage") ?? .1f),
            (CheaterType.Troll, types?.Value<float?>("Troll") ?? .05f),
        ];
        float total = shares.Sum(s => Math.Max(0, s.Share));
        if (total <= 0)
            return CheaterType.Closet;
        float roll = Hash(account, 1) * total;
        foreach (var (type, share) in shares)
            if ((roll -= Math.Max(0, share)) < 0)
                return type;
        return CheaterType.Closet;
    }

    internal static CheaterType TypeOf(Player player) => Roll(player).Type;

    internal static Cheat CheatsOf(Player player) => Roll(player).Cheats | (forced?.Value ?? Cheat.None);

    internal static bool Has(Player player, Cheat cheat) => (CheatsOf(player) & cheat) != 0;

    internal static float SinceHit(Player player) => lastHit.TryGetValue(player, out float t) ? Time.time - t : float.MaxValue;

    internal static void Suspect(Player player, float amount)
    {
        int account = Account(player);
        heat[account] = heat.TryGetValue(account, out float h) ? h + amount : amount;
    }

    // Set by the brain while it drives the bot, so ESP reports only go to bots on their normal AI.
    internal static void SetDriven(Player bot, bool on)
    {
        if (on)
            driven.Add(bot);
        else
            driven.Remove(bot);
    }

    // Every shot from this bot goes to the target until cleared.
    internal static void Aim(Player bot, Player? target)
    {
        if (target is null)
            aims.Remove(bot);
        else
            aims[bot] = target;
    }

    private static float Hash(int account, int salt) => (uint)((account * 2654435761u) ^ (uint)(salt * 40503)) % 10000 / 10000f;

    // Hits feed the anticheat: a cheater landing headshots, or one that soaks bullets with godmode.
    private static bool Damage(Player __instance, DamageInfo damageInfo, EBodyPart bodyPartType)
    {
        if (!Behaviour || accounts.Count == 0)
            return true;
        if (damageInfo.Player?.iPlayer is Player shooter && shooter != __instance && IsCheater(shooter))
            Suspect(shooter, bodyPartType == EBodyPart.Head ? .35f : .12f);
        if (!IsCheater(__instance))
            return true;
        lastHit[__instance] = Time.time;
        if (!Has(__instance, Cheat.Godmode))
            return true;
        Suspect(__instance, .3f);
        return false;
    }

    // Bends a shot onto the target only if the gun already points roughly at it, the type's hit roll comes up and the line is clear.
    // So an aimbot still has to turn first, misses now and then, and never shoots through walls.
    private static void Aimbot(Player.FirearmController __instance, Vector3 shotPosition, ref Vector3 shotDirection)
    {
        if (
            !behaviour
            || accounts.Count == 0
            || __instance._player is not { IsAI: true } shooter
            || !IsCheater(shooter)
            || !Has(shooter, Cheat.Aimbot | Cheat.Spinbot)
        )
            return;
        if (!aims.TryGetValue(shooter, out var target))
            target = shooter.AIData?.BotOwner?.Memory?.GoalEnemy?.Person as Player;
        if (target == null || target.HealthController?.IsAlive != true)
            return;

        var aim = AimOf(shooter);
        var point = (UnityEngine.Random.value < aim.Head ? target.PlayerBones.Head : target.PlayerBones.Ribcage).position;
        var want = (point - shotPosition).normalized;
        // A spinbot's body faces anywhere, so it snaps from any angle.
        if (Vector3.Angle(shotDirection, want) > (Has(shooter, Cheat.Spinbot) ? 180 : aim.Snap))
            return;
        if (UnityEngine.Random.value >= Mathf.Clamp01(aim.Hit * Accuracy))
            return;
        if (Physics.Linecast(shotPosition, point, LayersMaskController.HighPolyWithTerrainMask))
            return;
        shotDirection = want;
    }

    private static void Speedhack(MovementContext __instance, ref Vector3 motion, out float __state)
    {
        __state = float.NaN;
        if (speeders.Count == 0 || !speeders.Contains(contextPlayer(__instance)))
            return;
        float m = SpeedMultiplier;
        motion = new Vector3(motion.x * m, motion.y, motion.z * m);
        // The controller's own speed limit would clamp the boost away.
        __state = __instance.CharacterController.SpeedLimit;
        __instance.CharacterController.SpeedLimit = -1f;
    }

    private static void SpeedhackDone(MovementContext __instance, float __state)
    {
        if (!float.IsNaN(__state))
            __instance.CharacterController.SpeedLimit = __state;
    }

    internal static void Tick()
    {
        if (!Singleton<GameWorld>.Instantiated)
            return;
        float now = Time.unscaledTime;
        if (now >= nextRefresh && !fetching)
        {
            nextRefresh = now + 20;
            _ = Refresh();
        }

        speeders.Clear();
        if (!behaviour)
            return;
        alive.Clear();
        foreach (var player in Singleton<GameWorld>.Instance.AllAlivePlayersList)
        {
            if (player is not { IsAI: true })
                continue;
            // Generated bots spawn in random order, so the server's debug cheaters may never show up. This picks from those that do.
            if (
                picked < spawnedCheaters!.Value
                && player.Side is EPlayerSide.Usec or EPlayerSide.Bear
                && RaidActivity.IsPbtAccount(Account(player))
                && accounts.Add(Account(player))
            )
            {
                picked++;
                Log($"Debug: {player.Profile.Nickname} turned cheater on spawn.");
            }

            if (IsCheater(player))
                alive.Add(player);
        }

        bool banRoll = now >= nextBanRoll;
        if (banRoll)
            nextBanRoll = now + 1;
        foreach (var player in alive)
        {
            if (Has(player, Cheat.VoiceSpam))
                Spam(player, now);
            if (Has(player, Cheat.Vacuum))
                Vacuum(player);
            if (BrainInstalled && Has(player, Cheat.Speedhack))
            {
                speeders.Add(player);
                if (banRoll && player.Velocity.sqrMagnitude > 1)
                    Suspect(player, .08f);
            }

            if (Has(player, Cheat.Esp) && !driven.Contains(player))
                Esp(player, now);
            if (banRoll)
                MaybeBan(player);
        }
    }

    // A bot on its normal AI with ESP gets told where the nearest player is now and then, so it flanks and pushes like it can see through walls.
    private static void Esp(Player cheater, float now)
    {
        int account = Account(cheater);
        if (
            nextEsp.TryGetValue(account, out float next) && now < next
            || cheater.AIData?.BotOwner is not { BotsGroup: { } group } owner
        )
            return;
        nextEsp[account] = now + UnityEngine.Random.Range(5f, 10f);
        var mind = CheaterMind.Of(cheater);
        if (Time.time < mind.RestUntil)
            return;
        Player? found = null;
        float best = 80 * 80;
        foreach (var p in Singleton<GameWorld>.Instance.AllAlivePlayersList)
        {
            if (
                p is null
                || p == cheater
                || p.HealthController?.IsAlive != true
                || !string.IsNullOrEmpty(cheater.GroupId) && p.GroupId == cheater.GroupId
                || mind.Ignores(p)
                || !mind.Worth(cheater, p)
            )
                continue;
            float weight =
                !p.IsAI ? 1 / Math.Max(.05f, TargetsYou * 2)
                : IsCheater(p) ? 1 / Math.Max(.05f, Rivalry * 2)
                : 1;
            float d = (p.Position - cheater.Position).sqrMagnitude * weight;
            if (d < best)
            {
                best = d;
                found = p;
            }
        }

        if (found is null)
            return;
        try
        {
            if (!group.IsEnemy(found))
                group.AddEnemy(found, EBotEnemyCause.addPlayer);
            group.ReportAboutEnemy(found, EEnemyPartVisibleType.Sence, owner);
        }
        catch (Exception ex)
        {
            Log($"ESP report failed for {cheater.Profile.Nickname}: {ex.Message}");
        }
    }

    // The in-raid anticheat. An average cheater (about 5 suspicion) goes at the base rate; a quiet closet one at a third of it, a flying teleporter several times over.
    private static void MaybeBan(Player player)
    {
        float boost = play.Value<float?>("BlatantBanBoost") ?? 1;
        float suspicion = heat.TryGetValue(Account(player), out float h) ? h : 0;
        float factor = boost <= 0 ? 1 : Mathf.Clamp(.3f + suspicion * boost / 7f, .3f, 6f);
        float chance = (play.Value<float?>("BanPerMinute") ?? 0) / 60f * factor;
        if (chance <= 0 || UnityEngine.Random.value >= chance || player.ActiveHealthController is not { } health)
            return;
        Log($"{player.Profile.Nickname} got banned mid-raid at {suspicion:0.0} suspicion.");
        aims.Remove(player);
        health.Kill(EDamageType.Undefined);
        if (play.Value<bool?>("BanNotice") ?? true)
        {
            try
            {
                NotificationManager.DisplayMessageNotification(
                    $"{player.Profile.Nickname} was banned for cheating.",
                    ENotificationDurationType.Long,
                    ENotificationIconType.Alert
                );
            }
            catch (Exception ex)
            {
                Log("Ban notice failed: " + ex.Message);
            }
        }

        _ = ReportBan(Account(player));
    }

    // Lets the server retire the account and maybe bring the cheater back on a new one.
    private static async Task ReportBan(int account)
    {
        try
        {
            await RequestHandler.PostJsonAsync(
                "/pbt/raid/banned",
                new JObject { ["AccountId"] = account }.ToString(Newtonsoft.Json.Formatting.None)
            );
        }
        catch (Exception ex)
        {
            Log("Ban report failed: " + ex.Message);
        }
    }

    // Every few minutes pulls a handful of the most valuable loose items within 200 m straight into its bag, without moving.
    // Once the bag is "full" it extracts. Needs no BigBrain, so it runs for any cheater with the cheat.
    private static void Vacuum(Player player)
    {
        var mind = CheaterMind.Of(player);
        if (mind.Left || Time.time < mind.NextPull)
            return;
        mind.NextPull = Time.time + UnityEngine.Random.Range(90f, 240f);
        if (mind.Looted >= mind.Capacity)
        {
            mind.Left = true;
            Log($"{player.Profile.Nickname} vacuumed {mind.Looted} items and extracted.");
            player.AIData?.BotOwner?.LeaveData?.RemoveFromMap();
            return;
        }

        var world = Singleton<GameWorld>.Instance;
        var loot = world.LootItems;
        var picks = new List<(EFT.Interactive.LootItem Item, int Price)>();
        for (int i = 0; i < loot.Count; i++)
        {
            var item = loot.GetByIndex(i);
            if (item?.Item is null || item.Item.QuestItem || (item.transform.position - player.Position).sqrMagnitude > 200 * 200)
                continue;
            picks.Add((item, item.Item.Template.CreditsPrice * Math.Max(1, item.Item.StackObjectsCount)));
        }

        int take = Math.Min(UnityEngine.Random.Range(2, 6), mind.Capacity - mind.Looted);
        foreach (var (item, price) in picks.OrderByDescending(p => p.Price).Take(take))
        {
            Log(
                $"{player.Profile.Nickname} vacuumed {item.Item.TemplateId} ({price:N0} RUB) from {Vector3.Distance(item.transform.position, player.Position):0} m."
            );
            world.DestroyLoot(item);
            mind.Looted++;
            Suspect(player, 1);
        }

        // Nothing left nearby: that counts as a full bag.
        if (picks.Count == 0)
            mind.Looted = mind.Capacity;
    }

    // "Scav ahead" or "Hold fire" nonstop, lines piling over each other like a voice-line macro.
    private static void Spam(Player player, float now)
    {
        int account = Account(player);
        if (nextLine.TryGetValue(account, out float next) && now < next || player.Speaker is null)
            return;
        // Only within earshot, so far-off spammers do not tie up the voice pool.
        if (Singleton<GameWorld>.Instance.MainPlayer is not { } me || (me.Position - player.Position).sqrMagnitude > 80 * 80)
        {
            nextLine[account] = now + 1;
            return;
        }

        var phrase = Hash(account, 4) < .5f ? EPhraseTrigger.Scav : EPhraseTrigger.HoldFire;
        player.Speaker.Play(phrase, player.HealthStatus | ETagStatus.Combat, true, 1000);
        nextLine[account] = now + UnityEngine.Random.Range(.06f, .14f);
    }

    private static async Task Refresh()
    {
        fetching = true;
        try
        {
            var response = JObject.Parse(await RequestHandler.GetJsonAsync("/pbt/raid/cheaters"));
            if (response.Value<int>("ProtocolVersion") != 1)
                return;
            behaviour = response.Value<bool>("Behaviour");
            if (response["Play"] is JObject chances && !JToken.DeepEquals(chances, play))
            {
                play = chances;
                rolled.Clear();
            }

            foreach (var id in response["Cheaters"] ?? new JArray())
                if (accounts.Add(id.Value<int>()))
                    Log($"Account {id} is a cheater in this raid.");
        }
        catch (Exception ex)
        {
            Plugin.Report("Cheater roster unavailable: " + ex.Message);
        }
        finally
        {
            fetching = false;
        }
    }
}
