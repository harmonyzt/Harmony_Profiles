using Comfort.Common;
using EFT;
using EFT.UI;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace PBT.Client.UI;

// Ping and packet loss from the server matching put you on, shown by the game's own RTT and loss icons.
internal static class Connection
{
    private const float Step = .5f;

    // Off on Fika clients (they show their real ping to the host) and on the headless.
    internal static bool Allowed = true;
    internal static bool Fika;
    private static string server = "";
    private static bool simulate, lossEvents, bad;
    private static int ping, jitter;
    private static double loss;
    private static float next, fightUntil, startedAt = -1, dropAt = -1;
    private static int spike;
    private static bool touched, dropped;

    internal static void Install(Harmony harmony)
    {
        var shot = AccessTools.Method(typeof(Player), nameof(Player.OnMakingShot));
        if (shot is null)
        {
            Plugin.Report("Connection fight detection unavailable.");
            return;
        }

        harmony.Patch(shot, postfix: new HarmonyMethod(typeof(Connection), nameof(Shot)));
    }

    internal static void Matched(JToken? info)
    {
        server = "";
        if (info is not JObject s || s.Value<string>("Name") is not { Length: > 0 } name) return;
        server = name;
        ping = s.Value<int>("Ping");
        jitter = s.Value<int>("Jitter");
        loss = s.Value<double>("Loss");
        bad = s.Value<bool>("Bad");
        simulate = s.Value<bool>("Simulate");
        lossEvents = s.Value<bool>("LossEvents");
        
        Plugin.Trace($"Matched into {name} ({s.Value<string>("Region")}), ~{ping} ms{(bad ? ", bad server" : "")}.");
    }

    // Gunfire near you is when a bad server drops packet
    private static void Shot(Player __instance)
    {
        if (!bad || !Singleton<GameWorld>.Instantiated) return;
        var me = Singleton<GameWorld>.Instance.MainPlayer;
        if (me is null) return;
        if (__instance == me || (__instance.Position - me.Position).sqrMagnitude < 100f * 100f) fightUntil = Time.unscaledTime + 4f;
    }

    internal static void Tick()
    {
        float now = Time.unscaledTime;
        if (now < next) return;
        next = now + Step;
#pragma warning disable CS0618
        bool inRaid = InGameStatus.InRaid;
#pragma warning restore CS0618
        var game = Singleton<AbstractGame>.Instantiated ? Singleton<AbstractGame>.Instance : null;
        var me = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer : null;
        
        if (!Allowed || !simulate || server.Length == 0 || !inRaid || game is null || me is null)
        {
            if (!inRaid)
            {
                startedAt = -1;
                dropAt = -1;
                dropped = false;
                spike = 0;
            }

            if (touched) Set(0, 0, false);
            return;
        }

        var random = UnityEngine.Random.value;
        if (game.Status is GameStatus.Stopping or GameStatus.SoftStopping or GameStatus.Stopped)
        {
            if (touched) Set(0, 0, false);
            return;
        }

        // EFT always spikes while the countdown runs
        if (game.Status != GameStatus.Started)
        {
            spike = UnityEngine.Random.Range(170, 251);
            Set(spike, 0, false);
            return;
        }

        if (startedAt < 0) startedAt = now;
        if (dropAt >= 0)
        {
            if (now >= dropAt)
            {
                dropAt = -1;
                Drop(game, me);
            }
            else Set(UnityEngine.Random.Range(900, 2501), UnityEngine.Random.Range(40, 101), true);

            return;
        }

        double rtt = ping + (UnityEngine.Random.value * 2 - 1) * jitter;
        if (random < .03f) rtt += UnityEngine.Random.Range(30, 91);
        float settle = now - startedAt;
        if (spike > 0 && settle < 4f) rtt = Mathf.Lerp(spike, (float)rtt, settle / 4f);
        int lost = 0;
        if (bad && now < fightUntil)
        {
            rtt += UnityEngine.Random.Range(15, 61);
            if (UnityEngine.Random.value < loss) lost = UnityEngine.Random.Range(3, 26);
            if (lossEvents && !Fika && !dropped && settle > 180f && UnityEngine.Random.value < .002 * loss)
            {
                dropped = true;
                dropAt = now + UnityEngine.Random.Range(3f, 6f);
                Plugin.Trace($"Connection to {server} is dropping.");
            }
        }

        Set(Math.Max(20, Math.Round(rtt)), lost, lost >= 15);
    }

    private static void Set(double rtt, int lossPercent, bool lags)
    {
        touched = rtt > 0;
        NetworkGameSession.Rtt = rtt;
        NetworkGameSession.LossPercent = lossPercent;
        if (lags) NetworkGameSession.ClientServerConnectionLags = true;
    }

    // Closing the message or waiting it out both end the raid, like a real disconnect.
    private static async void Drop(AbstractGame game, Player me)
    {
        try
        {
            Set(0, 0, false);
            string profile = me.ProfileId;
            if (!MonoBehaviourSingleton<PreloaderUI>.Instantiated)
            {
                End(game, profile);
                return;
            }

            var window = MonoBehaviourSingleton<PreloaderUI>.Instance.ShowCriticalErrorScreen("Server connection lost", $"Connection to the game server ({server}) was lost.",
                ErrorScreen.EButtonType.OkButton, 30f);

            // A window that never opened has no result, so the timer still ends the raid.
            await Task.WhenAny(window.WindowResult, Task.Delay(32000));
            End(game, profile);
        }
        catch (Exception ex)
        {
            Plugin.Report("Connection lost event failed: " + ex.Message);
        }
    }

    private static void End(AbstractGame game, string profile)
    {
        if (game.Status != GameStatus.Started || game is not BaseLocalGame<EftGamePlayerOwner> local)
        {
            return;
        }

        Plugin.Trace("Server connection lost, leaving the raid as missing in action.");

        local.Stop(profile, ExitStatus.MissingInAction, null);
    }
}
