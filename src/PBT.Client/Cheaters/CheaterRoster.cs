using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.HealthSystem;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using UnityEngine;

namespace PBT.Client.Cheaters;

internal enum CheaterMode
{
    Random,
    Vacuum,
    Stalker,
    Hunter,
    Hitman,
    Spinbot,
    Jumpshot
}

// Which bots in this raid are cheaters and what each one does. The movement itself lives in CheaterBrain, which needs BigBrain.
internal static class CheaterRoster
{
    private static ConfigEntry<bool>? debugLog, forceSpeed;
    private static ConfigEntry<CheaterMode>? forced;
    private static ConfigEntry<float>? speedMultiplier, banPerMinute;
    private static ConfigEntry<int>? spawnedCheaters;
    private static int picked;
    private static readonly HashSet<int> accounts = new();
    private static readonly Dictionary<int, CheaterMode> modes = new();
    private static readonly Dictionary<int, float> nextLine = new();
    private static readonly HashSet<Player> speeders = new();
    private static readonly Dictionary<Player, Player> aims = new();
    private static readonly List<Player> alive = new();
    private static readonly AccessTools.FieldRef<MovementContext, Player> contextPlayer = AccessTools.FieldRefAccess<MovementContext, Player>("_player");
    private static bool behaviour, fetching;
    private static float nextRefresh, nextBanRoll;
    internal static bool BrainInstalled;

    internal static bool Behaviour => behaviour && BrainInstalled;

    internal static void Install(Harmony harmony, ConfigFile config)
    {
        debugLog = config.Bind("Cheaters (debug)", "Log cheater behaviour", false, "Write what each cheater bot is doing to the BepInEx console.");
        forced = config.Bind("Cheaters (debug)", "Force behaviour", CheaterMode.Random,
            "Every cheater uses this behaviour instead of a random one. Spawn cheaters with the server's \"Debug: cheaters per raid\" setting.");
        spawnedCheaters = config.Bind("Cheaters (debug)", "First spawned PMCs that cheat", 0,
            new ConfigDescription("Turns this many of the first PBT PMCs that actually spawn into cheaters, so you meet one quickly. They keep their normal kit. 0 is off.", new AcceptableValueRange<int>(0, 10)));
        forceSpeed = config.Bind("Cheaters (debug)", "Force speedhack", false, "Every cheater speedhacks, not just some.");
        speedMultiplier = config.Bind("Cheaters (debug)", "Speedhack multiplier", 2.2f,
            new ConfigDescription("How much faster a speedhacking cheater moves.", new AcceptableValueRange<float>(1f, 6f)));
        banPerMinute = config.Bind("Cheaters (debug)", "Ban chance per minute", .02f,
            new ConfigDescription("Chance each minute that a cheater drops dead mid-raid, as if banned. Three times as likely while it is shooting.", new AcceptableValueRange<float>(0f, 1f)));

        var damage = AccessTools.Method(typeof(Player), nameof(Player.ApplyDamageInfo));
        if (damage is null) Plugin.Report("Cheater invincibility unavailable.");
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
        modes.Clear();
        nextLine.Clear();
        speeders.Clear();
        aims.Clear();
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

    internal static CheaterMode ModeOf(Player player)
    {
        int account = Account(player);
        if (forced?.Value is { } f and not CheaterMode.Random) return f;
        if (modes.TryGetValue(account, out var mode)) return mode;
        // Most cheaters vacuum the map. The rest come for you or clear everyone else.
        float roll = Hash(account, 1);
        mode = roll < .3f ? CheaterMode.Vacuum : roll < .45f ? CheaterMode.Stalker : roll < .63f ? CheaterMode.Hunter
            : roll < .73f ? CheaterMode.Hitman : roll < .87f ? CheaterMode.Spinbot : CheaterMode.Jumpshot;
        modes[account] = mode;
        return mode;
    }

    internal static void SetMode(Player player, CheaterMode mode) => modes[Account(player)] = mode;

    internal static bool Speeds(Player player) => forceSpeed?.Value == true || Hash(Account(player), 5) < .35f;

    // Every shot from this bot goes to the target's head until cleared.
    internal static void Aim(Player bot, Player? target)
    {
        if (target is null) aims.Remove(bot);
        else aims[bot] = target;
    }

    private static float Hash(int account, int salt) => (uint)((account * 2654435761u) ^ (uint)(salt * 40503)) % 10000 / 10000f;

    // Teleporters can't be hurt. Vacuumers, spinbotters and jumpshotters can, so a lucky player still gets their backpack.
    private static bool Damage(Player __instance) =>
        !(Behaviour && IsCheater(__instance) && ModeOf(__instance) is CheaterMode.Stalker or CheaterMode.Hunter or CheaterMode.Hitman);

    private static void Aimbot(Player.FirearmController __instance, Vector3 shotPosition, ref Vector3 shotDirection)
    {
        if (aims.Count == 0 || __instance._player is not { } shooter || !aims.TryGetValue(shooter, out var target)) return;
        if (target == null || target.HealthController?.IsAlive != true) return;
        shotDirection = (target.PlayerBones.Head.position - shotPosition).normalized;
    }

    private static void Speedhack(MovementContext __instance, ref Vector3 motion, out float __state)
    {
        __state = float.NaN;
        if (speeders.Count == 0 || !speeders.Contains(contextPlayer(__instance))) return;
        float m = speedMultiplier!.Value;
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
            if (picked < spawnedCheaters!.Value && player.Side is EPlayerSide.Usec or EPlayerSide.Bear && Account(player) >= 1500000000 && accounts.Add(Account(player)))
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
            Spam(player, now);
            if (Behaviour && Speeds(player)) speeders.Add(player);
            if (banRoll) MaybeBan(player);
        }
    }

    // Like a live ban wave: the cheater just drops dead, wherever it is.
    private static void MaybeBan(Player player)
    {
        float chance = banPerMinute!.Value / 60f * (aims.ContainsKey(player) ? 3 : 1);
        if (chance <= 0 || UnityEngine.Random.value >= chance || player.ActiveHealthController is not { } health) return;
        Log($"{player.Profile.Nickname} got banned mid-raid ({ModeOf(player)}{(aims.ContainsKey(player) ? ", mid-fight" : "")}).");
        aims.Remove(player);
        health.Kill(EDamageType.Undefined);
    }

    // Some spam "Scav ahead" or "Hold fire" nonstop, lines piling over each other like a voice-line macro.
    private static void Spam(Player player, float now)
    {
        int account = Account(player);
        if (Hash(account, 2) >= .5f || nextLine.TryGetValue(account, out float next) && now < next || player.Speaker is null) return;
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
