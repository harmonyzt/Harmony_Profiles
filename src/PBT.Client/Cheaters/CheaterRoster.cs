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
    All = 2047
}

// What a cheater is after. It decides which cheats the cheater can roll and how it plays.
internal enum CheaterType
{
    Random,
    Closet,
    Looter,
    Hunter,
    Rage,
    Troll
}

// Which bots in this raid are cheaters, their type and the cheats they rolled. The movement itself lives in CheaterBrain, which needs BigBrain.
internal static class CheaterRoster
{
    private static readonly (Cheat Cheat, string Key)[] Chances =
    [
        (Cheat.Aimbot, "Aimbot"), (Cheat.Esp, "Esp"), (Cheat.Godmode, "Godmode"), (Cheat.Speedhack, "Speedhack"), (Cheat.Spinbot, "Spinbot"),
        (Cheat.BunnyHop, "BunnyHop"), (Cheat.FightTeleport, "FightTeleport"), (Cheat.Fly, "Fly"), (Cheat.Vacuum, "Vacuum"), (Cheat.Stalker, "Stalker"),
        (Cheat.VoiceSpam, "VoiceSpam")
    ];

    // Per type: cheats it always has, and the ones it may roll.
    private static readonly Dictionary<CheaterType, (Cheat Always, Cheat May)> Kits = new()
    {
        [CheaterType.Closet] = (Cheat.None, Cheat.Aimbot | Cheat.Esp),
        [CheaterType.Looter] = (Cheat.Vacuum, Cheat.Esp | Cheat.Aimbot | Cheat.Speedhack),
        [CheaterType.Hunter] = (Cheat.Esp | Cheat.Aimbot, Cheat.Speedhack | Cheat.BunnyHop | Cheat.FightTeleport | Cheat.Godmode | Cheat.VoiceSpam),
        [CheaterType.Rage] = (Cheat.Aimbot, Cheat.All & ~(Cheat.Vacuum | Cheat.Stalker)),
        [CheaterType.Troll] = (Cheat.Stalker | Cheat.VoiceSpam, Cheat.Godmode | Cheat.FightTeleport | Cheat.Speedhack | Cheat.Spinbot | Cheat.Aimbot)
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
    private static readonly HashSet<Player> speeders = new();
    private static readonly HashSet<Player> driven = new();
    private static readonly Dictionary<Player, Player> aims = new();
    private static readonly List<Player> alive = new();
    private static readonly AccessTools.FieldRef<MovementContext, Player> contextPlayer = AccessTools.FieldRefAccess<MovementContext, Player>("_player");
    private static JObject play = new();
    private static bool behaviour, fetching;
    private static float nextRefresh, nextBanRoll;
    private static int picked;
    internal static bool BrainInstalled;

    internal static bool Behaviour => behaviour && BrainInstalled;
    internal static float TargetsYou => play.Value<float?>("TargetsYou") ?? .35f;
    internal static float Rivalry => play.Value<float?>("Rivalry") ?? .5f;
    private static float SpeedMultiplier => Mathf.Clamp(play.Value<float?>("SpeedMultiplier") ?? 2.2f, 1, 6);

    internal static void Install(Harmony harmony, ConfigFile config)
    {
        debugLog = config.Bind("Cheaters (debug)", "Log cheater behaviour", false, "Write what each cheater bot is doing to the BepInEx console.");
        forcedType = config.Bind("Cheaters (debug)", "Force type", CheaterType.Random, "Every cheater is this type instead of one rolled from the server's shares.");
        forced = config.Bind("Cheaters (debug)", "Force cheat", Cheat.None,
            "Every cheater gets this cheat on top of what it rolled, whatever its type. All gives every cheat. The chances themselves are on the server's Cheaters page.");
        spawnedCheaters = config.Bind("Cheaters (debug)", "First spawned PMCs that cheat", 0,
            new ConfigDescription("Turns this many of the first PBT PMCs that actually spawn into cheaters, so you meet one quickly. They keep their normal kit. 0 is off.",
                new AcceptableValueRange<int>(0, 10)));

        var damage = AccessTools.Method(typeof(Player), nameof(Player.ApplyDamageInfo));
        if (damage is null) Plugin.Report("Cheater godmode unavailable.");
        else harmony.Patch(damage, prefix: new HarmonyMethod(typeof(CheaterRoster), nameof(Damage)));

        var shot = AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.InitiateShot));
        if (shot is null) Plugin.Report("Cheater aimbot unavailable.");
        else harmony.Patch(shot, prefix: new HarmonyMethod(typeof(CheaterRoster), nameof(Aimbot)));

        var motion = AccessTools.Method(typeof(MovementContext), nameof(MovementContext.DirectApplyMotion));
        if (motion is null) Plugin.Report("Cheater speedhack unavailable.");
        else harmony.Patch(motion, prefix: new HarmonyMethod(typeof(CheaterRoster), nameof(Speedhack)), postfix: new HarmonyMethod(typeof(CheaterRoster), nameof(SpeedhackDone)));
    }

    internal static void RaidStarted()
    {
        accounts.Clear();
        rolled.Clear();
        nextLine.Clear();
        nextEsp.Clear();
        heat.Clear();
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
        if (debugLog?.Value == true) Plugin.Trace("[Cheater] " + message);
    }

    private static int Account(Player? player) => player?.Profile?.AccountId is { } id && int.TryParse(id, out int account) ? account : 0;

    internal static bool IsCheater(Player? player) => player is not null && accounts.Contains(Account(player));

    private static (CheaterType Type, Cheat Cheats) Roll(Player player)
    {
        int account = Account(player);
        if (rolled.TryGetValue(account, out var known)) return known;

        var type = forcedType?.Value is { } f and not CheaterType.Random ? f : RollType(account);
        var (always, may) = Kits[type];
        var cheats = always;
        int salt = 10;
        foreach (var (cheat, key) in Chances)
            if ((may & cheat) != 0 && Hash(account, salt++) < (play.Value<float?>(key) ?? 0))
                cheats |= cheat;
        // A closet cheater that rolled nothing still runs one of the two.
        if (type == CheaterType.Closet && cheats == Cheat.None) cheats = Hash(account, 30) < .5f ? Cheat.Aimbot : Cheat.Esp;
        rolled[account] = (type, cheats);
        Log($"{player.Profile.Nickname} is a {type} cheater with {cheats}.");
        return (type, cheats);
    }

    private static CheaterType RollType(int account)
    {
        var types = play["Types"] as JObject;
        (CheaterType Type, float Share)[] shares =
        [
            (CheaterType.Closet, types?.Value<float?>("Closet") ?? .4f), (CheaterType.Looter, types?.Value<float?>("Looter") ?? .25f),
            (CheaterType.Hunter, types?.Value<float?>("Hunter") ?? .2f), (CheaterType.Rage, types?.Value<float?>("Rage") ?? .1f),
            (CheaterType.Troll, types?.Value<float?>("Troll") ?? .05f)
        ];
        float total = shares.Sum(s => Math.Max(0, s.Share));
        if (total <= 0) return CheaterType.Closet;
        float roll = Hash(account, 1) * total;
        foreach (var (type, share) in shares)
            if ((roll -= Math.Max(0, share)) < 0)
                return type;
        return CheaterType.Closet;
    }

    internal static CheaterType TypeOf(Player player) => Roll(player).Type;

    internal static Cheat CheatsOf(Player player) => Roll(player).Cheats | (forced?.Value ?? Cheat.None);

    internal static bool Has(Player player, Cheat cheat) => (CheatsOf(player) & cheat) != 0;

    internal static void Suspect(Player player, float amount)
    {
        int account = Account(player);
        heat[account] = heat.TryGetValue(account, out float h) ? h + amount : amount;
    }

    // Set by the brain while it drives the bot, so ESP reports only go to bots on their normal AI.
    internal static void SetDriven(Player bot, bool on)
    {
        if (on) driven.Add(bot);
        else driven.Remove(bot);
    }

    // Every shot from this bot goes to the target until cleared.
    internal static void Aim(Player bot, Player? target)
    {
        if (target is null) aims.Remove(bot);
        else aims[bot] = target;
    }

    private static float Hash(int account, int salt) => (uint)((account * 2654435761u) ^ (uint)(salt * 40503)) % 10000 / 10000f;

    // Hits feed the anticheat: a cheater landing headshots, or one that soaks bullets with godmode.
    private static bool Damage(Player __instance, DamageInfo damageInfo, EBodyPart bodyPartType)
    {
        if (!Behaviour || accounts.Count == 0) return true;
        if (damageInfo.Player?.iPlayer is Player shooter && shooter != __instance && IsCheater(shooter)) Suspect(shooter, bodyPartType == EBodyPart.Head ? .35f : .12f);
        if (!IsCheater(__instance) || !Has(__instance, Cheat.Godmode)) return true;
        Suspect(__instance, .3f);
        return false;
    }

    // Only a shot with a clear line gets bent onto the target, so an aimbot never shoots through walls.
    private static void Aimbot(Player.FirearmController __instance, Vector3 shotPosition, ref Vector3 shotDirection)
    {
        if (!behaviour || accounts.Count == 0 || __instance._player is not { IsAI: true } shooter || !IsCheater(shooter) || !Has(shooter, Cheat.Aimbot)) return;
        if (!aims.TryGetValue(shooter, out var target)) target = shooter.AIData?.BotOwner?.Memory?.GoalEnemy?.Person as Player;
        if (target == null || target.HealthController?.IsAlive != true) return;

        bool closet = TypeOf(shooter) == CheaterType.Closet;
        // A closet cheater lets a few shots go wide and aims mostly at the body, so it doesn't stand out.
        if (closet && UnityEngine.Random.value < .15f) return;
        var bone = closet && UnityEngine.Random.value < .7f ? target.PlayerBones.Ribcage : target.PlayerBones.Head;
        var point = bone.position;
        if (Physics.Linecast(shotPosition, point, LayersMaskController.HighPolyWithTerrainMask)) return;
        shotDirection = (point - shotPosition).normalized;
    }

    private static void Speedhack(MovementContext __instance, ref Vector3 motion, out float __state)
    {
        __state = float.NaN;
        if (speeders.Count == 0 || !speeders.Contains(contextPlayer(__instance))) return;
        float m = SpeedMultiplier;
        motion = new Vector3(motion.x * m, motion.y, motion.z * m);
        // The controller's own speed limit would clamp the boost away.
        __state = __instance.CharacterController.SpeedLimit;
        __instance.CharacterController.SpeedLimit = -1f;
    }

    private static void SpeedhackDone(MovementContext __instance, float __state)
    {
        if (!float.IsNaN(__state)) __instance.CharacterController.SpeedLimit = __state;
    }

    internal static void Tick()
    {
        if (!Singleton<GameWorld>.Instantiated) return;
        float now = Time.unscaledTime;
        if (now >= nextRefresh && !fetching)
        {
            nextRefresh = now + 20;
            _ = Refresh();
        }

        speeders.Clear();
        if (!behaviour) return;
        alive.Clear();
        foreach (var player in Singleton<GameWorld>.Instance.AllAlivePlayersList)
        {
            if (player is not { IsAI: true }) continue;
            // Generated bots spawn in random order, so the server's debug cheaters may never show up. This picks from those that do.
            if (picked < spawnedCheaters!.Value && player.Side is EPlayerSide.Usec or EPlayerSide.Bear && RaidActivity.IsPbtAccount(Account(player))
                && accounts.Add(Account(player)))
            {
                picked++;
                Log($"Debug: {player.Profile.Nickname} turned cheater on spawn.");
            }

            if (IsCheater(player)) alive.Add(player);
        }

        bool banRoll = now >= nextBanRoll;
        if (banRoll) nextBanRoll = now + 1;
        foreach (var player in alive)
        {
            if (Has(player, Cheat.VoiceSpam)) Spam(player, now);
            if (BrainInstalled && Has(player, Cheat.Speedhack))
            {
                speeders.Add(player);
                if (banRoll && player.Velocity.sqrMagnitude > 1) Suspect(player, .08f);
            }

            if (Has(player, Cheat.Esp) && !driven.Contains(player)) Esp(player, now);
            if (banRoll) MaybeBan(player);
        }
    }

    // A bot on its normal AI with ESP gets told where the nearest player is now and then, so it flanks and pushes like it can see through walls.
    private static void Esp(Player cheater, float now)
    {
        int account = Account(cheater);
        if (nextEsp.TryGetValue(account, out float next) && now < next || cheater.AIData?.BotOwner is not { BotsGroup: { } group } owner) return;
        nextEsp[account] = now + UnityEngine.Random.Range(5f, 10f);
        Player? found = null;
        float best = 150 * 150;
        foreach (var p in Singleton<GameWorld>.Instance.AllAlivePlayersList)
        {
            if (p is null || p == cheater || p.HealthController?.IsAlive != true || !string.IsNullOrEmpty(cheater.GroupId) && p.GroupId == cheater.GroupId) continue;
            float weight = !p.IsAI ? 1 / Math.Max(.05f, TargetsYou * 2) : IsCheater(p) ? 1 / Math.Max(.05f, Rivalry * 2) : 1;
            float d = (p.Position - cheater.Position).sqrMagnitude * weight;
            if (d < best)
            {
                best = d;
                found = p;
            }
        }

        if (found is null) return;
        try
        {
            if (!group.IsEnemy(found)) group.AddEnemy(found, EBotEnemyCause.addPlayer);
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
        if (chance <= 0 || UnityEngine.Random.value >= chance || player.ActiveHealthController is not { } health) return;
        Log($"{player.Profile.Nickname} got banned mid-raid at {suspicion:0.0} suspicion.");
        aims.Remove(player);
        health.Kill(EDamageType.Undefined);
        if (play.Value<bool?>("BanNotice") ?? true)
        {
            try
            {
                NotificationManager.DisplayMessageNotification($"{player.Profile.Nickname} was banned for cheating.", ENotificationDurationType.Long, ENotificationIconType.Alert);
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
            await RequestHandler.PostJsonAsync("/pbt/raid/banned", new JObject { ["AccountId"] = account }.ToString(Newtonsoft.Json.Formatting.None));
        }
        catch (Exception ex)
        {
            Log("Ban report failed: " + ex.Message);
        }
    }

    // "Scav ahead" or "Hold fire" nonstop, lines piling over each other like a voice-line macro.
    private static void Spam(Player player, float now)
    {
        int account = Account(player);
        if (nextLine.TryGetValue(account, out float next) && now < next || player.Speaker is null) return;
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
            if (response.Value<int>("ProtocolVersion") != 1) return;
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
