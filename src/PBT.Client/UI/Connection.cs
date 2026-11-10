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
    private const float Step = 1f;
    private const float BotRange = 150f;

    // Off on Fika clients
    internal static bool Allowed = true;
    internal static bool Fika;
    private static string server = "";
    private static bool simulate,
        lossEvents,
        bad,
        rubberBand,
        rubberBandBots;
    private static int ping,
        jitter;
    private static double loss;
    private static float next,
        fightUntil,
        startedAt = -1,
        dropAt = -1;
    private static int spike;
    private static bool touched,
        dropped;

    private static readonly Trail mine = new();
    private static readonly Dictionary<Player, Trail> bots = new();
    private static bool frozen,
        hitchDone;
    private static Vector3 frozenAt;
    private static Vector2 frozenLook;

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
        if (info is not JObject s || s.Value<string>("Name") is not { Length: > 0 } name)
            return;
        server = name;
        ping = s.Value<int>("Ping");
        jitter = s.Value<int>("Jitter");
        loss = s.Value<double>("Loss");
        bad = s.Value<bool>("Bad");
        simulate = s.Value<bool>("Simulate");
        lossEvents = s.Value<bool>("LossEvents");
        rubberBand = s.Value<bool?>("RubberBand") == true;
        rubberBandBots = rubberBand && s.Value<bool?>("RubberBandBots") == true;

        Plugin.Trace($"Matched into {name} ({s.Value<string>("Region")}), ~{ping} ms{(bad ? ", bad server" : "")}.");
    }

    // Gunfire near you is when a bad server drops packet
    private static void Shot(Player __instance)
    {
        if (!bad || !Singleton<GameWorld>.Instantiated)
            return;
        var me = Singleton<GameWorld>.Instance.MainPlayer;
        if (me is null)
            return;
        if (__instance == me || (__instance.Position - me.Position).sqrMagnitude < 100f * 100f)
            fightUntil = Time.unscaledTime + 4f;
    }

    internal static void Tick()
    {
        float now = Time.unscaledTime;
        if (now < next)
            return;
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

            if (touched)
                Set(0, 0, false);
            return;
        }

        var random = UnityEngine.Random.value;
        if (game.Status is GameStatus.Stopping or GameStatus.SoftStopping or GameStatus.Stopped)
        {
            if (touched)
                Set(0, 0, false);
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
                for (int i = 0; i < count; i++)
                    mine.Snap(now + UnityEngine.Random.Range(.3f, 6f));
            }
        }

        if (dropAt >= 0)
        {
            if (now >= dropAt)
            {
                dropAt = -1;
                Drop(game, me);
            }
            else
                Set(UnityEngine.Random.Range(900, 2501), UnityEngine.Random.Range(40, 101), true);

            return;
        }

        double rtt = ping + (UnityEngine.Random.value * 2 - 1) * jitter;
        if (random < .03f)
            rtt += UnityEngine.Random.Range(30, 91);
        float settle = now - startedAt;
        if (spike > 0 && settle < 4f)
            rtt = Mathf.Lerp(spike, (float)rtt, settle / 4f);
        int lost = 0;
        if (bad && now < fightUntil)
        {
            rtt += UnityEngine.Random.Range(15, 61);
            if (UnityEngine.Random.value < loss)
                lost = UnityEngine.Random.Range(3, 26);
            if (lossEvents && !Fika && !dropped && settle > 180f && UnityEngine.Random.value < .004 * loss)
            {
                dropped = true;
                dropAt = now + UnityEngine.Random.Range(3f, 6f);
                Freeze(me);
                Plugin.Trace($"Connection to {server} is dropping.");
            }
        }
        // Bad servers also drop packets in quiet moments, just less and lighter.
        else if (bad && UnityEngine.Random.value < loss * .25)
            lost = UnityEngine.Random.Range(2, 12);

        if (rubberBand && lost > 0)
        {
            mine.Band(now, lost);
            if (rubberBandBots)
                foreach (var trail in bots.Values)
                    trail.Band(now, lost);
        }

        PruneBots();
        Set(Math.Max(20, Math.Round(rtt)), lost, lost >= 15);
    }

    private static void ResetMovement()
    {
        mine.Clear();
        bots.Clear();
        frozen = false;
        hitchDone = false;
    }

    private static void PruneBots()
    {
        if (bots.Count == 0)
            return;
        List<Player>? gone = null;
        foreach (var bot in bots.Keys)
            if (bot == null || bot.HealthController?.IsAlive != true)
                (gone ??= new()).Add(bot!);
        if (gone is not null)
            foreach (var bot in gone)
                bots.Remove(bot);
    }

    internal static void Frame()
    {
        if (!Allowed || Fika || !simulate || server.Length == 0 || !Singleton<GameWorld>.Instantiated)
            return;
        var world = Singleton<GameWorld>.Instance;
        var me = world.MainPlayer;
        if (me is null || me.HealthController?.IsAlive != true)
            return;
        float now = Time.unscaledTime;
        if (frozen)
        {
            me.Teleport(frozenAt);
            me.Rotation = frozenLook;
            return;
        }

        mine.Sample(now, me.Position);
        if (!rubberBand)
            return;
        mine.Apply(me, now);
        if (rubberBandBots)
            Bots(world, me.Position, now);
    }

    private static void Bots(GameWorld world, Vector3 center, float now)
    {
        var players = world.RegisteredPlayers;
        if (players is null)
            return;
        foreach (var player in players)
        {
            if (player is not Player { IsAI: true } bot || bot.HealthController?.IsAlive != true)
                continue;
            // A trail that stopped sampling out of range would snap the bot somewhere stale.
            if ((bot.Position - center).sqrMagnitude > BotRange * BotRange)
            {
                bots.Remove(bot);
                continue;
            }

            if (!bots.TryGetValue(bot, out var trail))
                bots[bot] = trail = new Trail();
            trail.Sample(now, bot.Position);
            trail.Apply(bot, now);
        }
    }

    // The connection is gone: you stop where the server last saw you, sometimes a step back, and the view stops too.
    private static void Freeze(Player me)
    {
        float now = Time.unscaledTime;
        frozenAt =
            UnityEngine.Random.value < .5f ? mine.Past(now, UnityEngine.Random.Range(.4f, 1.2f)) ?? me.Position : me.Position;
        frozenLook = me.Rotation;
        frozen = true;
    }

    private static void Set(double rtt, int lossPercent, bool lags)
    {
        touched = rtt > 0;
        NetworkGameSession.Rtt = rtt;
        NetworkGameSession.LossPercent = lossPercent;
        if (lags)
            NetworkGameSession.ClientServerConnectionLags = true;
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

            var window = MonoBehaviourSingleton<PreloaderUI>.Instance.ShowCriticalErrorScreen(
                "Server connection lost",
                $"Connection to the game server ({server}) was lost.",
                ErrorScreen.EButtonType.OkButton,
                30f
            );

            // A window that never opened has no result, so the timer still ends the raid.
            await Task.WhenAny(window.WindowResult, Task.Delay(32000));
            End(game, profile);
        }
        catch (Exception ex)
        {
            // Never leave the player frozen in a raid that keeps running.
            frozen = false;
            Plugin.Report("Connection lost event failed: " + ex.Message);
        }
    }

    private static void End(AbstractGame game, string profile)
    {
        if (game.Status != GameStatus.Started || game is not BaseLocalGame<EftGamePlayerOwner> local)
        {
            frozen = false;
            return;
        }

        Plugin.Trace("Server connection lost, leaving the raid as missing in action.");

        local.Stop(profile, ExitStatus.MissingInAction, null);
    }

    // Where one player was over the last few seconds, for snapping them back.
    private sealed class Trail
    {
        private readonly Queue<(float Time, Vector3 Position)> history = new();
        private readonly List<float> snaps = new();
        private float nextSample,
            lockUntil,
            nextYank;
        private Vector3 anchor;

        internal void Sample(float now, Vector3 position)
        {
            if (now < nextSample)
                return;
            nextSample = now + .05f;
            history.Enqueue((now, position));
            while (history.Count > 0 && history.Peek().Time < now - 3f)
                history.Dequeue();
        }

        internal void Snap(float at) => snaps.Add(at);

        // Light loss snaps back once, sometimes a moment late. Heavy loss keeps yanking back to one spot for a while.
        internal void Band(float now, int lost)
        {
            if (
                lost >= 15
                && now >= lockUntil
                && UnityEngine.Random.value < .3f
                && Past(now, UnityEngine.Random.Range(.3f, .8f)) is { } spot
            )
            {
                lockUntil = now + UnityEngine.Random.Range(1f, 3f);
                anchor = spot;
                nextYank = now;
                return;
            }

            if (UnityEngine.Random.value < .35f)
                snaps.Add(now + UnityEngine.Random.Range(0f, 1.2f));
        }

        internal void Apply(Player player, float now)
        {
            for (int i = snaps.Count - 1; i >= 0; i--)
            {
                if (snaps[i] > now)
                    continue;
                snaps.RemoveAt(i);
                if (Past(now, UnityEngine.Random.Range(.25f, .7f)) is { } spot)
                    player.Teleport(spot);
            }

            if (now < lockUntil && now >= nextYank)
            {
                nextYank = now + UnityEngine.Random.Range(.12f, .3f);
                player.Teleport(anchor);
            }
        }

        internal Vector3? Past(float now, float seconds)
        {
            if (history.Count == 0)
                return null;
            Vector3 result = history.Peek().Position;
            foreach (var (time, position) in history)
            {
                if (time > now - seconds)
                    break;
                result = position;
            }

            return result;
        }

        internal void Clear()
        {
            history.Clear();
            snaps.Clear();
            lockUntil = 0;
        }
    }
}
