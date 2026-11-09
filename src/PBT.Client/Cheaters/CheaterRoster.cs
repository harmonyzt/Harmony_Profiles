using BepInEx.Configuration;
using Comfort.Common;
using EFT;
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
    Hitman
}

// Which bots in this raid are cheaters and what each one does. The movement itself lives in CheaterBrain, which needs BigBrain.
internal static class CheaterRoster
{
    private static ConfigEntry<bool>? debugLog;
    private static ConfigEntry<CheaterMode>? forced;
    private static readonly HashSet<int> accounts = new();
    private static readonly Dictionary<int, CheaterMode> modes = new();
    private static readonly Dictionary<int, float> nextLine = new();
    private static bool behaviour, fetching;
    private static float nextRefresh;
    internal static bool BrainInstalled;

    internal static bool Behaviour => behaviour && BrainInstalled;

    internal static void Install(Harmony harmony, ConfigFile config)
    {
        debugLog = config.Bind("Cheaters (debug)", "Log cheater behaviour", false, "Write what each cheater bot is doing to the BepInEx console.");
        forced = config.Bind("Cheaters (debug)", "Force behaviour", CheaterMode.Random,
            "Every cheater uses this behaviour instead of a random one. Spawn cheaters with the server's \"Debug: cheaters per raid\" setting.");
        var damage = AccessTools.Method(typeof(Player), nameof(Player.ApplyDamageInfo));
        if (damage is null) Plugin.Report("Cheater invincibility unavailable.");
        else harmony.Patch(damage, prefix: new HarmonyMethod(typeof(CheaterRoster), nameof(Damage)));
    }

    internal static void RaidStarted()
    {
        accounts.Clear();
        modes.Clear();
        nextLine.Clear();
        nextRefresh = 0;
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
        // Most cheaters vacuum the map. The rest hunt you, stalk you or clear everyone else.
        float roll = Hash(account, 1);
        mode = roll < .4f ? CheaterMode.Vacuum : roll < .6f ? CheaterMode.Stalker : roll < .85f ? CheaterMode.Hunter : CheaterMode.Hitman;
        modes[account] = mode;
        return mode;
    }

    internal static void SetMode(Player player, CheaterMode mode) => modes[Account(player)] = mode;

    private static float Hash(int account, int salt) => (uint)((account * 2654435761u) ^ (uint)(salt * 40503)) % 10000 / 10000f;

    // Teleporting cheaters can't be hurt. Vacuumers can, so a lucky player still gets their backpack.
    private static bool Damage(Player __instance) => !(Behaviour && IsCheater(__instance) && ModeOf(__instance) != CheaterMode.Vacuum);

    internal static void Tick()
    {
        if (!Singleton<GameWorld>.Instantiated) return;
        float now = Time.unscaledTime;
        if (now >= nextRefresh && !fetching)
        {
            nextRefresh = now + 20;
            _ = Refresh();
        }

        if (!behaviour || accounts.Count == 0) return;
        foreach (var player in Singleton<GameWorld>.Instance.AllAlivePlayersList)
            if (player is { IsAI: true } && IsCheater(player))
                Spam(player, now);
    }

    // Some spam "Scav ahead" or "Hold fire" forever, the worst without waiting for the last line to finish.
    private static void Spam(Player player, float now)
    {
        int account = Account(player);
        if (Hash(account, 2) >= .4f || nextLine.TryGetValue(account, out float next) && now < next || player.Speaker is null) return;
        bool overlap = Hash(account, 3) < .5f;
        if (!overlap && player.Speaker.Busy) return;
        var phrase = Hash(account, 4) < .5f ? EPhraseTrigger.Scav : EPhraseTrigger.HoldFire;
        player.Speaker.Play(phrase, player.HealthStatus | ETagStatus.Combat, true, overlap ? 1000 : null);
        nextLine[account] = now + (overlap ? UnityEngine.Random.Range(.35f, .9f) : UnityEngine.Random.Range(.3f, 1.5f));
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
