using Comfort.Common;
using EFT;
using EFT.UI;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace PBT.Client.UI;

// Ping and packet loss from the server matching put you on
// Shown by the game's own RTT and loss icons
internal static class Connection
{
    private const float Step = .5f;

    // Off on Fika clients
    internal static bool Allowed = true;
    internal static bool Fika;
    private static string server = "";
    private static bool simulate, lossEvents, bad, rubberBand;
    private static int ping, jitter;
    private static double loss;
    private static float next, fightUntil, startedAt = -1, dropAt = -1;
    private static int spike;
    private static bool touched, dropped;

    // Where you were over the last few seconds, for freezing and snapping back.
    private static readonly Queue<(float Time, Vector3 Position)> history = new();
    private static float nextSample;
    private static bool frozen, hitchDone;
    private static Vector3 frozenAt;
    private static Vector2 frozenLook;
    private static readonly List<float> snaps = new();
    private static float lockUntil, nextYank;
    private static Vector3 anchor;

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
        rubberBand = s.Value<bool?>("RubberBand") == true;
        
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
                ResetMovement();
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

        if (startedAt < 0)
        {
            startedAt = now;
            // Live raids often hitch right after the countdown.
            if (rubberBand && !hitchDone && UnityEngine.Random.value < .6f)
            {
                hitchDone = true;
                int count = UnityEngine.Random.Range(1, 4);
                for (int i = 0; i < count; i++) snaps.Add(now + UnityEngine.Random.Range(.3f, 6f));
            }
        }

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
                Freeze(me);
                Plugin.Trace($"Connection to {server} is dropping.");
            }
        }

        if (rubberBand && lost > 0) RubberBand(now, lost);
        Set(Math.Max(20, Math.Round(rtt)), lost, lost >= 15);
    }

    // Light loss snaps you back once, sometimes a moment late. Heavy loss keeps yanking you back to one spot for a while.
    private static void RubberBand(float now, int lost)
    {
        if (lost >= 15 && now >= lockUntil && UnityEngine.Random.value < .3f)
        {
            lockUntil = now + UnityEngine.Random.Range(1f, 3f);
            anchor = Past(now, UnityEngine.Random.Range(.3f, .8f));
            nextYank = now;
            return;
        }

        if (UnityEngine.Random.value < .35f) snaps.Add(now + UnityEngine.Random.Range(0f, 1.2f));
    }

    private static Vector3 Past(float now, float seconds)
    {
        Vector3 result = history.Count > 0 ? history.Peek().Position : Vector3.zero;
        foreach (var (time, position) in history)
        {
            if (time > now - seconds) break;
            result = position;
        }

        return result;
    }

    private static void ResetMovement()
    {
        history.Clear();
        snaps.Clear();
        frozen = false;
        hitchDone = false;
        lockUntil = 0;
    }

    internal static void Frame()
    {
        if (!Allowed || Fika || !simulate || server.Length == 0 || !Singleton<GameWorld>.Instantiated) return;
        var me = Singleton<GameWorld>.Instance.MainPlayer;
        if (me is null || me.HealthController?.IsAlive != true) return;
        float now = Time.unscaledTime;
        if (frozen)
        {
            me.Teleport(frozenAt);
            me.Rotation = frozenLook;
            return;
        }

        if (now >= nextSample)
        {
            nextSample = now + .05f;
            history.Enqueue((now, me.Position));
            while (history.Count > 0 && history.Peek().Time < now - 3f) history.Dequeue();
        }

        if (!rubberBand) return;
        for (int i = snaps.Count - 1; i >= 0; i--)
        {
            if (snaps[i] > now) continue;
            snaps.RemoveAt(i);
            me.Teleport(Past(now, UnityEngine.Random.Range(.25f, .7f)));
        }

        if (now < lockUntil && now >= nextYank)
        {
            nextYank = now + UnityEngine.Random.Range(.12f, .3f);
            me.Teleport(anchor);
        }
    }

    // The connection is gone: you stop where the server last saw you, sometimes a step back, and the view stops too.
    private static void Freeze(Player me)
    {
        float now = Time.unscaledTime;
        frozenAt = UnityEngine.Random.value < .5f ? Past(now, UnityEngine.Random.Range(.4f, 1.2f)) : me.Position;
        frozenLook = me.Rotation;
        frozen = true;
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
